using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MandanHeat.Services;

namespace MandanHeat.Controls;

/// <summary>Caja que captura una combinación de teclas. Retroceso/Supr/Esc la dejan vacía (atajo desactivado).</summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyBox)d).Refresh()));

    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        ToolTip = "Haz clic y pulsa la combinación (p. ej. Ctrl+Shift+F10). Retroceso para desactivar.";
        GotKeyboardFocus += (_, _) => Text = "Pulsa la combinación…";
        LostKeyboardFocus += (_, _) => Refresh();
    }

    private void Refresh() => Text = string.IsNullOrEmpty(Hotkey) ? "(desactivado)" : Hotkey;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.Back or Key.Delete or Key.Escape)
        {
            Hotkey = "";
            Keyboard.ClearFocus();
            return;
        }
        // Solo modificadores: esperar a la tecla principal.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.Tab)
            return;

        var mods = Keyboard.Modifiers;
        bool isFunctionKey = key >= Key.F1 && key <= Key.F24;
        if (mods == ModifierKeys.None && !isFunctionKey)
        {
            Text = "Usa Ctrl, Alt o Shift + tecla (o una tecla F)";
            return;
        }

        var text = HotkeyManager.Format(mods, key);
        if (HotkeyManager.TryParse(text, out _, out _))
        {
            Hotkey = text;
            Keyboard.ClearFocus();
        }
    }
}
