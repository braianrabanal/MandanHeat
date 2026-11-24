using System.Windows;
using System.Windows.Interop;
using MandanHeat.Interop;
using MandanHeat.ViewModels;

namespace MandanHeat;

public partial class SettingsWindow : Window
{
    public SettingsWindow(MainViewModel main)
    {
        InitializeComponent();
        DataContext = new SettingsViewModel(main);
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }
}
