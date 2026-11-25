using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace MandanHeat.Services;

/// <summary>
/// Registra métricas en CSV (formato parecido al de HWiNFO: columnas Date, Time y "Sensor [unidad]")
/// y, al terminar, un resumen con mínimo / media / máximo y estadísticas de FPS.
/// </summary>
public sealed class MetricsLogger : IDisposable
{
    private sealed class Column
    {
        public required ISensor Sensor;
        public required string Header;
        public required string Group;
        public double Min = double.MaxValue, Max = double.MinValue, Sum;
        public int Count;

        public void Add(double v)
        {
            if (v < Min) Min = v;
            if (v > Max) Max = v;
            Sum += v;
            Count++;
        }
    }

    private readonly List<Column> _columns = new();
    private readonly List<double> _fpsSamples = new();
    private readonly HashSet<string> _games = new(StringComparer.OrdinalIgnoreCase);
    private StreamWriter? _writer;
    private long _lastFlush;
    private CultureInfo _culture = CultureInfo.InvariantCulture;
    private char _sep = ',';

    public bool IsActive => _writer != null;
    public string? FilePath { get; private set; }
    public DateTime StartedAt { get; private set; }
    public TimeSpan Elapsed => IsActive ? DateTime.Now - StartedAt : TimeSpan.Zero;
    public int Rows { get; private set; }
    public string Label { get; private set; } = "";
    public bool StartedAutomatically { get; private set; }

