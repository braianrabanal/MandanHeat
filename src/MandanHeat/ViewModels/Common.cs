using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;

namespace MandanHeat.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}

/// <summary>Paleta compartida (debe coincidir con App.xaml).</summary>
public static class Palette
{
    public static readonly SolidColorBrush Text = Make(0xF3, 0xF3, 0xF6);
    public static readonly SolidColorBrush Muted = Make(0x8B, 0x8F, 0x9C);
    public static readonly SolidColorBrush Accent = Make(0xFF, 0x4A, 0x1C);
    public static readonly SolidColorBrush Good = Make(0x3D, 0xFF, 0xA2);
    public static readonly SolidColorBrush Warn = Make(0xFF, 0xB3, 0x00);
    public static readonly SolidColorBrush Hot = Make(0xFF, 0x1E, 0x3C);
    public static readonly SolidColorBrush Info = Make(0x4F, 0xC3, 0xFF);

    private static SolidColorBrush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Color por temperatura. El líquido de un AIO usa umbrales más bajos.</summary>
    public static SolidColorBrush ForTemperature(double celsius, bool liquid = false)
    {
        if (liquid) return celsius < 40 ? Good : celsius < 50 ? Warn : Hot;
        return celsius < 65 ? Good : celsius < 85 ? Warn : Hot;
    }

    public static SolidColorBrush ForFps(double fps) => fps >= 60 ? Good : fps >= 30 ? Warn : Hot;
}
