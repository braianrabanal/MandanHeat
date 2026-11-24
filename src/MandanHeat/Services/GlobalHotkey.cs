using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using MandanHeat.Interop;

namespace MandanHeat.Services;

/// <summary>Atajos de teclado globales (funcionan aunque el juego tenga el foco).</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly Dictionary<int, Action> _actions = new();
    private HwndSource? _source;
    private IntPtr _hwnd;
    private int _nextId = 0x4D48; // "MH"

    public void Attach(Window window)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(WndProc);
    }

    /// <summary>Registra un atajo. Devuelve false si el texto no es válido o si otra app ya lo usa. "" no registra nada.</summary>
    public bool Register(string text, Action action)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!TryParse(text, out var mods, out var vk)) return false;
        int id = _nextId++;
        if (!Native.RegisterHotKey(_hwnd, id, mods | Native.MOD_NOREPEAT, vk)) return false;
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) Native.UnregisterHotKey(_hwnd, id);
        _actions.Clear();
    }

    public static bool TryParse(string text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        var key = Key.None;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= Native.MOD_CONTROL; break;
                case "alt": modifiers |= Native.MOD_ALT; break;
                case "shift": case "mayus": case "mayús": modifiers |= Native.MOD_SHIFT; break;
                case "win": modifiers |= Native.MOD_WIN; break;
                default:
                    if (!Enum.TryParse(part, ignoreCase: true, out key)) return false;
                    break;
            }
        }
        if (key == Key.None) return false;
        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return true;
    }

    /// <summary>Texto normalizado ("Ctrl+Shift+F10") a partir de una pulsación.</summary>
    public static string Format(ModifierKeys mods, Key key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
