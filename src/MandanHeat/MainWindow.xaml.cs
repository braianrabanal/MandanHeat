using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
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

        _vm = new MainViewModel(AppSettings.Load());
        DataContext = _vm;
        _overlay = new OverlayWindow { DataContext = _vm };

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.Notify += (title, text, warning) => _tray?.ShowBalloon(title, text, warning);

        SourceInitialized += OnSourceInitialized;
        StateChanged += OnStateChanged;
        IsVisibleChanged += (_, _) => _vm.SetUiVisible(IsVisible && WindowState != WindowState.Minimized);
        Closing += OnClosing;
        Closed += OnClosed;

        // El bucle de sensores arranca aunque la ventana empiece oculta en la bandeja.
        Dispatcher.BeginInvoke(Start);
    }

    private async void Start()
    {
        _tray = new TrayIcon(ShowFromTray, ToggleOverlay, _vm.ToggleLogging, _vm.OpenLogFolder, ExitApp);
        bool devPreview = App.IsDevInstance && Environment.GetCommandLineArgs().Contains("--open-settings");
        if (devPreview) _vm.DevOverlayPreview = true;
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
        await _vm.RunAsync(_cts.Token);
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
                $"No se pudo registrar: {string.Join(", ", failed)}.\nPuede que otra app ya use esa combinación. Cámbiala en Ajustes.",
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
        _settings = new SettingsWindow(_vm) { Owner = this };
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
            _tray?.ShowBalloon("MandanHeat sigue funcionando",
                "Está en la bandeja, junto al reloj. Doble clic para abrirla; clic derecho → Salir para cerrarla.");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _cts.Cancel();
        _settings?.Close();
        _hotkeys.Dispose();
        _tray?.Dispose();
        _overlay.Close();
        _vm.Dispose();
    }
}
