using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using MandanHeat.Services;

namespace MandanHeat;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        SessionEnding += (_, _) => IsShuttingDown = true;

        // MandanHeat.exe --dump [ruta]  → vuelca todos los sensores a un archivo de texto y sale.
        int dumpIdx = Array.IndexOf(e.Args, "--dump");
        if (dumpIdx >= 0)
        {
            var path = dumpIdx + 1 < e.Args.Length ? e.Args[dumpIdx + 1] : Path.Combine(AppContext.BaseDirectory, "sensores.txt");
            RunDump(path);
            Shutdown();
            return;
        }

        // MandanHeat.exe --fps-test [ruta] → mide 6 s con PresentMon y guarda los FPS de cada proceso.
        int fpsIdx = Array.IndexOf(e.Args, "--fps-test");
        if (fpsIdx >= 0)
        {
            var path = fpsIdx + 1 < e.Args.Length ? e.Args[fpsIdx + 1] : Path.Combine(AppContext.BaseDirectory, "fps-test.txt");
            RunFpsTest(path);
            Shutdown();
            return;
        }

        // --fps-parse-test entrada salida → alimenta el parser con un CSV de PresentMon (pruebas).
        int parseIdx = Array.IndexOf(e.Args, "--fps-parse-test");
        if (parseIdx >= 0 && parseIdx + 2 < e.Args.Length)
        {
            using var fps = new FpsMonitor();
            foreach (var line in File.ReadLines(e.Args[parseIdx + 1])) fps.FeedLine(line);
            var sb = new StringBuilder();
            foreach (var (pid, st) in fps.Snapshot().OrderBy(x => x.Pid))
                sb.AppendLine($"pid {pid} {st.App} fps={st.Fps:0.0} ft={st.FrameTimeMs:0.00} low={st.Low1Percent:0.0}");
            File.WriteAllText(e.Args[parseIdx + 2], sb.ToString());
            Shutdown();
            return;
        }

        // --log-test carpeta → registra 4 filas reales y escribe el resumen (pruebas).
        int logIdx = Array.IndexOf(e.Args, "--log-test");
        if (logIdx >= 0 && logIdx + 1 < e.Args.Length)
        {
            var settings = new AppSettings();
            using var hw = new HardwareService();
            hw.Open();
            var sensors = hw.AllHardware().SelectMany(h => h.Sensors
                .Where(SensorNames.IsPlausible)
                .Select(s => (s, SensorNames.Display(s, settings), SensorNames.HardwareName(h)))).ToList();
            using var logger = new MetricsLogger();
            logger.Start(e.Args[logIdx + 1], "Prueba", sensors, excelFormat: false, automatic: false);
            var rnd = new Random(1);
            var frames = new List<float>();
            for (int i = 0; i < 4; i++)
            {
                hw.Update();
                logger.WriteRow(new FrameStats("Prueba.exe", 120 + i, 8.3, 95));
                for (int f = 0; f < 120; f++) frames.Add(f % 100 == 0 ? 25f : 8.3f + (float)rnd.NextDouble());
                Thread.Sleep(1000);
            }
            logger.Stop(frames, true, new List<(string, LibreHardwareMonitor.Hardware.ISensor?)>());
            Shutdown();
            return;
        }

        // --dev: instancia de pruebas (sin UAC, sin instancia única y sin atajos globales).
        IsDevInstance = e.Args.Contains("--dev");

        // Leer CPU/placa base y medir FPS requiere administrador: se pide UAC al arrancar.
        if (!SystemInfo.IsAdmin && !e.Args.Contains("--no-elevate") && !IsDevInstance)
        {
            if (SystemInfo.RelaunchElevated())
            {
                Shutdown();
                return;
            }
            // El usuario canceló el UAC: seguimos con lo que se pueda leer.
        }

        if (!IsDevInstance && !AcquireSingleInstance())
        {
            MessageBox.Show("MandanHeat ya está abierto (mira en la bandeja del sistema, junto al reloj).",
                "MandanHeat", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // --minimized: arranque desde "Iniciar con Windows", directamente a la bandeja.
        MainWindow = new MainWindow(startHidden: e.Args.Contains("--minimized"));
    }

    public static bool IsDevInstance { get; private set; }

    /// <summary>true cuando la app se cierra de verdad (no minimizar a la bandeja).</summary>
    public static bool IsShuttingDown { get; set; }

    private static void RunFpsTest(string path)
    {
        var sb = new StringBuilder();
        var exe = FpsMonitor.FindExecutable();
        sb.AppendLine($"PresentMon: {exe ?? "no encontrado"}   Administrador: {SystemInfo.IsAdmin}");
        if (exe != null)
        {
            using var fps = new FpsMonitor();
            var args = AppSettings.Load().PresentMonArguments.Replace("MandanHeatFPS", "MandanHeatFPSTest");
            sb.AppendLine($"Argumentos: {args}");
            fps.Start(exe, args);
            Thread.Sleep(6000);
            sb.AppendLine($"Estado: {fps.State} {fps.StatusMessage}");
            foreach (var (pid, st) in fps.Snapshot().OrderByDescending(x => x.Stats.Fps))
                sb.AppendLine($"  pid {pid,-7} {st.App,-32} {st.Fps,7:0.0} FPS   {st.FrameTimeMs,6:0.00} ms   1% low {st.Low1Percent,6:0.0}");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private bool AcquireSingleInstance()
    {
        try
        {
            _singleInstance = new Mutex(false, "MandanHeat_SingleInstance");
            // Margen para cuando nos reiniciamos como administrador y la instancia anterior aún se cierra.
            return _singleInstance.WaitOne(TimeSpan.FromSeconds(3));
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void RunDump(string path)
    {
        using var hw = new HardwareService();
        hw.Open();
        hw.Update();
        Thread.Sleep(1500); // algunos sensores (uso de CPU) necesitan dos lecturas
        hw.Update();
        File.WriteAllText(path, hw.Dump(), new UTF8Encoding(false));
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(Path.Combine(AppSettings.Folder, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
        }
        catch { }
        MessageBox.Show($"Error inesperado:\n{e.Exception.Message}\n\nDetalles en %AppData%\\MandanHeat\\error.log",
            "MandanHeat", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
