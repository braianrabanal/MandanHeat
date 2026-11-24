using System.Windows;
using System.Windows.Interop;
using MandanHeat.Interop;

namespace MandanHeat;

public partial class InputDialog : Window
{
    public InputDialog(string title, string prompt, string value)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = value;
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    public string Value => ValueBox.Text;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
