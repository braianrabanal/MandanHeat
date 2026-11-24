using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MandanHeat.Interop;
using MandanHeat.Services;
using MandanHeat.ViewModels;

namespace MandanHeat;

/// <summary>
/// Ventana transparente, siempre encima y que deja pasar los clics al juego.
/// Funciona sobre juegos en "pantalla completa sin bordes" o en ventana (lo habitual hoy en DX11/DX12/Vulkan).
/// </summary>
public partial class OverlayWindow : Window
{
    private const double EdgeMargin = 14;
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private IntPtr _hwnd;

    public OverlayWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Reposition();
        _topmostTimer.Tick += (_, _) => ForceTopmost();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    public void ShowOverlay()
    {
        if (!IsVisible) Show();
        Reposition();
        ForceTopmost();
        _topmostTimer.Start();
    }

    public void HideOverlay()
    {
        _topmostTimer.Stop();
        Hide();
    }

    /// <summary>Coloca el overlay en la esquina elegida del monitor principal.</summary>
    public void Reposition()
    {
        if (DataContext is not MainViewModel vm) return;
        double w = SystemParameters.PrimaryScreenWidth;
        double h = SystemParameters.PrimaryScreenHeight;
        var corner = vm.OverlayPosition;
        Left = corner is OverlayCorner.TopRight or OverlayCorner.BottomRight ? w - ActualWidth - EdgeMargin : EdgeMargin;
        Top = corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight ? h - ActualHeight - EdgeMargin : EdgeMargin;
    }

    // Algunos juegos se colocan por encima al recibir el foco; reafirmamos el "siempre encima".
    private void ForceTopmost()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}
