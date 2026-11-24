using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;
using MandanHeat.Interop;
using MandanHeat.Services;

namespace MandanHeat.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan StructureInterval = TimeSpan.FromSeconds(10);

    private readonly HardwareService _hw = new();
    private readonly FpsMonitor _fps = new();
    private readonly MetricsLogger _logger = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly int _ownPid = Environment.ProcessId;
    private readonly bool _isAdmin = SystemInfo.IsAdmin;
    private readonly bool _pawnIo = SystemInfo.IsPawnIoInstalled();

    private List<IHardware> _allHw = new();
    private readonly List<SensorVm> _sensorVms = new();
    private KeySensors _keys = new();
    private IReadOnlySet<IHardware> _overlayHardware = new HashSet<IHardware>();
    private string _signature = "";
    private bool _forceRebuild = true;
    private long _lastStructureCheck;
    private bool _isUiVisible = true;
    private bool _efficiencyMode;
    private bool _downloading, _reopening;
    private long _fpsLastCheck;
    private string _bannerSignature = "";

    private int _lastFpsTarget;
    private FrameStats? _lastFps;
    private readonly Dictionary<int, (string? Name, long At)> _procNames = new();

    private long _lastLogWrite;
    private int _autoLogPid, _autoCandidatePid;
    private long _autoCandidateSince, _autoLostSince;

    private string _statusText = "Iniciando sensores…";
    private string _overlayTitle = "MandanHeat";
    private string _overlayRec = "";
    private Brush _overlayBackground = Brushes.Transparent;

    private readonly SummaryCard _fpsCard = new("FPS · JUEGO ACTIVO");
    private readonly SummaryCard _cpuCard = new("CPU");
    private readonly SummaryCard _gpuCard = new("GPU");
    private readonly SummaryCard _liquidCard = new("REFRIGERACIÓN LÍQUIDA");
    private readonly SummaryCard _ramCard = new("MEMORIA RAM");

    /// <summary>Título, texto y si es un aviso (para la notificación de la bandeja).</summary>
    public event Action<string, string, bool>? Notify;

    public MainViewModel(AppSettings settings)
    {
        Settings = settings;
        Cards = new ObservableCollection<SummaryCard> { _fpsCard, _cpuCard, _gpuCard, _liquidCard, _ramCard };
        ToggleLoggingCommand = new RelayCommand(ToggleLogging);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        UpdateOverlayBackground();
    }

    public AppSettings Settings { get; }
    public ObservableCollection<HardwareVm> Hardware { get; } = new();
    public ObservableCollection<SummaryCard> Cards { get; }
    public ObservableCollection<OverlayLine> OverlayLines { get; } = new();
    public ObservableCollection<BannerVm> Banners { get; } = new();
    public ICommand ToggleLoggingCommand { get; }
    public ICommand OpenLogFolderCommand { get; }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string OverlayTitle { get => _overlayTitle; private set => Set(ref _overlayTitle, value); }
    public string OverlayRec { get => _overlayRec; private set { if (Set(ref _overlayRec, value)) OnPropertyChanged(nameof(HasOverlayRec)); } }
    public bool HasOverlayRec => _overlayRec.Length > 0;
    public Brush OverlayBackground { get => _overlayBackground; private set => Set(ref _overlayBackground, value); }
    public string TrayTooltip { get; private set; } = "MandanHeat";
    public string? LastLogPath { get; private set; }

    public bool IsLogging => _logger.IsActive;
    public string LogButtonText => _logger.IsActive
        ? $"■  DETENER  {_logger.Elapsed:hh\\:mm\\:ss}"
        : "●  GRABAR REGISTRO";
    public string OverlayHotkeyText => string.IsNullOrEmpty(Settings.OverlayHotkey) ? "" : $"({Settings.OverlayHotkey})";
    public string LogHotkeyText => string.IsNullOrEmpty(Settings.LogHotkey) ? "" : Settings.LogHotkey;

    public bool OverlayEnabled
    {
        get => Settings.OverlayEnabled;
        set { if (Settings.OverlayEnabled == value) return; Settings.OverlayEnabled = value; Settings.Save(); OnPropertyChanged(); Wake(); }
    }

    public bool ShowAllSensors
    {
        get => Settings.ShowAllSensors;
        set { Settings.ShowAllSensors = value; Settings.Save(); OnPropertyChanged(); _forceRebuild = true; Wake(); }
    }

    /// <summary>Solo instancia de pruebas: muestra el overlay sin cambiar los ajustes guardados.</summary>
    public bool DevOverlayPreview { get; set; }

    public double OverlayScale => Settings.OverlayScale;
    public OverlayCorner OverlayPosition => Settings.OverlayPosition;

    /// <summary>Lo llama la ventana de Ajustes tras cambiar algo.</summary>
    public void ApplySetting(string name)
    {
        switch (name)
        {
            case nameof(AppSettings.OverlayPosition): OnPropertyChanged(nameof(OverlayPosition)); break;
            case nameof(AppSettings.OverlayScale): OnPropertyChanged(nameof(OverlayScale)); break;
            case nameof(AppSettings.OverlayBackgroundOpacity): UpdateOverlayBackground(); break;
            case nameof(AppSettings.OverlayHotkey):
            case nameof(AppSettings.LogHotkey):
                OnPropertyChanged(nameof(OverlayHotkeyText));
                OnPropertyChanged(nameof(LogHotkeyText));
                break;
            case "Sensors":
                _forceRebuild = true;
                break;
        }
        RefreshKeys();
        if (Settings.OverlayEnabled) RefreshOverlay(_lastFps);
        Wake();
    }

    /// <summary>La ventana principal avisa cuando se muestra u oculta: oculta, no se actualiza la interfaz.</summary>
    public void SetUiVisible(bool visible)
    {
        if (_isUiVisible == visible) return;
        _isUiVisible = visible;
        if (visible) { _forceRebuild = true; Wake(); }
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private void UpdateOverlayBackground()
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(Settings.OverlayBackgroundOpacity, 0, 1) * 255), 10, 12, 18));
        b.Freeze();
        OverlayBackground = b;
    }

    // ================================================================== Bucle principal

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Run(_hw.Open, ct);
            _allHw = _hw.AllHardware();
            RefreshKeys();
        }
        catch (Exception ex)
        {
            StatusText = $"Error al abrir los sensores: {ex.Message}";
        }

        while (!ct.IsCancellationRequested)
        {
            int delay;
            try
            {
                delay = await TickAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                StatusText = $"Error leyendo sensores: {ex.Message}";
                delay = 2000;
            }

            try { await _wake.WaitAsync(delay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Un ciclo: solo se lee lo que alguien va a ver o registrar.
    /// Ventana oculta + overlay activo → solo CPU/GPU/AIO/RAM. Nada activo → no se lee ningún sensor.
    /// </summary>
    private async Task<int> TickAsync()
    {
        bool ui = _isUiVisible;
        bool overlay = Settings.OverlayEnabled || DevOverlayPreview;
        bool logging = _logger.IsActive;
        bool autoWatch = Settings.LogAutoStart && !logging;
        bool fpsWanted = Settings.FpsSource != FpsSource.Off &&
                         (ui || (overlay && Settings.OverlayShowFps) || logging || autoWatch);
        bool usePresentMon = fpsWanted && Settings.FpsSource is FpsSource.Auto or FpsSource.PresentMon;
        bool useDriverFps = fpsWanted && Settings.FpsSource is FpsSource.Auto or FpsSource.Driver;

        ManagePresentMon(usePresentMon);

        // ---- Lectura de sensores
        bool updateAll = ui || logging;
        HashSet<IHardware>? subset = null;
        if (!updateAll)
        {
            subset = new HashSet<IHardware>();
            if (overlay) subset.UnionWith(_overlayHardware);
            if (useDriverFps && _keys.GpuFps != null) subset.Add(HardwareService.TopLevel(_keys.GpuFps.Hardware));
        }
        if (updateAll || subset!.Count > 0)
            await Task.Run(() => _hw.Update(subset));

        // ---- Estructura (sensores que aparecen o desaparecen) y sensores clave, cada 10 s
        if (_forceRebuild || Stopwatch.GetElapsedTime(_lastStructureCheck) >= StructureInterval)
        {
            _allHw = _hw.AllHardware();
            RefreshKeys();
            if (ui) CheckStructure();
            _lastStructureCheck = Stopwatch.GetTimestamp();
        }
        else if (ui)
        {
            foreach (var vm in _sensorVms) vm.Update();
        }

        var fps = CurrentFps(fpsWanted, useDriverFps);
        _lastFps = fps;

        if (ui)
        {
            UpdateCards(fps);
            UpdateBanners();
            StatusText = $"Actualizado {DateTime.Now:HH:mm:ss} · {_sensorVms.Count} sensores · FPS: {FpsStatus(usePresentMon, useDriverFps)}" +
                         (logging ? $" · Registrando en {Path.GetFileName(_logger.FilePath)}" : "");
        }

        HandleLogging(fps);
        if (overlay) RefreshOverlay(fps);
        UpdateTray(fps);

        // Modo eficiencia de Windows mientras la ventana no está a la vista.
        if (_efficiencyMode == ui)
        {
            _efficiencyMode = !ui;
            PowerSaving.SetEfficiencyMode(_efficiencyMode);
        }

        int delay = int.MaxValue;
        if (ui) delay = Math.Min(delay, Settings.UpdateIntervalMs);
        if (overlay) delay = Math.Min(delay, Math.Max(500, Settings.UpdateIntervalMs));
        if (logging) delay = Math.Min(delay, Settings.LogIntervalMs);
        if (autoWatch) delay = Math.Min(delay, 2000);
        return delay == int.MaxValue ? 3000 : delay;
    }

    private string FpsStatus(bool presentMon, bool driver)
    {
        if (Settings.FpsSource == FpsSource.Off) return "desactivados";
        if (presentMon && _fps.IsRunning) return driver ? "PresentMon (+ driver de respaldo)" : "PresentMon";
        if (driver) return "contador del driver de la GPU";
        return _fps.StatusMessage;
    }

    // ================================================================== Sensores

    private List<(IHardware Hw, List<ISensor> Sensors)> BuildVisible()
    {
        var hidden = new HashSet<string>(Settings.HiddenSensors);
        return _allHw
            .Select(h => (Hw: h, Sensors: h.Sensors
                .Where(s => SensorNames.IsPlausible(s) && !hidden.Contains(s.Identifier.ToString()) &&
                            (Settings.ShowAllSensors || SensorFormat.IsDefault(s.SensorType)))
                .OrderBy(s => SensorFormat.Order(s.SensorType)).ThenBy(s => s.Index)
                .ToList()))
            .Where(x => x.Sensors.Count > 0)
            .OrderBy(x => SensorFormat.HardwareOrder(x.Hw.HardwareType))
            .ToList();
    }

    private void CheckStructure()
    {
        var visible = BuildVisible();
        var signature = string.Join("|", visible.SelectMany(x => x.Sensors).Select(s => s.Identifier.ToString()));
        if (_forceRebuild || signature != _signature)
        {
            Rebuild(visible);
            _signature = signature;
        }
        else
        {
            foreach (var vm in _sensorVms) vm.Update();
        }
        _forceRebuild = false;
    }

    private void Rebuild(List<(IHardware Hw, List<ISensor> Sensors)> visible)
    {
        foreach (var vm in _sensorVms) vm.PropertyChanged -= OnSensorPropertyChanged;
        _sensorVms.Clear();
        Hardware.Clear();

        var pinned = new HashSet<string>(Settings.PinnedSensors);
        foreach (var (hw, sensors) in visible)
        {
            var groups = sensors
                .GroupBy(s => s.SensorType)
                .OrderBy(g => SensorFormat.Order(g.Key))
                .Select(g =>
                {
                    var vms = g.Select(s => new SensorVm(s, SensorNames.Display(s, Settings),
                        pinned.Contains(s.Identifier.ToString()), RenameSensor, HideSensor)).ToList();
                    _sensorVms.AddRange(vms);
                    return new SensorGroupVm(SensorFormat.GroupTitle(g.Key), vms);
                })
                .ToList();
            Hardware.Add(new HardwareVm(SensorNames.HardwareName(hw), SensorFormat.Badge(hw.HardwareType), groups));
        }

        foreach (var vm in _sensorVms) vm.PropertyChanged += OnSensorPropertyChanged;
    }

    private void OnSensorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SensorVm.IsPinned) || sender is not SensorVm vm) return;
        Settings.PinnedSensors.Remove(vm.Id);
        if (vm.IsPinned) Settings.PinnedSensors.Add(vm.Id);
        Settings.Save();
        RefreshKeys();
        RefreshOverlay(_lastFps);
    }

    private void RenameSensor(SensorVm vm)
    {
        var current = Settings.CustomNames.TryGetValue(vm.Id, out var c) ? c : vm.Name;
        var dialog = new InputDialog("Renombrar sensor",
            $"Nombre para «{vm.Sensor.Name}» ({SensorNames.HardwareName(vm.Sensor.Hardware)}). Déjalo vacío para usar el nombre por defecto.",
            current) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        if (string.IsNullOrWhiteSpace(dialog.Value)) Settings.CustomNames.Remove(vm.Id);
        else Settings.CustomNames[vm.Id] = dialog.Value.Trim();
        Settings.Save();
        vm.Name = SensorNames.Display(vm.Sensor, Settings);
        RefreshOverlay(_lastFps);
    }

    private void HideSensor(SensorVm vm)
    {
        if (!Settings.HiddenSensors.Contains(vm.Id)) Settings.HiddenSensors.Add(vm.Id);
        Settings.PinnedSensors.Remove(vm.Id);
        Settings.Save();
        _forceRebuild = true;
        Wake();
    }

    /// <summary>Todos los ventiladores detectados (para elegir los del radiador en Ajustes).</summary>
    public List<(ISensor Sensor, string Name, string Hardware)> FanSensors() =>
        _allHw.SelectMany(h => h.Sensors)
            .Where(s => s.SensorType == SensorType.Fan && SensorNames.IsPlausible(s))
            .Select(s => (s, SensorNames.Display(s, Settings), SensorNames.HardwareName(s.Hardware)))
            .ToList();

    public string ExportDiagnostics()
    {
        Directory.CreateDirectory(Settings.LogFolder);
        var path = Path.Combine(Settings.LogFolder, $"MandanHeat-diagnostico_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");
        File.WriteAllText(path, _hw.Dump(), new System.Text.UTF8Encoding(true));
        return path;
    }

    public async void ReopenSensors()
    {
        if (_reopening) return;
        _reopening = true;
        StatusText = "Reiniciando la lectura de sensores…";
        try
        {
            await Task.Run(_hw.Reopen);
            _allHw = _hw.AllHardware();
            _forceRebuild = true;
            RefreshKeys();
        }
        catch (Exception ex)
        {
            Notify?.Invoke("Error al reiniciar sensores", ex.Message, true);
        }
        finally
        {
            _reopening = false;
            Wake();
        }
    }

    // ================================================================== FPS

    private void ManagePresentMon(bool wanted)
    {
        if (!wanted)
        {
            if (_fps.IsRunning) _fps.Stop();
            return;
        }
        if (_fps.IsRunning || _fps.State == FpsState.Error || _downloading) return;
        // Si falta PresentMon, se vuelve a buscar como mucho cada 30 s.
        if (_fps.State == FpsState.NotFound && Stopwatch.GetElapsedTime(_fpsLastCheck).TotalSeconds < 30) return;
        StartFps();
    }

    private void StartFps()
    {
        _fpsLastCheck = Stopwatch.GetTimestamp();
        var exe = FpsMonitor.FindExecutable();
        if (exe == null)
            _fps.SetState(FpsState.NotFound, "falta PresentMon");
        else if (!_isAdmin)
            _fps.SetState(FpsState.Error, "PresentMon requiere ejecutar MandanHeat como administrador");
        else
            _fps.Start(exe, Settings.PresentMonArguments);
    }

    private void RetryFps()
    {
        _fps.SetState(FpsState.Stopped, "");
        StartFps();
        _bannerSignature = "";
        Wake();
    }

    private async void DownloadPresentMon()
    {
        if (_downloading) return;
        _downloading = true;
        UpdateBanners();
        try
        {
            await FpsMonitor.DownloadAsync();
            _fps.SetState(FpsState.Stopped, "");
            StartFps();
        }
        catch (Exception ex)
        {
            _fps.SetState(FpsState.NotFound, $"no se pudo descargar PresentMon: {ex.Message}");
        }
        finally
        {
            _downloading = false;
            UpdateBanners();
            Wake();
        }
    }

    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost", "LockApp",
    };

    /// <summary>
    /// FPS del proceso en primer plano. Si el foco está en MandanHeat o en el escritorio, el último juego medido.
    /// PresentMon primero (frametime y 1 % low reales); si no da datos, el contador del driver de la GPU.
    /// </summary>
    private FrameStats? CurrentFps(bool wanted, bool driverFallback)
    {
        if (!wanted) return null;

        var fg = Native.GetForegroundProcessId();
        bool fgUsable = fg > 0 && fg != _ownPid && !IsShell(fg);
        int target = fgUsable ? fg : _lastFpsTarget;

        if (_fps.IsRunning)
        {
            var stats = _fps.GetStats(target);
            if (stats != null) { _lastFpsTarget = target; return stats; }
        }

        if (driverFallback && _keys.GpuFps?.Value is { } d && d > 0.5f)
        {
            if (fgUsable) _lastFpsTarget = fg;
            var name = ProcessName(_lastFpsTarget) ?? "Juego";
            return new FrameStats(name + ".exe", d, 1000.0 / d, double.NaN, FromDriver: true);
        }
        return null;
    }

    private string? ProcessName(int pid)
    {
        if (pid <= 0) return null;
        if (_procNames.TryGetValue(pid, out var e) && Stopwatch.GetElapsedTime(e.At).TotalSeconds < 30) return e.Name;
        string? name;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
        }
        catch
        {
            name = null;
        }
        if (_procNames.Count > 256) _procNames.Clear();
        _procNames[pid] = (name, Stopwatch.GetTimestamp());
        return name;
    }

    private bool IsShell(int pid) => ProcessName(pid) is { } n && ShellProcesses.Contains(n);

    private static string GameName(string app) =>
        app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;

    // ================================================================== Registro

    public void ToggleLogging()
    {
        if (_logger.IsActive) StopLogging();
        else StartLogging(automatic: false, _lastFps is { } f ? GameName(f.App) : "Sesion", _lastFpsTarget);
    }

    private void StartLogging(bool automatic, string label, int gamePid)
    {
        _allHw = _hw.AllHardware();
        var sensors = _allHw
            .OrderBy(h => SensorFormat.HardwareOrder(h.HardwareType))
            .SelectMany(h => h.Sensors
                .Where(s => SensorNames.IsPlausible(s) && (Settings.LogAllSensors || SensorFormat.IsDefault(s.SensorType)))
                .OrderBy(s => SensorFormat.Order(s.SensorType)).ThenBy(s => s.Index)
                .Select(s => (s, SensorNames.Display(s, Settings), SensorNames.HardwareName(h))))
            .ToList();
        try
        {
            _logger.Start(Settings.LogFolder, label, sensors, Settings.LogExcelFormat, automatic);
        }
        catch (Exception ex)
        {
            Notify?.Invoke("No se pudo iniciar el registro", ex.Message, true);
            return;
        }

        _autoLogPid = gamePid;
        _autoLostSince = 0;
        _fps.BeginCapture();
        _fps.SetCapturePid(gamePid);
        _lastLogWrite = 0;
        OnLoggingChanged();
        Notify?.Invoke(automatic ? $"Registro automático: {label}" : "Registro iniciado",
            $"Guardando {sensors.Count} sensores en {Settings.LogFolder}", false);
        Wake();
    }

    private void StopLogging()
    {
        var frames = _fps.EndCapture();
        var path = _logger.Stop(frames, Settings.LogWriteSummary, Highlights());
        _autoLogPid = 0;
        OnLoggingChanged();
        if (path != null)
        {
            LastLogPath = path;
            Notify?.Invoke("Registro guardado", Path.GetFileName(path), false);
        }
    }

    private void HandleLogging(FrameStats? fps)
    {
        var now = Stopwatch.GetTimestamp();
        if (_logger.IsActive)
        {
            if (Settings.LogDurationMinutes > 0 && _logger.Elapsed.TotalMinutes >= Settings.LogDurationMinutes)
            {
                StopLogging();
                return;
            }

            // En un registro automático los FPS son los del juego aunque cambies de ventana.
            var rowFps = fps;
            if (_logger.StartedAutomatically && _fps.IsRunning)
                rowFps = _fps.GetStats(_autoLogPid) ?? (fps is { FromDriver: true } ? fps : null);
            else if (_lastFpsTarget > 0 && _autoLogPid != _lastFpsTarget && fps != null)
            {
                _autoLogPid = _lastFpsTarget;
                _fps.SetCapturePid(_autoLogPid);
            }

            if (_lastLogWrite == 0 || Stopwatch.GetElapsedTime(_lastLogWrite).TotalMilliseconds >= Settings.LogIntervalMs - 60)
            {
                _logger.WriteRow(rowFps);
                _lastLogWrite = now;
            }

            // Fin automático: el juego se cerró (o lleva 60 s sin presentar fotogramas).
            if (_logger.StartedAutomatically)
            {
                bool gone = ProcessName(_autoLogPid) == null;
                if (rowFps == null) { if (_autoLostSince == 0) _autoLostSince = now; }
                else _autoLostSince = 0;
                if (gone || (_autoLostSince != 0 && Stopwatch.GetElapsedTime(_autoLostSince).TotalSeconds >= 60))
                    StopLogging();
            }
            OnPropertyChanged(nameof(LogButtonText));
        }
        else if (Settings.LogAutoStart)
        {
            // Inicio automático: un juego (no excluido) lleva 15 s presentando fotogramas en primer plano.
            if (fps is { } f && f.Fps >= 15 && !IsExcluded(f.App))
            {
                if (_autoCandidatePid != _lastFpsTarget)
                {
                    _autoCandidatePid = _lastFpsTarget;
                    _autoCandidateSince = now;
                }
                else if (Stopwatch.GetElapsedTime(_autoCandidateSince).TotalSeconds >= 15)
                {
                    _autoCandidatePid = 0;
                    StartLogging(automatic: true, GameName(f.App), _lastFpsTarget);
                }
            }
            else
            {
                _autoCandidatePid = 0;
            }
        }
    }

    private bool IsExcluded(string app)
    {
        var name = GameName(app);
        return Settings.AutoLogExcludedApps.Any(x => name.Equals(GameName(x.Trim()), StringComparison.OrdinalIgnoreCase));
    }

    private void OnLoggingChanged()
    {
        OnPropertyChanged(nameof(IsLogging));
        OnPropertyChanged(nameof(LogButtonText));
        if (!_logger.IsActive) OverlayRec = "";
    }

    private List<(string, ISensor?)> Highlights()
    {
        var k = _keys;
        var list = new List<(string, ISensor?)>
        {
            ("CPU (Tctl/Tdie)", k.CpuTemp), ("CPU uso", k.CpuLoad), ("CPU potencia", k.CpuPower),
            ("GPU (borde)", k.GpuTemp), ("GPU punto caliente", k.GpuHotSpot), ("GPU uso", k.GpuLoad), ("GPU potencia", k.GpuPower),
            ("Líquido", k.Liquid), ("Bomba", k.Pump), ("RAM uso", k.RamLoad),
        };
        foreach (var fan in k.RadiatorFans)
            list.Add(($"Radiador · {SensorNames.Display(fan, Settings)}", fan));
        return list;
    }

    public void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(Settings.LogFolder);
            if (LastLogPath != null && File.Exists(LastLogPath))
                Process.Start("explorer.exe", $"/select,\"{LastLogPath}\"");
            else
                Process.Start("explorer.exe", $"\"{Settings.LogFolder}\"");
        }
        catch { }
    }

    // ================================================================== Tarjetas de resumen

    private void UpdateCards(FrameStats? fps)
    {
        if (fps is { } f)
        {
            _fpsCard.Value = $"{f.Fps:0}";
            _fpsCard.Brush = Palette.ForFps(f.Fps);
            _fpsCard.Detail = f.FromDriver
                ? $"{GameName(f.App)} · contador del driver"
                : $"{GameName(f.App)} · {f.FrameTimeMs:0.0} ms" + (double.IsNaN(f.Low1Percent) ? "" : $" · 1% low {f.Low1Percent:0}");
        }
        else
        {
            _fpsCard.Value = "—";
            _fpsCard.Brush = Palette.Muted;
            _fpsCard.Detail = Settings.FpsSource == FpsSource.Off ? "Desactivado en Ajustes" : "Abre un juego y ponlo en primer plano";
        }

        var k = _keys;
        SetTempCard(_cpuCard, k.CpuTemp, false,
            Join(Part("Uso", k.CpuLoad), Part(null, k.CpuPower)), "Falta el driver PawnIO o permisos");
        SetTempCard(_gpuCard, k.GpuTemp, false,
            Join(Part("Uso", k.GpuLoad), Part("Punto caliente", k.GpuHotSpot), Part(null, k.GpuPower)), "No se detecta la GPU");
        SetTempCard(_liquidCard, k.Liquid, true,
            Join(Part("Bomba", k.Pump), RadiatorText(k, "Radiador ")), "No se detecta un AIO compatible");

        if (k.RamLoad?.Value is { } ram)
        {
            _ramCard.Value = $"{ram:0}";
            _ramCard.Unit = "%";
            _ramCard.Brush = ram < 80 ? Palette.Text : ram < 90 ? Palette.Warn : Palette.Hot;
            _ramCard.Detail = k.RamUsed?.Value is { } used
                ? $"{used:0.0} GB en uso" + (k.RamAvailable?.Value is { } avail ? $" de {used + avail:0} GB" : "")
                : "";
        }
        else
        {
            _ramCard.Value = "—";
            _ramCard.Unit = "";
            _ramCard.Detail = "";
        }
    }

    private static string? RadiatorText(KeySensors k, string prefix)
    {
        var rpms = k.RadiatorFans.Where(f => f.Value.HasValue).Select(f => $"{f.Value:0}").ToList();
        return rpms.Count == 0 ? null : $"{prefix}{string.Join(" · ", rpms)} rpm";
    }

    private static void SetTempCard(SummaryCard card, ISensor? temp, bool liquid, string detail, string missing)
    {
        if (temp?.Value is { } t)
        {
            card.Value = $"{t:0}";
            card.Unit = "°C";
            card.Brush = Palette.ForTemperature(t, liquid);
            card.Detail = detail;
        }
        else
        {
            card.Value = "—";
            card.Unit = "";
            card.Brush = Palette.Muted;
            card.Detail = string.IsNullOrEmpty(detail) ? missing : detail;
        }
    }

    private static string? Part(string? label, ISensor? s)
    {
        if (s?.Value is null) return null;
        var text = SensorFormat.Short(s.SensorType, s.Value);
        return label == null ? text : $"{label} {text}";
    }

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => p != null));
    private static string Short(ISensor? s) => s == null ? "—" : SensorFormat.Short(s.SensorType, s.Value);

    private void UpdateTray(FrameStats? fps)
    {
        var k = _keys;
        var text = $"CPU {Short(k.CpuTemp)} · GPU {Short(k.GpuTemp)}" +
                   (k.Liquid != null ? $" · Líq {Short(k.Liquid)}" : "") +
                   (fps is { } f ? $" · {f.Fps:0} FPS" : "") +
                   (_logger.IsActive ? " · REC" : "");
        if (text == TrayTooltip) return;
        TrayTooltip = text;
        OnPropertyChanged(nameof(TrayTooltip));
    }

    // ================================================================== Overlay

    private void RefreshOverlay(FrameStats? fps)
    {
        var k = _keys;
        var lines = new List<(string Key, string Label, Brush LabelBrush, List<OverlaySegment> Segs)>();

        if (Settings.OverlayShowFps)
        {
            var segs = new List<OverlaySegment>();
            if (fps is { } f)
            {
                segs.Add(new($"{f.Fps:0}", Palette.ForFps(f.Fps)));
                if (!f.FromDriver)
                {
                    segs.Add(new($"{f.FrameTimeMs:0.0} ms", Palette.Muted));
                    if (!double.IsNaN(f.Low1Percent)) segs.Add(new($"1% {f.Low1Percent:0}", Palette.Muted));
                }
            }
            else
            {
                segs.Add(new("—", Palette.Muted));
            }
            lines.Add(("fps", "FPS", Palette.Accent, segs));
        }

        if (Settings.OverlayShowCpu && (k.CpuTemp ?? k.CpuLoad) != null)
            lines.Add(("cpu", "CPU", Palette.Info, Segs(Temp(k.CpuTemp), Plain(k.CpuLoad), Dim(k.CpuPower))));

        if (Settings.OverlayShowGpu && (k.GpuTemp ?? k.GpuLoad) != null)
            lines.Add(("gpu", "GPU", Palette.Good, Segs(Temp(k.GpuTemp), Plain(k.GpuLoad), Temp(k.GpuHotSpot, prefix: "PC "), Dim(k.GpuPower))));

        if (Settings.OverlayShowLiquid && (k.Liquid != null || k.RadiatorFans.Count > 0))
        {
            var rad = RadiatorText(k, "");
            lines.Add(("liq", "LIQ", Palette.Info, Segs(Temp(k.Liquid, liquid: true),
                rad != null ? new OverlaySegment(rad, Palette.Text) : null, Dim(k.Pump))));
        }

        if (Settings.OverlayShowRam && k.RamLoad != null)
            lines.Add(("ram", "RAM", Palette.Warn, Segs(Plain(k.RamLoad),
                k.RamUsed?.Value is { } used ? new OverlaySegment($"{used:0.0} GB", Palette.Muted) : null)));

        foreach (var s in k.Pinned)
        {
            var name = SensorNames.Display(s, Settings);
            var label = name.Length > 16 ? name[..15] + "…" : name;
            var brush = s.SensorType == SensorType.Temperature && s.Value is { } t
                ? Palette.ForTemperature(t, SensorVm.IsLiquidSensor(s)) : Palette.Text;
            lines.Add(("pin:" + s.Identifier, label, Palette.Muted,
                new List<OverlaySegment> { new(SensorFormat.Short(s.SensorType, s.Value), brush) }));
        }

        OverlayTitle = fps is { } g ? GameName(g.App) : "MandanHeat";
        OverlayRec = _logger.IsActive && Settings.OverlayShowRecording ? $"● REC {_logger.Elapsed:hh\\:mm\\:ss}" : "";

        // Actualiza en sitio si la estructura no cambia (evita parpadeos y trabajo de maquetación).
        bool sameShape = lines.Count == OverlayLines.Count &&
                         lines.Zip(OverlayLines).All(p => p.First.Key == p.Second.Key && p.First.Segs.Count == p.Second.Segments.Count);
        if (sameShape)
        {
            for (int i = 0; i < lines.Count; i++)
                for (int j = 0; j < lines[i].Segs.Count; j++)
                {
                    OverlayLines[i].Segments[j].Text = lines[i].Segs[j].Text;
                    OverlayLines[i].Segments[j].Brush = lines[i].Segs[j].Brush;
                }
        }
        else
        {
            OverlayLines.Clear();
            foreach (var l in lines) OverlayLines.Add(new OverlayLine(l.Key, l.Label, l.LabelBrush, l.Segs));
        }
    }

    private static List<OverlaySegment> Segs(params OverlaySegment?[] segs) => segs.Where(s => s != null).ToList()!;

    private static OverlaySegment? Temp(ISensor? s, bool liquid = false, string prefix = "") =>
        s?.Value is { } v ? new OverlaySegment(prefix + SensorFormat.Short(s.SensorType, v), Palette.ForTemperature(v, liquid)) : null;

    private static OverlaySegment? Plain(ISensor? s) =>
        s?.Value is { } v ? new OverlaySegment(SensorFormat.Short(s.SensorType, v), Palette.Text) : null;

    private static OverlaySegment? Dim(ISensor? s) =>
        s?.Value is { } v ? new OverlaySegment(SensorFormat.Short(s.SensorType, v), Palette.Muted) : null;

    // ================================================================== Avisos

    private void UpdateBanners()
    {
        var list = new List<BannerVm>();
        bool needsPresentMon = Settings.FpsSource is FpsSource.Auto or FpsSource.PresentMon;

        if (!_isAdmin)
            list.Add(new("admin",
                "Sin permisos de administrador: faltarán la temperatura del CPU, los sensores de la placa y los FPS de PresentMon.",
                "Reiniciar como administrador", new RelayCommand(RestartElevated)));
        if (!_pawnIo)
            list.Add(new("pawnio",
                "Falta el driver PawnIO, necesario para la temperatura del CPU y los sensores de la placa. " +
                "Instálalo (o ejecuta «winget install namazso.PawnIO») y reinicia MandanHeat.",
                "Descargar PawnIO", new RelayCommand(() => SystemInfo.OpenUrl("https://pawnio.eu/"))));
        else if (_isAdmin && _allHw.Count > 0 &&
                 _allHw.Any(h => h.HardwareType == HardwareType.Motherboard) &&
                 !_allHw.Any(h => h.HardwareType == HardwareType.SuperIO && h.Sensors.Any(s => s.Value.HasValue)))
            list.Add(new("superio",
                "No se pueden leer los sensores de la placa base (ventiladores del radiador, MOS, chipset). " +
                "Suele pasar cuando otro programa de monitorización (NZXT CAM, MSI Center, iCUE, HWiNFO…) tiene ocupado el chip. " +
                "Ciérralo y pulsa «Reintentar». Si sigue igual, exporta el diagnóstico desde Ajustes.",
                _reopening ? null : "Reintentar", _reopening ? null : new RelayCommand(ReopenSensors)));

        if (needsPresentMon && _fps.State == FpsState.NotFound)
            list.Add(new("presentmon",
                _downloading ? "Descargando PresentMon…" :
                "Para medir FPS, frametime y 1 % low de cualquier juego hace falta PresentMon (herramienta gratuita de Intel, ~1 MB).",
                _downloading ? null : "Descargar PresentMon", _downloading ? null : new RelayCommand(DownloadPresentMon)));
        else if (needsPresentMon && _fps.State == FpsState.Error && _isAdmin)
            list.Add(new("fpserror", _fps.StatusMessage, "Reintentar", new RelayCommand(RetryFps)));

        var sig = string.Join("|", list.Select(b => b.Key + b.Text + b.HasAction));
        if (sig == _bannerSignature) return;
        _bannerSignature = sig;
        Banners.Clear();
        foreach (var b in list) Banners.Add(b);
    }

    private void RestartElevated()
    {
        if (SystemInfo.RelaunchElevated("--restarted"))
        {
            App.IsShuttingDown = true;
            Application.Current.Shutdown();
        }
    }

    public void Dispose()
    {
        if (_logger.IsActive) StopLogging();
        _fps.Dispose();
        _hw.Dispose();
        _logger.Dispose();
    }

    // ================================================================== Sensores clave

    private void RefreshKeys()
    {
        _keys = KeySensors.Find(_allHw, Settings);
        var set = new HashSet<IHardware>();
        void Add(ISensor? s) { if (s != null) set.Add(HardwareService.TopLevel(s.Hardware)); }
        var k = _keys;
        if (Settings.OverlayShowCpu) { Add(k.CpuTemp); Add(k.CpuLoad); Add(k.CpuPower); }
        if (Settings.OverlayShowGpu) { Add(k.GpuTemp); Add(k.GpuHotSpot); Add(k.GpuLoad); Add(k.GpuPower); }
        if (Settings.OverlayShowLiquid) { Add(k.Liquid); Add(k.Pump); foreach (var f in k.RadiatorFans) Add(f); }
        if (Settings.OverlayShowRam) { Add(k.RamLoad); Add(k.RamUsed); }
        foreach (var p in k.Pinned) Add(p);
        _overlayHardware = set;
    }

    private sealed class KeySensors
    {
        public ISensor? CpuTemp, CpuLoad, CpuPower;
        public ISensor? GpuTemp, GpuHotSpot, GpuLoad, GpuPower, GpuFps;
        public ISensor? Liquid, Pump;
        public ISensor? RamLoad, RamUsed, RamAvailable;
        public List<ISensor> RadiatorFans = new();
        public List<ISensor> Pinned = new();

        public static KeySensors Find(List<IHardware> all, AppSettings settings)
        {
            var k = new KeySensors();
            var cpu = all.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            if (cpu != null)
            {
                k.CpuTemp = Pick(cpu, SensorType.Temperature, true, "Core (Tctl/Tdie)", "Tctl", "Package", "Tdie", "Core Max");
                k.CpuLoad = Pick(cpu, SensorType.Load, true, "CPU Total");
                k.CpuPower = Pick(cpu, SensorType.Power, false, "Package", "CPU Package");
            }

            var gpu = all.FirstOrDefault(h => h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia)
                      ?? all.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);
            if (gpu != null)
            {
                k.GpuTemp = Pick(gpu, SensorType.Temperature, true, "GPU Core");
                k.GpuHotSpot = Pick(gpu, SensorType.Temperature, false, "GPU Hot Spot", "Hot Spot", "Hotspot", "Junction");
                k.GpuLoad = Pick(gpu, SensorType.Load, true, "GPU Core", "D3D 3D");
                k.GpuPower = Pick(gpu, SensorType.Power, true, "GPU Package", "GPU Total", "Board", "GPU Core");
                k.GpuFps = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Factor &&
                                                           s.Name.Contains("FPS", StringComparison.OrdinalIgnoreCase));
            }

            var coolers = all.Where(h => h.HardwareType == HardwareType.Cooler).ToList();
            k.Liquid = coolers.Select(c => Pick(c, SensorType.Temperature, false, "Liquid", "Coolant", "Water")).FirstOrDefault(s => s != null)
                       ?? coolers.Select(c => Pick(c, SensorType.Temperature, true)).FirstOrDefault(s => s != null);
            k.Pump = coolers.Select(c => Pick(c, SensorType.Fan, false, "Pump")).FirstOrDefault(s => s != null);

            var mem = all.FirstOrDefault(h => h.HardwareType == HardwareType.Memory && !h.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase));
            if (mem != null)
            {
                k.RamLoad = Pick(mem, SensorType.Load, true, "Memory");
                k.RamUsed = Pick(mem, SensorType.Data, false, "Memory Used");
                k.RamAvailable = Pick(mem, SensorType.Data, false, "Memory Available");
            }

            var byId = new Dictionary<string, ISensor>();
            foreach (var s in all.SelectMany(h => h.Sensors)) byId.TryAdd(s.Identifier.ToString(), s);

            // Ventiladores del radiador: los elegidos en Ajustes o, por defecto, los "System Fan" de la placa que giran.
            if (settings.RadiatorFansConfigured)
            {
                k.RadiatorFans = settings.RadiatorFans.Select(id => byId.GetValueOrDefault(id)).OfType<ISensor>().ToList();
            }
            else
            {
                var boardFans = all.Where(h => h.HardwareType == HardwareType.SuperIO)
                    .SelectMany(h => h.Sensors)
                    .Where(s => s.SensorType == SensorType.Fan && s.Value > 100)
                    .ToList();
                k.RadiatorFans = boardFans.Where(s => s.Name.StartsWith("System Fan", StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
                if (k.RadiatorFans.Count == 0)
                    k.RadiatorFans = boardFans.Where(s => !s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase) &&
                                                          !s.Name.Contains("Pump", StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
            }

            k.Pinned = settings.PinnedSensors.Select(id => byId.GetValueOrDefault(id)).OfType<ISensor>().ToList();
            return k;
        }

        /// <summary>Busca por nombre exacto, luego por coincidencia parcial y, si se permite, el primero del tipo.</summary>
        private static ISensor? Pick(IHardware hw, SensorType type, bool fallback, params string[] names)
        {
            var candidates = hw.Sensors.Where(s => s.SensorType == type && SensorNames.IsPlausible(s)).ToList();
            foreach (var n in names)
            {
                var exact = candidates.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
            }
            foreach (var n in names)
            {
                var partial = candidates.FirstOrDefault(s => s.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
                if (partial != null) return partial;
            }
            return fallback ? candidates.FirstOrDefault() : null;
        }
    }
}
