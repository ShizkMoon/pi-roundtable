using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace PiRoundtable.Windows;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
    }
}
