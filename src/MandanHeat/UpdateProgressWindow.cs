using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MandanHeat.Interop;
using MandanHeat.Services;

namespace MandanHeat;

/// <summary>Ventanita con el progreso de la descarga de una actualización.</summary>
public sealed class UpdateProgressWindow : Window
{
    private readonly ProgressBar _bar;
    private readonly TextBlock _text;
    private readonly string _caption;

    public UpdateProgressWindow(string version)
    {
        _caption = Loc.F("Descargando MandanHeat {0}…", version);
        Title = "MandanHeat";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("WindowBackground");
        Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextBrush");
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("BodyFont");
        Icon = AppIcon.CreateImageSource();

        _text = new TextBlock { Text = _caption, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
        _bar = new ProgressBar
        {
            Height = 8,
            Minimum = 0,
            Maximum = 1,
            IsIndeterminate = true,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("AccentBrush"),
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("CardBrush"),
            BorderThickness = new Thickness(0),
        };
        var panel = new StackPanel { Margin = new Thickness(20, 18, 20, 20) };
        panel.Children.Add(_text);
        panel.Children.Add(_bar);
        Content = panel;

        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    public void Report(double fraction)
    {
        _bar.IsIndeterminate = false;
        _bar.Value = fraction;
        _text.Text = $"{_caption}  {fraction * 100:0} %";
    }

    public void SetInstalling()
    {
        _bar.IsIndeterminate = true;
        _text.Text = Loc.T("Instalando y reiniciando…");
    }
}
