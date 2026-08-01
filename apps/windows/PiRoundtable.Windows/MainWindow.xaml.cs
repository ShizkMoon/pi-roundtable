using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using PiRoundtable.Windows.ViewModels;
using PiRoundtable.Windows.Models;

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

    private async void SendPrivate_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            if (await ViewModel.SendPrivateMessageAsync(PrivatePromptBox.Text))
            {
                PrivatePromptBox.Text = string.Empty;
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
        ApplyTheme();
        ShowPage(MeetingPage);
        ApplyAdaptiveLayout(Root.ActualWidth);
    }

    private async void NewSession_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            await ViewModel.CreateSessionAsync();
            ShowPage(MeetingPage);
        });
    }

    private void BeginNewProvider_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.BeginNewProvider();
        ProviderApiKeyBox.Password = string.Empty;
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

    private void ToggleShellPane_Click(object sender, RoutedEventArgs e)
    {
        ShellSplitView.IsPaneOpen = !ShellSplitView.IsPaneOpen;
    }

    private void NavigateMeeting_Click(object sender, RoutedEventArgs e) => ShowPage(MeetingPage);

    private void NavigateRoles_Click(object sender, RoutedEventArgs e) => ShowPage(RoleManagementPage);

    private void NavigateSkills_Click(object sender, RoutedEventArgs e) => ShowPage(SkillPage);

    private void NavigateMcp_Click(object sender, RoutedEventArgs e) => ShowPage(McpPage);

    private void NavigateSettings_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage);

    private void ShowPage(FrameworkElement page)
    {
        foreach (var candidate in new FrameworkElement[]
                 {
                     MeetingPage,
                     RoleManagementPage,
                     SkillPage,
                     McpPage,
                     SettingsPage,
                 })
        {
            candidate.Visibility = candidate == page ? Visibility.Visible : Visibility.Collapsed;
        }

        ApplyAdaptiveLayout(Root.ActualWidth);
    }

    private void OpenPrivatePane_Click(object sender, RoutedEventArgs e)
    {
        PrivateChatPanel.Visibility = Visibility.Visible;
        RoleDetailPanel.Visibility = Visibility.Collapsed;
        ContextSplitView.IsPaneOpen = true;
    }

    private void CloseContextPane_Click(object sender, RoutedEventArgs e)
    {
        ContextSplitView.IsPaneOpen = false;
    }

    private void BackToPrivateChat_Click(object sender, RoutedEventArgs e)
    {
        RoleDetailPanel.Visibility = Visibility.Collapsed;
        PrivateChatPanel.Visibility = Visibility.Visible;
    }

    private void TranscriptSpeaker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string roleId })
        {
            return;
        }
        var role = ViewModel.Roles.FirstOrDefault(item => item.RoleId == roleId);
        if (role is null)
        {
            return;
        }
        ViewModel.SelectedRole = role;
        PrivateChatPanel.Visibility = Visibility.Collapsed;
        RoleDetailPanel.Visibility = Visibility.Visible;
        ContextSplitView.IsPaneOpen = true;
    }

    private void ToggleRoleList_Click(object sender, RoutedEventArgs e)
    {
        RoleManagementSplitView.IsPaneOpen = !RoleManagementSplitView.IsPaneOpen;
    }

    private async void NewSessionGroup_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { Header = "分组名称", PlaceholderText = "例如：产品设计" };
        var kindBox = new ComboBox
        {
            Header = "分组类型",
            ItemsSource = new[] { "文件夹", "项目" },
            SelectedIndex = 0,
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(nameBox);
        panel.Children.Add(kindBox);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "新建会话分组",
            Content = panel,
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await RunUiActionAsync(() => ViewModel.CreateSessionGroupAsync(
                nameBox.Text,
                kindBox.SelectedIndex == 1 ? "project" : "folder"));
        }
    }

    private async void NewLongTermRole_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { Header = "角色名称", PlaceholderText = "例如：系统架构师" };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "新建长期角色",
            Content = nameBox,
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await RunUiActionAsync(() => ViewModel.CreateLongTermRoleAsync(nameBox.Text));
        }
    }

    private async void SaveSkill_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ViewModel.SaveSkillCatalogEntryAsync());
    }

    private async void SaveMcp_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ViewModel.SaveMcpCatalogEntryAsync());
    }

    private async void SaveClientSettings_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            try
            {
                await ViewModel.SaveClientSettingsAsync(SyncCredentialBox.Password);
                ApplyTheme();
            }
            finally
            {
                SyncCredentialBox.Password = string.Empty;
            }
        });
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = ViewModel.ThemeMode switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyAdaptiveLayout(e.NewSize.Width);
    }

    private void ApplyAdaptiveLayout(double width)
    {
        if (width >= 1120)
        {
            ShellSplitView.DisplayMode = SplitViewDisplayMode.Inline;
            ShellSplitView.IsPaneOpen = true;
            ContextSplitView.DisplayMode = SplitViewDisplayMode.Inline;
            ContextSplitView.IsPaneOpen = MeetingPage.Visibility == Visibility.Visible;
            RoleManagementSplitView.DisplayMode = SplitViewDisplayMode.Inline;
            RoleManagementSplitView.IsPaneOpen = true;
        }
        else if (width >= 720)
        {
            ShellSplitView.DisplayMode = SplitViewDisplayMode.Inline;
            ShellSplitView.IsPaneOpen = true;
            ContextSplitView.DisplayMode = SplitViewDisplayMode.Overlay;
            ContextSplitView.IsPaneOpen = false;
            RoleManagementSplitView.DisplayMode = SplitViewDisplayMode.Inline;
            RoleManagementSplitView.IsPaneOpen = true;
        }
        else
        {
            ShellSplitView.DisplayMode = SplitViewDisplayMode.Overlay;
            ShellSplitView.IsPaneOpen = false;
            ContextSplitView.DisplayMode = SplitViewDisplayMode.Overlay;
            ContextSplitView.IsPaneOpen = false;
            RoleManagementSplitView.DisplayMode = SplitViewDisplayMode.Overlay;
            RoleManagementSplitView.IsPaneOpen = false;
        }
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
