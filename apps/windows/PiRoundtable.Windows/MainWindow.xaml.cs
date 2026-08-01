using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PiRoundtable.Windows.ViewModels;

namespace PiRoundtable.Windows;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel(DispatcherQueue);
        RootDataContext = ViewModel;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
        Closed += MainWindow_Closed;
    }

    private object RootDataContext
    {
        set => ((FrameworkElement)Content).DataContext = value;
    }

    private async void StartMeeting_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            try
            {
                await ViewModel.StartMeetingAsync(ApiKeyBox.Password);
            }
            finally
            {
                ApiKeyBox.Password = string.Empty;
            }
        });
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
            await ViewModel.AddTemporaryRoleAsync(TemporaryRoleNameBox.Text);
            TemporaryRoleNameBox.Text = string.Empty;
        });
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

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        ViewModel.TerminateRuntimeForAppExit();
        _ = ViewModel.DisposeAsync().AsTask();
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
