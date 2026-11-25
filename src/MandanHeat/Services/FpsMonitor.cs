using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MandanHeat.Interop;

namespace MandanHeat.Services;

public readonly record struct FrameStats(string App, double Fps, double FrameTimeMs, double Low1Percent, bool FromDriver = false);

public enum FpsState { NotFound, Stopped, Running, Error }

/// <summary>
/// Lanza PresentMon (Intel, MIT) en modo CSV por stdout y calcula FPS / frametime / 1 % low por proceso.
/// PresentMon usa ETW: funciona con DirectX 9-12, Vulkan y OpenGL sin inyectar nada en el juego.
/// Se usa el modo "v1" sin seguimiento de pantalla/GPU/entrada: 10 columnas por fotograma y sin retraso.
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    public const string DownloadUrl = "https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe";

    // Procesos del sistema que presentan fotogramas pero nunca son "el juego".
    private static readonly HashSet<string> IgnoredApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm.exe", "explorer.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe",
        "SearchHost.exe", "TextInputHost.exe", "LockApp.exe", "MandanHeat.exe", "<unknown>",
    };

    /// <summary>Búfer circular de frametimes de un swapchain.</summary>
    private sealed class Chain
    {
        public const int Capacity = 4096; // ~10 s a 400 FPS
        public readonly float[] Frames = new float[Capacity];
        public int Count, Head;
        public long LastSeen;
        public string App = "";

        public void Add(float ms)
        {
            Frames[Head] = ms;
            Head = (Head + 1) % Capacity;
            if (Count < Capacity) Count++;
        }

        /// <summary>i = 0 es el fotograma más reciente.</summary>
        public float Recent(int i) => Frames[(Head - 1 - i + Capacity * 2) % Capacity];
    }

    private sealed class PidInfo
    {
        public string App = "";
        public bool Ignored;
    }

    private readonly object _lock = new();
    private readonly Dictionary<(int Pid, ulong SwapChain), Chain> _chains = new();
    private readonly Dictionary<int, PidInfo> _pids = new();
    private readonly List<double> _scratch = new(4096);
    private Process? _process;
    private IntPtr _job;
    private string? _lastError;

    // Captura de sesión (para el resumen del registro).
    private int _capturePid;
    private List<float>? _capture;

    private bool _headerParsed;
    private int _iApp = -1, _iPid = -1, _iChain = -1, _iMs = -1, _maxIndex;

    public FpsState State { get; private set; } = FpsState.Stopped;
    public string StatusMessage { get; private set; } = "";
    public bool IsRunning => State == FpsState.Running;

    public static string? FindExecutable()
    {
        var dirs = new List<string> { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "tools") };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            dirs.Add(Path.Combine(dir.FullName, "tools"));

        foreach (var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(d)) continue;
            var exe = Directory.GetFiles(d, "PresentMon*.exe")
                .Where(f => !Path.GetFileName(f).Contains("Service", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (exe != null) return exe;
        }
        return null;
    }

    /// <summary>Descarga PresentMon junto al ejecutable (carpeta tools). Lo dispara el usuario con un botón.</summary>
    public static async Task<string> DownloadAsync()
    {
        var target = Path.Combine(AppContext.BaseDirectory, "tools", "PresentMon.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MandanHeat/1.1");
        var bytes = await http.GetByteArrayAsync(DownloadUrl);
        var tmp = target + ".part";
        await File.WriteAllBytesAsync(tmp, bytes);
        File.Move(tmp, target, overwrite: true);
        return target;
    }

    public bool Start(string exePath, string arguments)
    {
        Stop();
        lock (_lock)
        {
            _chains.Clear();
            _pids.Clear();
        }
        _headerParsed = false;
        _lastError = null;

        var p = new Process
        {
            StartInfo = new ProcessStartInfo(exePath, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) OnLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data) && !e.Data.StartsWith(' ')) _lastError = e.Data.Trim(); };
        p.Exited += (_, _) =>
        {
            if (!ReferenceEquals(_process, p)) return;
            State = FpsState.Error;
            StatusMessage = _lastError != null ? Loc.F("PresentMon se detuvo: {0}", _lastError) : Loc.T("PresentMon se detuvo");
        };

        _process = p;
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            _process = null;
            State = FpsState.Error;
            StatusMessage = Loc.F("No se pudo iniciar PresentMon: {0}", ex.Message);
            return false;
        }

        AttachToJob(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        State = FpsState.Running;
        StatusMessage = Loc.T("PresentMon activo");
        return true;
    }

    public void SetState(FpsState state, string message)
    {
        State = state;
        StatusMessage = message;
    }

    /// <summary>Procesa una línea CSV de PresentMon (público para el autodiagnóstico).</summary>
    public void FeedLine(string line) => OnLine(line);

    private void OnLine(string line)
    {
        if (!_headerParsed)
        {
            if (line.StartsWith("Application,", StringComparison.Ordinal))
                ParseHeader(line);
            return;
        }

        // Extrae solo las columnas necesarias sin crear cadenas por campo.
        ReadOnlySpan<char> s = line;
        ReadOnlySpan<char> appS = default, pidS = default, chainS = default, msS = default;
        int field = 0, start = 0;
        for (int i = 0; i <= s.Length && field <= _maxIndex; i++)
        {
            if (i < s.Length && s[i] != ',') continue;
            var tok = s[start..i];
            if (field == _iApp) appS = tok;
            else if (field == _iPid) pidS = tok;
            else if (field == _iChain) chainS = tok;
            else if (field == _iMs) msS = tok;
            field++;
            start = i + 1;
        }
        if (field <= _maxIndex || !int.TryParse(pidS, out var pid)) return;
        if (!double.TryParse(msS, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || ms <= 0 || ms > 5000) return;

        ulong chainKey = 0;
        if (chainS.Length > 2 && chainS[0] == '0' && (chainS[1] == 'x' || chainS[1] == 'X'))
            ulong.TryParse(chainS[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out chainKey);

        var now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            if (!_pids.TryGetValue(pid, out var info))
            {
                var app = appS.ToString();
                _pids[pid] = info = new PidInfo { App = app, Ignored = IgnoredApps.Contains(app) };
            }
            if (info.Ignored) return;

            if (!_chains.TryGetValue((pid, chainKey), out var c))
                _chains[(pid, chainKey)] = c = new Chain { App = info.App };
            c.Add((float)ms);
            c.LastSeen = now;

            if (_capture != null && pid == _capturePid)
                _capture.Add((float)ms);
        }
    }

    private void ParseHeader(string line)
    {
        var cols = line.Split(',');
        int Find(params string[] names)
        {
            foreach (var n in names)
            {
                var i = Array.FindIndex(cols, c => c.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
            }
            return -1;
        }
        _iApp = Find("Application");
        _iPid = Find("ProcessID");
        _iChain = Find("SwapChainAddress");
        _iMs = Find("msBetweenPresents", "MsBetweenAppStart", "FrameTime");
        _maxIndex = Math.Max(Math.Max(_iApp, _iPid), Math.Max(_iChain, _iMs));
        _headerParsed = _iPid >= 0 && _iMs >= 0;
        if (!_headerParsed)
        {
            State = FpsState.Error;
            StatusMessage = Loc.T("Formato de PresentMon no reconocido");
        }
    }

    /// <summary>Estadísticas del proceso indicado (su swapchain más activo), o null si no está presentando.</summary>
    public FrameStats? GetStats(int pid)
    {
        if (pid <= 0) return null;
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            List<(int, ulong)>? stale = null;
            Chain? best = null;
            int bestRecent = 0;

            foreach (var (key, chain) in _chains)
            {
                var age = Stopwatch.GetElapsedTime(chain.LastSeen, now).TotalSeconds;
                if (age > 30) { (stale ??= new()).Add(key); continue; }
                if (key.Pid != pid || age > 2 || chain.Count < 2) continue;

                // El swapchain "del juego" es el que más fotogramas ha presentado en el último segundo.
                int recent = 0; double sum = 0;
                while (recent < chain.Count && sum < 1000) sum += chain.Recent(recent++);
                if (recent > bestRecent) { best = chain; bestRecent = recent; }
            }

            if (stale != null)
                foreach (var k in stale) { _chains.Remove(k); _pids.Remove(k.Item1); }

            return best == null ? null : Compute(best);
        }
    }

    private FrameStats Compute(Chain chain)
    {
        // FPS y frametime medio del último segundo.
        double sum = 0; int n = 0;
        while (n < chain.Count && sum < 1000) sum += chain.Recent(n++);
        double fps = n * 1000.0 / sum;

        // 1 % low: percentil 99 del frametime en los últimos ~10 s.
        _scratch.Clear();
        double sum10 = 0;
        for (int i = 0; i < chain.Count && sum10 < 10000; i++)
        {
            var ms = chain.Recent(i);
            _scratch.Add(ms);
            sum10 += ms;
        }
        double low = _scratch.Count >= 30 ? LowFps(_scratch, 0.01) : double.NaN;
        return new FrameStats(chain.App, fps, sum / n, low);
    }

    /// <summary>
    /// "1 % low" como HWiNFO/CapFrameX: FPS equivalentes a la media del peor 1 % de frametimes.
    /// Ordena la lista recibida.
    /// </summary>
    public static double LowFps(List<double> frameTimes, double fraction)
    {
        frameTimes.Sort();
        int count = Math.Max(1, (int)Math.Round(frameTimes.Count * fraction));
        double sum = 0;
        for (int i = frameTimes.Count - count; i < frameTimes.Count; i++) sum += frameTimes[i];
        return 1000.0 / (sum / count);
    }

    /// <summary>Estadísticas de todos los procesos que presentan (diagnóstico).</summary>
    public List<(int Pid, FrameStats Stats)> Snapshot()
    {
        List<int> pids;
        lock (_lock) pids = _chains.Keys.Select(k => k.Pid).Distinct().ToList();
        var list = new List<(int, FrameStats)>();
        foreach (var pid in pids)
            if (GetStats(pid) is { } st) list.Add((pid, st));
        return list;
    }

    // ---- Captura de todos los frametimes de un proceso durante un registro

    public void BeginCapture() { lock (_lock) _capture = new List<float>(64 * 1024); }

    public void SetCapturePid(int pid) { lock (_lock) _capturePid = pid; }

    public List<float> EndCapture()
    {
        lock (_lock)
        {
            var c = _capture ?? new List<float>();
            _capture = null;
            return c;
        }
    }

    private void AttachToJob(Process p)
    {
        try
        {
            if (_job == IntPtr.Zero)
            {
                _job = Native.CreateJobObject(IntPtr.Zero, null);
                var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                Native.SetInformationJobObject(_job, Native.JobObjectExtendedLimitInformation, ref info,
                    (uint)Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
            }
            Native.AssignProcessToJobObject(_job, p.Handle);
        }
        catch
        {
            // Si falla, Stop() sigue cerrando PresentMon al salir normalmente.
        }
    }

    public void Stop()
    {
        var p = _process;
        _process = null;
        if (p != null)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            p.Dispose();
        }
        if (State is FpsState.Running) State = FpsState.Stopped;
        lock (_lock) _chains.Clear();
    }

    public void Dispose()
    {
        Stop();
        if (_job != IntPtr.Zero)
        {
            Native.CloseHandle(_job);
            _job = IntPtr.Zero;
        }
    }
}
