using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using MandanHeat.Interop;
using MandanHeat.ViewModels;

namespace MandanHeat;

public partial class SettingsWindow : Window
{
    public SettingsWindow(MainViewModel main, Func<Task<string>> checkForUpdates)
    {
        InitializeComponent();
        DataContext = new SettingsViewModel(main, checkForUpdates);
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }
}
