using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using PiRoundtable.Windows.ViewModels;

namespace PiRoundtable.Windows;

public sealed partial class MainWindow : Window
{
    private bool _initialized;
    private bool _shutdownStarted;
    private bool _shutdownComplete;

    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel(DispatcherQueue);
        RootDataContext = ViewModel;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Closing += MainWindow_Closing;
    }

    private object RootDataContext
    {
        set => ((FrameworkElement)Content).DataContext = value;
    }

    private async void StartMeeting_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ViewModel.StartMeetingAsync());
    }

    private async void SendPrompt_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            if (await ViewModel.SendPromptAsync(PromptBox.Text))
            {
                PromptBox.Text = string.Empty;
                TranscriptList.ScrollIntoView(ViewModel.Transcript.LastOrDefault());
            }
        });
    }

    private async void Interrupt_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            if (await ViewModel.InterruptAsync(PromptBox.Text))
            {
                PromptBox.Text = string.Empty;
            }
        });
    }

    private async void CancelGeneration_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ViewModel.CancelActiveGenerationAsync());
    }

    private async void AddTemporaryRole_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            await ViewModel.AddTemporaryRoleAsync(
                ViewModel.TemporaryRoleName,
                ViewModel.TemporaryRolePurpose,
                ViewModel.TemporaryRoleSystemPrompt);
            if (!ViewModel.HasError)
            {
                ViewModel.TemporaryRoleName = string.Empty;
                ViewModel.TemporaryRolePurpose = string.Empty;
                ViewModel.TemporaryRoleSystemPrompt = string.Empty;
            }
        });
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        await RunUiActionAsync(() => ViewModel.InitializeAsync());
    }

    private async void NewSession_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ViewModel.CreateSessionAsync());
    }

    private void BeginNewProvider_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.BeginNewProvider();
        ProviderApiKeyBox.Password = string.Empty;
    }

    private void ShowRoleInspector_Click(object sender, RoutedEventArgs e)
    {
        ContextTabs.SelectedIndex = 0;
    }

    private void ShowProviderInspector_Click(object sender, RoutedEventArgs e)
    {
        ContextTabs.SelectedIndex = 1;
    }

    private async void SaveProvider_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            try
            {
                await ViewModel.SaveProviderConfigurationAsync(ProviderApiKeyBox.Password);
            }
            finally
            {
                ProviderApiKeyBox.Password = string.Empty;
            }
        });
    }

    private async void SaveRole_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ViewModel.SaveRoleConfigurationAsync());
    }

    private async void PromoteRole_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ViewModel.PromoteSelectedRoleAsync());
    }

    private async void ArchiveRole_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ViewModel.ArchiveSelectedRoleAsync());
    }

    private async void CloseMeeting_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ViewModel.CloseMeetingAsync());
    }

    private void ErrorInfoBar_CloseButtonClick(InfoBar sender, object args)
    {
        ViewModel.ClearError();
    }

    private async void MainWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_shutdownComplete)
        {
            return;
        }
        args.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }
        _shutdownStarted = true;
        try
        {
            await ViewModel.DisposeAsync();
        }
        finally
        {
            ViewModel.TerminateRuntimeForAppExit();
            _shutdownComplete = true;
            Close();
        }
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            ViewModel.ReportClientError("操作已取消。");
        }
        catch
        {
            ViewModel.ReportClientError("操作失败，请检查 Runtime Host 状态后重试。");
        }
    }
}
