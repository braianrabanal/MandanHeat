using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MandanHeat.Services;

public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>De dónde salen los FPS.</summary>
public enum FpsSource
{
    /// <summary>PresentMon para el juego en primer plano; si no hay datos, el contador del driver de la GPU.</summary>
    Auto,
    /// <summary>Solo PresentMon (frametime y 1 % low precisos).</summary>
    PresentMon,
    /// <summary>Solo el contador del driver (el mismo que Adrenalin/HWiNFO "Cuadros por segundo"). Consumo mínimo.</summary>
    Driver,
    Off,
}

/// <summary>Ajustes persistentes en %AppData%\MandanHeat\settings.json.</summary>
public sealed class AppSettings
{
    private const string OldPresentMonDefault = "--output_stdout --stop_existing_session --session_name MandanHeatFPS";
    public const string DefaultPresentMonArguments =
        "--output_stdout --no_console_stats --v1_metrics --no_track_display --no_track_input --no_track_gpu " +
        "--stop_existing_session --session_name MandanHeatFPS";

    // ---- General
    public int UpdateIntervalMs { get; set; } = 1000;
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool CloseHintShown { get; set; }
    public bool StartMinimized { get; set; }
    public bool ShowAllSensors { get; set; }
    /// <summary>Vista simple (tarjetas + métricas elegidas) en lugar de la lista completa.</summary>
    public bool SimpleView { get; set; } = true;
    public bool SimpleSensorsConfigured { get; set; }
    public List<string> SimpleSensors { get; set; } = new();

    // ---- Atajos ("" = desactivado)
    public string OverlayHotkey { get; set; } = "Ctrl+Shift+F10";
    public string LogHotkey { get; set; } = "Ctrl+Shift+F11";
    public string WindowHotkey { get; set; } = "Ctrl+Shift+F9";

    // ---- Overlay
    public bool OverlayEnabled { get; set; }
    public OverlayCorner OverlayPosition { get; set; } = OverlayCorner.TopLeft;
    public double OverlayScale { get; set; } = 1.0;
    public double OverlayBackgroundOpacity { get; set; } = 0.55;
    public bool OverlayShowFps { get; set; } = true;
    public bool OverlayShowCpu { get; set; } = true;
    public bool OverlayShowGpu { get; set; } = true;
    public bool OverlayShowLiquid { get; set; } = true;
    public bool OverlayShowRam { get; set; } = true;
    public bool OverlayShowRecording { get; set; } = true;

    // ---- FPS
    public FpsSource FpsSource { get; set; } = FpsSource.Auto;
    public string PresentMonArguments { get; set; } = DefaultPresentMonArguments;

    // ---- Registro de métricas
    public string LogFolder { get; set; } = DefaultLogFolder();
    public int LogIntervalMs { get; set; } = 1000;
    public bool LogAllSensors { get; set; } = true;
    public int LogDurationMinutes { get; set; } // 0 = hasta que lo detengas
    public bool LogAutoStart { get; set; }
    public bool LogExcelFormat { get; set; }
    public bool LogWriteSummary { get; set; } = true;
    public List<string> AutoLogExcludedApps { get; set; } = new()
    {
        "brave", "chrome", "msedge", "firefox", "opera", "Code", "Discord", "Spotify", "steamwebhelper",
        "Battle.net", "EpicGamesLauncher", "NZXT CAM", "SteelSeriesGG", "explorer", "MandanHeat", "obs64",
    };

    // ---- Refrigeración y sensores
    public bool RadiatorFansConfigured { get; set; }
    public List<string> RadiatorFans { get; set; } = new();
    public Dictionary<string, string> CustomNames { get; set; } = new();
    public List<string> HiddenSensors { get; set; } = new();
    public List<string> PinnedSensors { get; set; } = new();

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MandanHeat");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static string DefaultLogFolder()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var existing = Path.Combine(desktop, "RegistrosDeJuegosRendimiento");
        if (Directory.Exists(existing)) return existing;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MandanHeat", "Registros");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            s = new AppSettings();
        }

        // Migración: la primera versión usaba el modo CSV completo de PresentMon (mucho más pesado).
        if (string.IsNullOrWhiteSpace(s.PresentMonArguments) || s.PresentMonArguments == OldPresentMonDefault)
            s.PresentMonArguments = DefaultPresentMonArguments;
        if (string.IsNullOrWhiteSpace(s.LogFolder))
            s.LogFolder = DefaultLogFolder();
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // No es crítico.
        }
    }
}
