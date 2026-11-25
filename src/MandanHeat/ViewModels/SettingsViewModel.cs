using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using MandanHeat.Services;

namespace MandanHeat.ViewModels;

/// <summary>Ventana de Ajustes: cada cambio se guarda y se aplica al momento.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly AppSettings _s;
    private bool _startWithWindows;
    private string _diagnosticText = "";

    public SettingsViewModel(MainViewModel main, Func<Task<string>> checkForUpdates)
    {
        _main = main;
        _s = main.Settings;
        _startWithWindows = StartupTask.IsEnabled();
        _updateStatus = Updater.CanSelfUpdate
            ? Loc.F("Versión instalada: {0}", Updater.CurrentVersionText)
            : Loc.F("Versión instalada: {0}", Updater.CurrentVersionText) + " · " + Loc.T("Las actualizaciones automáticas solo funcionan con el ejecutable descargado de GitHub.");
        CheckUpdatesCommand = new RelayCommand(async () =>
        {
            if (_checkingUpdates) return;
            _checkingUpdates = true;
            UpdateStatus = Loc.T("Buscando actualizaciones…");
            try { UpdateStatus = await checkForUpdates(); }
            finally { _checkingUpdates = false; }
        });

        foreach (var (sensor, name, hw) in main.FanSensors())
        {
            var id = sensor.Identifier.ToString();
            bool selected = _s.RadiatorFansConfigured ? _s.RadiatorFans.Contains(id) : IsDefaultRadiator(sensor.Name, sensor.Hardware.HardwareType, sensor.Value);
            Fans.Add(new FanChoiceVm(id, $"{name}  ·  {hw}", sensor.Value is { } v ? $"{v:0} RPM" : "—", selected, SaveFans));
        }

        BrowseLogFolderCommand = new RelayCommand(BrowseLogFolder);
        OpenLogFolderCommand = new RelayCommand(_main.OpenLogFolder);
        ExportDiagnosticsCommand = new RelayCommand(ExportDiagnostics);
        ReopenSensorsCommand = new RelayCommand(() => { _main.ReopenSensors(); DiagnosticText = Loc.T("Sensores reiniciados."); });
        ResetNamesCommand = new RelayCommand(() => { _s.CustomNames.Clear(); Changed("Sensors"); OnPropertyChanged(nameof(CustomNamesText)); });
        ShowHiddenCommand = new RelayCommand(() => { _s.HiddenSensors.Clear(); Changed("Sensors"); OnPropertyChanged(nameof(HiddenText)); });
        ResetRadiatorCommand = new RelayCommand(() =>
        {
            _s.RadiatorFansConfigured = false;
            _s.RadiatorFans.Clear();
            Changed("Radiator");
        });
    }

    private static bool IsDefaultRadiator(string name, LibreHardwareMonitor.Hardware.HardwareType hw, float? value) =>
        hw == LibreHardwareMonitor.Hardware.HardwareType.SuperIO && value > 100 &&
        name.StartsWith("System Fan", StringComparison.OrdinalIgnoreCase);

    private void Changed(string name)
    {
        _s.Save();
        _main.ApplySetting(name);
    }

    private void SetBool(Func<bool> get, Action<bool> set, bool value, string settingName, [CallerMemberName] string? prop = null)
    {
        if (get() == value) return;
        set(value);
        OnPropertyChanged(prop);
        Changed(settingName);
    }

    public ICommand BrowseLogFolderCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand ExportDiagnosticsCommand { get; }
    public ICommand ReopenSensorsCommand { get; }
    public ICommand ResetNamesCommand { get; }
    public ICommand ShowHiddenCommand { get; }
    public ICommand ResetRadiatorCommand { get; }
    public ObservableCollection<FanChoiceVm> Fans { get; } = new();

    // ------------------------------------------------------------------ General

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value) return;
            bool ok = value ? StartupTask.Enable() : StartupTask.Disable();
            _startWithWindows = ok ? value : StartupTask.IsEnabled();
            OnPropertyChanged();
        }
    }

    public bool StartMinimized { get => _s.StartMinimized; set => SetBool(() => _s.StartMinimized, v => _s.StartMinimized = v, value, nameof(AppSettings.StartMinimized)); }
    public bool CloseToTray { get => _s.CloseToTray; set => SetBool(() => _s.CloseToTray, v => _s.CloseToTray = v, value, nameof(AppSettings.CloseToTray)); }
    public bool MinimizeToTray { get => _s.MinimizeToTray; set => SetBool(() => _s.MinimizeToTray, v => _s.MinimizeToTray = v, value, nameof(AppSettings.MinimizeToTray)); }
    public bool CheckForUpdates { get => _s.CheckForUpdates; set => SetBool(() => _s.CheckForUpdates, v => _s.CheckForUpdates = v, value, nameof(AppSettings.CheckForUpdates)); }

    private string _updateStatus;
    private bool _checkingUpdates;
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }
    public ICommand CheckUpdatesCommand { get; }

    public bool LangAuto { get => _s.Language == ""; set { if (value) SetLanguage(""); } }
    public bool LangEs { get => _s.Language == "es"; set { if (value) SetLanguage("es"); } }
    public bool LangEn { get => _s.Language == "en"; set { if (value) SetLanguage("en"); } }

    private void SetLanguage(string lang)
    {
        _s.Language = lang;
        Loc.Apply(lang);
        OnPropertyChanged(nameof(LangAuto)); OnPropertyChanged(nameof(LangEs)); OnPropertyChanged(nameof(LangEn));
        OnPropertyChanged(nameof(CustomNamesText)); OnPropertyChanged(nameof(HiddenText));
        Changed(nameof(AppSettings.Language));
    }

    public bool Ui500 { get => _s.UpdateIntervalMs == 500; set { if (value) SetUi(500); } }
    public bool Ui1000 { get => _s.UpdateIntervalMs == 1000; set { if (value) SetUi(1000); } }
    public bool Ui2000 { get => _s.UpdateIntervalMs == 2000; set { if (value) SetUi(2000); } }

    private void SetUi(int ms)
    {
        _s.UpdateIntervalMs = ms;
        OnPropertyChanged(nameof(Ui500)); OnPropertyChanged(nameof(Ui1000)); OnPropertyChanged(nameof(Ui2000));
        Changed(nameof(AppSettings.UpdateIntervalMs));
    }

    // ------------------------------------------------------------------ Atajos

    public string OverlayHotkey { get => _s.OverlayHotkey; set { _s.OverlayHotkey = value; OnPropertyChanged(); Changed(nameof(AppSettings.OverlayHotkey)); } }
    public string LogHotkey { get => _s.LogHotkey; set { _s.LogHotkey = value; OnPropertyChanged(); Changed(nameof(AppSettings.LogHotkey)); } }
    public string WindowHotkey { get => _s.WindowHotkey; set { _s.WindowHotkey = value; OnPropertyChanged(); Changed(nameof(AppSettings.WindowHotkey)); } }

    // ------------------------------------------------------------------ Overlay

    public bool PosTopLeft { get => _s.OverlayPosition == OverlayCorner.TopLeft; set { if (value) SetCorner(OverlayCorner.TopLeft); } }
    public bool PosTopRight { get => _s.OverlayPosition == OverlayCorner.TopRight; set { if (value) SetCorner(OverlayCorner.TopRight); } }
    public bool PosBottomLeft { get => _s.OverlayPosition == OverlayCorner.BottomLeft; set { if (value) SetCorner(OverlayCorner.BottomLeft); } }
    public bool PosBottomRight { get => _s.OverlayPosition == OverlayCorner.BottomRight; set { if (value) SetCorner(OverlayCorner.BottomRight); } }

    private void SetCorner(OverlayCorner c)
    {
        _s.OverlayPosition = c;
        OnPropertyChanged(nameof(PosTopLeft)); OnPropertyChanged(nameof(PosTopRight));
        OnPropertyChanged(nameof(PosBottomLeft)); OnPropertyChanged(nameof(PosBottomRight));
        Changed(nameof(AppSettings.OverlayPosition));
    }

    public double OverlayScale { get => _s.OverlayScale; set { _s.OverlayScale = Math.Round(value, 2); OnPropertyChanged(); Changed(nameof(AppSettings.OverlayScale)); } }
    public double OverlayOpacity { get => _s.OverlayBackgroundOpacity; set { _s.OverlayBackgroundOpacity = Math.Round(value, 2); OnPropertyChanged(); Changed(nameof(AppSettings.OverlayBackgroundOpacity)); } }

    public bool ShowFps { get => _s.OverlayShowFps; set => SetBool(() => _s.OverlayShowFps, v => _s.OverlayShowFps = v, value, "Overlay"); }
    public bool ShowCpu { get => _s.OverlayShowCpu; set => SetBool(() => _s.OverlayShowCpu, v => _s.OverlayShowCpu = v, value, "Overlay"); }
    public bool ShowGpu { get => _s.OverlayShowGpu; set => SetBool(() => _s.OverlayShowGpu, v => _s.OverlayShowGpu = v, value, "Overlay"); }
    public bool ShowLiquid { get => _s.OverlayShowLiquid; set => SetBool(() => _s.OverlayShowLiquid, v => _s.OverlayShowLiquid = v, value, "Overlay"); }
    public bool ShowRam { get => _s.OverlayShowRam; set => SetBool(() => _s.OverlayShowRam, v => _s.OverlayShowRam = v, value, "Overlay"); }
    public bool ShowRec { get => _s.OverlayShowRecording; set => SetBool(() => _s.OverlayShowRecording, v => _s.OverlayShowRecording = v, value, "Overlay"); }

    // ------------------------------------------------------------------ FPS

    public bool FpsAuto { get => _s.FpsSource == FpsSource.Auto; set { if (value) SetFps(FpsSource.Auto); } }
    public bool FpsPresentMon { get => _s.FpsSource == FpsSource.PresentMon; set { if (value) SetFps(FpsSource.PresentMon); } }
    public bool FpsDriver { get => _s.FpsSource == FpsSource.Driver; set { if (value) SetFps(FpsSource.Driver); } }
    public bool FpsOff { get => _s.FpsSource == FpsSource.Off; set { if (value) SetFps(FpsSource.Off); } }

    private void SetFps(FpsSource src)
    {
        _s.FpsSource = src;
        OnPropertyChanged(nameof(FpsAuto)); OnPropertyChanged(nameof(FpsPresentMon));
        OnPropertyChanged(nameof(FpsDriver)); OnPropertyChanged(nameof(FpsOff));
        Changed(nameof(AppSettings.FpsSource));
    }

    // ------------------------------------------------------------------ Registro

    public string LogFolder
    {
        get => _s.LogFolder;
        set { _s.LogFolder = value; OnPropertyChanged(); Changed(nameof(AppSettings.LogFolder)); }
    }

    public bool Log500 { get => _s.LogIntervalMs == 500; set { if (value) SetLog(500); } }
    public bool Log1000 { get => _s.LogIntervalMs == 1000; set { if (value) SetLog(1000); } }
    public bool Log2000 { get => _s.LogIntervalMs == 2000; set { if (value) SetLog(2000); } }
    public bool Log5000 { get => _s.LogIntervalMs == 5000; set { if (value) SetLog(5000); } }

    private void SetLog(int ms)
    {
        _s.LogIntervalMs = ms;
        OnPropertyChanged(nameof(Log500)); OnPropertyChanged(nameof(Log1000));
        OnPropertyChanged(nameof(Log2000)); OnPropertyChanged(nameof(Log5000));
        Changed(nameof(AppSettings.LogIntervalMs));
    }

    public bool DurManual { get => _s.LogDurationMinutes == 0; set { if (value) SetDur(0); } }
    public bool Dur15 { get => _s.LogDurationMinutes == 15; set { if (value) SetDur(15); } }
    public bool Dur30 { get => _s.LogDurationMinutes == 30; set { if (value) SetDur(30); } }
    public bool Dur60 { get => _s.LogDurationMinutes == 60; set { if (value) SetDur(60); } }
    public bool Dur120 { get => _s.LogDurationMinutes == 120; set { if (value) SetDur(120); } }

    private void SetDur(int minutes)
    {
        _s.LogDurationMinutes = minutes;
        foreach (var p in new[] { nameof(DurManual), nameof(Dur15), nameof(Dur30), nameof(Dur60), nameof(Dur120) }) OnPropertyChanged(p);
        Changed(nameof(AppSettings.LogDurationMinutes));
    }

    public bool LogAllSensors { get => _s.LogAllSensors; set => SetBool(() => _s.LogAllSensors, v => _s.LogAllSensors = v, value, nameof(AppSettings.LogAllSensors)); }
    public bool LogAutoStart { get => _s.LogAutoStart; set => SetBool(() => _s.LogAutoStart, v => _s.LogAutoStart = v, value, nameof(AppSettings.LogAutoStart)); }
    public bool LogExcelFormat { get => _s.LogExcelFormat; set => SetBool(() => _s.LogExcelFormat, v => _s.LogExcelFormat = v, value, nameof(AppSettings.LogExcelFormat)); }
    public bool LogWriteSummary { get => _s.LogWriteSummary; set => SetBool(() => _s.LogWriteSummary, v => _s.LogWriteSummary = v, value, nameof(AppSettings.LogWriteSummary)); }

    public string ExcludedApps
    {
        get => string.Join(", ", _s.AutoLogExcludedApps);
        set
        {
            _s.AutoLogExcludedApps = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            Changed(nameof(AppSettings.AutoLogExcludedApps));
        }
    }

    private void BrowseLogFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.T("Carpeta donde guardar los registros"),
            InitialDirectory = Directory.Exists(_s.LogFolder) ? _s.LogFolder : AppSettings.DefaultLogFolder(),
        };
        if (dialog.ShowDialog() == true) LogFolder = dialog.FolderName;
    }

    // ------------------------------------------------------------------ Refrigeración y sensores

    private void SaveFans()
    {
        _s.RadiatorFansConfigured = true;
        _s.RadiatorFans = Fans.Where(f => f.IsSelected).Select(f => f.Id).ToList();
        Changed("Radiator");
    }

    public string CustomNamesText => Loc.F("Restablecer nombres personalizados ({0})", _s.CustomNames.Count);
    public string HiddenText => Loc.F("Mostrar sensores ocultos ({0})", _s.HiddenSensors.Count);
    public bool ShowAllSensors { get => _main.ShowAllSensors; set { _main.ShowAllSensors = value; OnPropertyChanged(); } }

    // ------------------------------------------------------------------ Diagnóstico

    public string DiagnosticText { get => _diagnosticText; private set => Set(ref _diagnosticText, value); }

    private void ExportDiagnostics()
    {
        try
        {
            var path = _main.ExportDiagnostics();
            DiagnosticText = Loc.F("Guardado: {0}", path);
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            DiagnosticText = $"Error: {ex.Message}";
        }
    }
}
