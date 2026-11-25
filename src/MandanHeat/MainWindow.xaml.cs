using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MandanHeat.Interop;
using MandanHeat.Services;
using MandanHeat.ViewModels;

namespace MandanHeat;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly OverlayWindow _overlay;
    private readonly HotkeyManager _hotkeys = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _startHidden;
    private TrayIcon? _tray;
    private SettingsWindow? _settings;

    public MainWindow(bool startHidden)
    {
        InitializeComponent();
        _startHidden = startHidden;

        var settings = AppSettings.Load();
        Loc.Apply(settings.Language);
        _vm = new MainViewModel(settings);
        DataContext = _vm;
        _overlay = new OverlayWindow { DataContext = _vm };

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.Notify += (title, text, warning) => _tray?.ShowBalloon(title, text, warning);

        SourceInitialized += OnSourceInitialized;
        StateChanged += OnStateChanged;
        IsVisibleChanged += (_, _) =>
        {
            _vm.SetUiVisible(IsVisible && WindowState != WindowState.Minimized);
            if (IsVisible) PromptPendingUpdate();
        };
        Closing += OnClosing;
        Closed += OnClosed;

        // El bucle de sensores arranca aunque la ventana empiece oculta en la bandeja.
        Dispatcher.BeginInvoke(Start);
    }

    private void CreateTray()
    {
        _tray = new TrayIcon(ShowFromTray, ToggleOverlay, _vm.ToggleLogging, _vm.OpenLogFolder, ExitApp);
        _tray.SetTooltip(_vm.TrayTooltip);
    }

    private async void Start()
    {
        CreateTray();
        Loc.LanguageChanged += () => { _tray?.Dispose(); CreateTray(); };
        bool devPreview = App.IsDevInstance && Environment.GetCommandLineArgs().Contains("--open-settings");
        if (devPreview) _vm.DevOverlayPreview = true;
        if (App.IsDevInstance && Environment.GetCommandLineArgs().Contains("--full-view")) _vm.Settings.SimpleView = false; // sin guardar
        if (_startHidden || (_vm.Settings.StartMinimized && !devPreview))
        {
            new WindowInteropHelper(this).EnsureHandle();
            _vm.SetUiVisible(false);
        }
        else
        {
            Show();
        }
        RegisterHotkeys(showErrors: !_startHidden);
        ApplyOverlayVisibility();
        if (App.IsDevInstance && Environment.GetCommandLineArgs().Contains("--open-settings"))
            _ = Dispatcher.BeginInvoke(() => OnSettingsClick(this, new RoutedEventArgs()), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        StartUpdateChecks();
        await _vm.RunAsync(_cts.Token);
    }

    // ------------------------------------------------------------------ Actualizaciones

    private DispatcherTimer? _updateTimer;
    private UpdateInfo? _pendingUpdate;
    private bool _updateDeclined;
    private bool _updateBusy;
    private bool _updateBalloonShown;

    private void StartUpdateChecks()
    {
        if (Environment.GetCommandLineArgs().Contains(Updater.UpdatedArg))
            _tray?.ShowBalloon(Loc.T("MandanHeat actualizado"), Loc.F("Ya tienes la versión {0}.", Updater.CurrentVersionText));

        if (App.IsDevInstance || !Updater.CanSelfUpdate) return;
        // Primera comprobación poco después de arrancar y luego cada 6 horas (la app suele pasar días en la bandeja).
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(6);
            if (_vm.Settings.CheckForUpdates) await CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();
    }

    /// <summary>Busca una versión nueva. Devuelve el texto de estado que muestra Ajustes.</summary>
    public async Task<string> CheckForUpdatesAsync(bool manual)
    {
        if (!Updater.CanSelfUpdate)
            return Loc.T("Las actualizaciones automáticas solo funcionan con el ejecutable descargado de GitHub.");
        if (_updateBusy) return Loc.T("Actualización en curso…");

        UpdateInfo? info;
        try
        {
            info = await Updater.CheckAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            return Loc.F("No se pudo comprobar: {0}", ex.Message);
        }

        if (info == null)
        {
            _pendingUpdate = null;
            return Loc.F("Tienes la última versión ({0}).", Updater.CurrentVersionText);
        }

        bool isNew = _pendingUpdate?.Version != info.Version;
        _pendingUpdate = info;
        if (isNew) _updateDeclined = false;

        if (manual || (IsVisible && WindowState != WindowState.Minimized))
        {
            await PromptUpdateAsync(manual);
        }
        else if (!_updateBalloonShown || isNew)
        {
            // Con la ventana oculta (o en pleno juego) no se interrumpe: se avisa y se pregunta al abrirla.
            _updateBalloonShown = true;
            _tray?.ShowBalloon(Loc.F("MandanHeat {0} disponible", info.VersionText), Loc.T("Abre MandanHeat para instalarla."));
        }
        return Loc.F("Versión {0} disponible.", info.VersionText);
    }

    private void PromptPendingUpdate()
    {
        if (_pendingUpdate == null || _updateDeclined || _updateBusy) return;
        _ = Dispatcher.BeginInvoke(async () => await PromptUpdateAsync(manual: false), DispatcherPriority.ApplicationIdle);
    }

    private async Task PromptUpdateAsync(bool manual)
    {
        if (_pendingUpdate is not { } info || _updateBusy || (_updateDeclined && !manual)) return;
        _updateBusy = true;
        try
        {
            Window owner = _settings is { IsVisible: true } ? _settings : this;
            var msg = Loc.F("Hay una versión nueva de MandanHeat: {0} (tienes la {1}).", info.VersionText, Updater.CurrentVersionText)
                      + "\n\n" + Loc.T("¿Descargarla e instalarla ahora? La app se reiniciará sola.");
            if (_vm.IsLogging) msg += "\n\n" + Loc.T("El registro de métricas en curso se detendrá y se guardará.");
            if (MessageBox.Show(owner, msg, "MandanHeat", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                _updateDeclined = true;
                return;
            }
            await InstallUpdateAsync(info, owner);
        }
        finally
        {
            _updateBusy = false;
        }
    }

    private async Task InstallUpdateAsync(UpdateInfo info, Window owner)
    {
        var progressWindow = new UpdateProgressWindow(info.VersionText);
        progressWindow.Show();
        try
        {
            await Updater.InstallAsync(info, new Progress<double>(progressWindow.Report), _cts.Token);
        }
        catch (Exception ex)
        {
            progressWindow.Close();
            if (_cts.IsCancellationRequested) return;
            var r = MessageBox.Show(owner,
                Loc.T("No se pudo instalar la actualización:") + "\n" + ex.Message + "\n\n" + Loc.T("¿Abrir la página de descargas?"),
                "MandanHeat", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes) SystemInfo.OpenUrl(info.PageUrl);
            return;
        }

        progressWindow.SetInstalling();
        try
        {
            Updater.Restart(minimized: !IsVisible || WindowState == WindowState.Minimized);
        }
        catch (Exception ex)
        {
            progressWindow.Close();
            MessageBox.Show(owner, Loc.T("La actualización está instalada, pero no se pudo reiniciar la app. Ábrela de nuevo.") + "\n" + ex.Message,
                "MandanHeat", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        progressWindow.Close();
        ExitApp();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private void RegisterHotkeys(bool showErrors)
    {
        if (App.IsDevInstance) return;
        if (!_hotkeysAttached)
        {
            _hotkeys.Attach(this);
            _hotkeysAttached = true;
        }
        _hotkeys.UnregisterAll();
        var failed = new List<string>();
        var s = _vm.Settings;
        if (!_hotkeys.Register(s.OverlayHotkey, ToggleOverlay)) failed.Add(s.OverlayHotkey);
        if (!_hotkeys.Register(s.LogHotkey, _vm.ToggleLogging)) failed.Add(s.LogHotkey);
        if (!_hotkeys.Register(s.WindowHotkey, ToggleWindow)) failed.Add(s.WindowHotkey);
        if (failed.Count > 0 && showErrors)
            MessageBox.Show(this,
                Loc.F("No se pudo registrar: {0}.", string.Join(", ", failed)) + "\n" + Loc.T("Puede que otra app ya use esa combinación. Cámbiala en Ajustes."),
                "MandanHeat", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private bool _hotkeysAttached;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.OverlayEnabled):
                ApplyOverlayVisibility();
                break;
            case nameof(MainViewModel.OverlayPosition):
            case nameof(MainViewModel.OverlayScale):
                _overlay.Reposition();
                break;
            case nameof(MainViewModel.TrayTooltip):
                _tray?.SetTooltip(_vm.TrayTooltip);
                break;
        }
    }

    private void ToggleOverlay() => _vm.OverlayEnabled = !_vm.OverlayEnabled;

    private void ApplyOverlayVisibility()
    {
        if (_vm.OverlayEnabled || _vm.DevOverlayPreview) _overlay.ShowOverlay();
        else _overlay.HideOverlay();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_settings != null) { _settings.Activate(); return; }
        // Mientras se editan, los atajos no deben dispararse al pulsarlos en las cajas.
        _hotkeys.UnregisterAll();
        _settings = new SettingsWindow(_vm, () => CheckForUpdatesAsync(manual: true)) { Owner = this };
        _settings.Closed += (_, _) =>
        {
            _settings = null;
            RegisterHotkeys(showErrors: true);
        };
        _settings.Show();
    }

    // Minimizar = ocultar en la bandeja (el overlay y el registro siguen funcionando).
    private void OnStateChanged(object? sender, EventArgs e)
    {
        bool minimized = WindowState == WindowState.Minimized;
        _vm.SetUiVisible(!minimized && IsVisible);
        if (minimized)
        {
            if (_vm.Settings.MinimizeToTray) Hide();
            // Libera la memoria de la interfaz cuando ya no se dibuja.
            Task.Delay(1500).ContinueWith(_ => PowerSaving.TrimMemory());
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ToggleWindow()
    {
        if (IsVisible && WindowState != WindowState.Minimized) WindowState = WindowState.Minimized;
        else ShowFromTray();
    }

    /// <summary>Salir de verdad (menú de la bandeja).</summary>
    public void ExitApp()
    {
        _exiting = true;
        Close();
    }

    private bool _exiting;

    // La X no cierra: la app sigue en la bandeja (overlay, registro y atajos siguen activos).
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting || App.IsShuttingDown || !_vm.Settings.CloseToTray) return;
        e.Cancel = true;
        Hide();
        _vm.SetUiVisible(false);
        Task.Delay(1500).ContinueWith(_ => PowerSaving.TrimMemory());
        if (!_vm.Settings.CloseHintShown)
        {
            _vm.Settings.CloseHintShown = true;
            _vm.Settings.Save();
            _tray?.ShowBalloon(Loc.T("MandanHeat sigue funcionando"),
                Loc.T("Está en la bandeja, junto al reloj. Doble clic para abrirla; clic derecho → Salir para cerrarla."));
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _cts.Cancel();
        _updateTimer?.Stop();
        _settings?.Close();
        _hotkeys.Dispose();
        _tray?.Dispose();
        _overlay.Close();
        _vm.Dispose();
    }
}
