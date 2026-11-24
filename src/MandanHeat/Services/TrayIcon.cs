using System;
using System.Drawing;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;

namespace MandanHeat.Services;

/// <summary>Icono en la bandeja del sistema con menú contextual.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;

    public TrayIcon(Action show, Action toggleOverlay, Action toggleLogging, Action openLogs, Action exit)
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Abrir MandanHeat", null, (_, _) => show());
        menu.Items.Add("Activar / desactivar overlay", null, (_, _) => toggleOverlay());
        menu.Items.Add("Iniciar / detener registro", null, (_, _) => toggleLogging());
        menu.Items.Add("Abrir carpeta de registros", null, (_, _) => openLogs());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => exit());

        _icon = new WinForms.NotifyIcon
        {
            Icon = AppIcon.CreateIcon(),
            Text = "MandanHeat",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => show();
    }

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void ShowBalloon(string title, string text, bool warning = false) =>
        _icon.ShowBalloonTip(4000, title, text, warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

/// <summary>Icono de la app (M con llamas) incrustado como recurso.</summary>
public static class AppIcon
{
    private static readonly Uri IcoUri = new("pack://application:,,,/Assets/MandanHeat.ico");

    /// <summary>Icono de Windows al tamaño pedido (16 px en la bandeja, escalado según el DPI).</summary>
    public static Icon CreateIcon(int size = 0)
    {
        if (size <= 0) size = WinForms.SystemInformation.SmallIconSize.Width;
        using var stream = System.Windows.Application.GetResourceStream(IcoUri).Stream;
        return new Icon(stream, size, size);
    }

    public static ImageSource CreateImageSource()
    {
        var frame = BitmapFrame.Create(IcoUri);
        frame.Freeze();
        return frame;
    }
}
