using Microsoft.UI.Xaml;

namespace PiRoundtable.Windows;

public partial class App : Application
{
    private readonly WindowsApplicationCompositionRoot _compositionRoot = new();
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window ??= _compositionRoot.CreateMainWindow();
        _window.Activate();
    }
}