    public void Start(string folder, string label, IEnumerable<(ISensor Sensor, string Name, string Group)> sensors,
                      bool excelFormat, bool automatic)
    {
        Stop();
        Directory.CreateDirectory(folder);

        Label = label;
        StartedAutomatically = automatic;
        StartedAt = DateTime.Now;
        Rows = 0;
        _fpsSamples.Clear();
        _games.Clear();
        _columns.Clear();

        // Formato "Excel en español": ; como separador y coma decimal. Si no, CSV estándar (como HWiNFO).
        _culture = excelFormat ? CultureInfo.GetCultureInfo("es-ES") : CultureInfo.InvariantCulture;
        _sep = excelFormat ? ';' : ',';

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sensor, name, group) in sensors)
        {
            var header = $"{name} [{Unit(sensor.SensorType)}]";
            if (!used.Add(header)) header = $"{name} ({group}) [{Unit(sensor.SensorType)}]";
            used.Add(header);
            _columns.Add(new Column { Sensor = sensor, Header = header, Group = group });
        }

        var safe = string.Concat(label.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (safe.Length == 0) safe = Loc.T("Sesion");
        FilePath = Path.Combine(folder, $"{safe}_{StartedAt:yyyy-MM-dd_HH-mm-ss}.csv");

        _writer = new StreamWriter(FilePath, false, new UTF8Encoding(true), 64 * 1024);
        var headers = new List<string> { "Date", "Time" };
        headers.AddRange(_columns.Select(c => c.Header));
        headers.AddRange(new[] { "FPS [FPS]", "Frametime [ms]", "FPS 1% low [FPS]", Loc.T("Juego") });
        _writer.WriteLine(string.Join(_sep, headers.Select(Quote)));
        _lastFlush = Stopwatch.GetTimestamp();
    }

    public void WriteRow(FrameStats? fps)
    {
        if (_writer == null) return;
        var now = DateTime.Now;
        var sb = new StringBuilder(_columns.Count * 8 + 64);
        sb.Append(now.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)).Append(_sep)
          .Append(now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));

        foreach (var c in _columns)
        {
            sb.Append(_sep);
            if (c.Sensor.Value is { } v && !float.IsNaN(v))
            {
                sb.Append(Math.Round(v, 3).ToString(_culture));
                c.Add(v);
            }
        }

        sb.Append(_sep);
        if (fps is { } f)
        {
            sb.Append(Math.Round(f.Fps, 1).ToString(_culture)).Append(_sep)
              .Append(Math.Round(f.FrameTimeMs, 2).ToString(_culture)).Append(_sep)
              .Append(double.IsNaN(f.Low1Percent) ? "" : Math.Round(f.Low1Percent, 1).ToString(_culture)).Append(_sep)
              .Append(Quote(f.App));
            _fpsSamples.Add(f.Fps);
            _games.Add(f.App);
        }
        else
        {
            sb.Append(_sep).Append(_sep).Append(_sep);
        }

        _writer.WriteLine(sb.ToString());
        Rows++;

        // Escritura a disco cada 10 s: un registro de horas no debe machacar el SSD.
        if (Stopwatch.GetElapsedTime(_lastFlush).TotalSeconds >= 10)
        {
            _writer.Flush();
            _lastFlush = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Cierra el CSV y escribe el resumen. Devuelve la ruta del CSV.</summary>
    public string? Stop(IReadOnlyList<float>? frameTimes = null, bool writeSummary = true,
                        IReadOnlyList<(string Title, ISensor? Sensor)>? highlights = null)
    {
        if (_writer == null) return null;
        _writer.Flush();
        _writer.Dispose();
        _writer = null;

        if (writeSummary && FilePath != null)
        {
            try { File.WriteAllText(Path.ChangeExtension(FilePath, null) + Loc.T("_resumen.txt"), Summary(frameTimes, highlights), new UTF8Encoding(true)); }
            catch { }
        }
        return FilePath;
    }

    private string Summary(IReadOnlyList<float>? frameTimes, IReadOnlyList<(string Title, ISensor? Sensor)>? highlights)
    {
        var es = Loc.English ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo("es-ES");
        var sb = new StringBuilder();
        var duration = DateTime.Now - StartedAt;
        sb.AppendLine(Loc.F("MandanHeat · Resumen del registro «{0}»", Label));
        sb.AppendLine(Loc.T("Inicio:") + $" {StartedAt:dd/MM/yyyy HH:mm:ss}   " + Loc.T("Duración:") + $" {duration:hh\\:mm\\:ss}   " + Loc.T("Muestras:") + $" {Rows}" +
                      (StartedAutomatically ? "   " + Loc.T("(iniciado automáticamente)") : ""));
        if (_games.Count > 0) sb.AppendLine(Loc.F("Juego(s): {0}", string.Join(", ", _games)));
        sb.AppendLine(Loc.F("Archivo: {0}", Path.GetFileName(FilePath)));
        sb.AppendLine();

        if (_fpsSamples.Count > 0 || frameTimes is { Count: > 0 })
        {
            sb.AppendLine("== FPS ==");
            if (frameTimes is { Count: >= 100 })
            {
                var list = frameTimes.Select(x => (double)x).ToList();
                double p1 = FpsMonitor.LowFps(list, 0.01);    // deja la lista ordenada
                double p01 = FpsMonitor.LowFps(list, 0.001);
                var sorted = list.ToArray();
                double total = sorted.Sum();
                double avg = sorted.Length * 1000.0 / total;
                sb.AppendLine(string.Format(es, Loc.T("  Media: {0:0.0} FPS   1% low: {1:0.0} FPS   0,1% low: {2:0.0} FPS   ({3:N0} fotogramas)"),
                    avg, p1, p01, sorted.Length));
                sb.AppendLine(string.Format(es, Loc.T("  Frametime medio: {0:0.00} ms   peor 1%: {1:0.00} ms   máximo: {2:0.00} ms"),
                    total / sorted.Length, sorted[(int)(sorted.Length * 0.99)], sorted[^1]));
            }
            else if (_fpsSamples.Count > 0)
            {
                sb.AppendLine(string.Format(es, Loc.T("  Media: {0:0.0} FPS   Mín: {1:0.0}   Máx: {2:0.0}   (muestras por segundo)"),
                    _fpsSamples.Average(), _fpsSamples.Min(), _fpsSamples.Max()));
            }
            sb.AppendLine();
        }

        if (highlights is { Count: > 0 })
        {
            sb.AppendLine(Loc.T("== Principales =="));
            foreach (var (title, sensor) in highlights)
            {
                var col = sensor == null ? null : _columns.FirstOrDefault(c => ReferenceEquals(c.Sensor, sensor));
                if (col is not { Count: > 0 }) continue;
                sb.AppendLine(string.Format(es, Loc.T("  {0,-28} mín {1,8:0.0}   media {2,8:0.0}   máx {3,8:0.0}  {4}"),
                    title, col.Min, col.Sum / col.Count, col.Max, Unit(col.Sensor.SensorType)));
            }
            sb.AppendLine();
        }

        sb.AppendLine(Loc.T("== Todos los sensores =="));
        foreach (var g in _columns.Where(c => c.Count > 0).GroupBy(c => c.Group))
        {
            sb.AppendLine($"[{g.Key}]");
            foreach (var c in g)
                sb.AppendLine(string.Format(es, Loc.T("  {0,-48} mín {1,9:0.##}   media {2,9:0.##}   máx {3,9:0.##}"),
                    c.Header, c.Min, c.Sum / c.Count, c.Max));
        }
        return sb.ToString();
    }

    private string Quote(string s) =>
        s.Contains(_sep) || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static string Unit(SensorType t) => t switch
    {
        SensorType.Temperature => "°C",
        SensorType.Load or SensorType.Control or SensorType.Level or SensorType.Humidity => "%",
        SensorType.Fan => "RPM",
        SensorType.Power => "W",
        SensorType.Clock => "MHz",
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s",
        SensorType.Flow => "L/h",
        SensorType.Frequency => "Hz",
        SensorType.Energy => "mWh",
        SensorType.Noise => "dBA",
        SensorType.TimeSpan => "s",
        _ => "",
    };

    public void Dispose() => Stop(writeSummary: false);
}
