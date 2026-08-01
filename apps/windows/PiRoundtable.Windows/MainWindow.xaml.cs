using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;

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
