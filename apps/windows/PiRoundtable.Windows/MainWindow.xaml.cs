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
    private bool _contextPaneRequested = true;
    private bool _secondaryPanesWereInline;

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
                ShowContextPanel(PrivateChatPanel);
                if (ContextSplitView.DisplayMode == SplitViewDisplayMode.Overlay)
                {
                    ContextSplitView.IsPaneOpen = false;
                }
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

    private async void FetchProviderModels_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ViewModel.DiscoverProviderModelsAsync(ProviderApiKeyBox.Password));
    }

    private void SelectAllProviderModels_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectAllDiscoveredModels();
    }

    private async void ImportProviderModels_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ViewModel.ImportSelectedProviderModelsAsync());
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

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is null)
        {
            return;
        }

        ActivateSessionPage();
    }

    private void SessionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SessionItem session)
        {
            return;
        }

        if (!ReferenceEquals(ViewModel.SelectedSession, session) && !ViewModel.IsRunning)
        {
            ViewModel.SelectedSession = session;
        }
        SessionList.SelectedItem = ViewModel.SelectedSession;
        ActivateSessionPage();
    }

    private void ActivateSessionPage()
    {
        ShowPage(MeetingPage);
        if (ShellSplitView.DisplayMode == SplitViewDisplayMode.Overlay)
        {
            ShellSplitView.IsPaneOpen = false;
        }
    }

    private void NavigateRoles_Click(object sender, RoutedEventArgs e) => NavigateToPage(RoleManagementPage);

    private void NavigateSkills_Click(object sender, RoutedEventArgs e) => NavigateToPage(SkillPage);

    private void NavigateMcp_Click(object sender, RoutedEventArgs e) => NavigateToPage(McpPage);

    private void NavigateSettings_Click(object sender, RoutedEventArgs e) => NavigateToPage(SettingsPage);

    private void NavigateToPage(FrameworkElement page)
    {
        ShowPage(page);
        if (ShellSplitView.DisplayMode == SplitViewDisplayMode.Overlay)
        {
            ShellSplitView.IsPaneOpen = false;
        }
    }

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
        ShowContextPanel(PrivateChatPanel);
    }

    private void OpenInvitationPane_Click(object sender, RoutedEventArgs e)
    {
        ShowContextPanel(InvitationPanel);
    }

    private void CloseContextPane_Click(object sender, RoutedEventArgs e)
    {
        _contextPaneRequested = false;
        ContextSplitView.IsPaneOpen = false;
    }

    private void BackToPrivateChat_Click(object sender, RoutedEventArgs e)
    {
        ShowContextPanel(PrivateChatPanel);
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
        ShowContextPanel(RoleDetailPanel);
    }

    private void ShowContextPanel(FrameworkElement panel)
    {
        PrivateChatPanel.Visibility = panel == PrivateChatPanel ? Visibility.Visible : Visibility.Collapsed;
        RoleDetailPanel.Visibility = panel == RoleDetailPanel ? Visibility.Visible : Visibility.Collapsed;
        InvitationPanel.Visibility = panel == InvitationPanel ? Visibility.Visible : Visibility.Collapsed;
        _contextPaneRequested = true;
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

    private async void ImportMcp_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ViewModel.ImportMcpCatalogEntryAsync());
    }

    private async void ApproveSkill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string skillId })
        {
            await RunUiActionAsync(() => ViewModel.ApproveSkillAsync(skillId));
        }
    }

    private async void ApproveMcp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mcpServerId })
        {
            await RunUiActionAsync(() => ViewModel.ApproveMcpAsync(mcpServerId));
        }
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
        var shellPaneWidth = Math.Clamp(width - 48, 240, 288);
        var contextPaneWidth = Math.Clamp(width - 48, 280, 392);
        var rolePaneWidth = Math.Clamp(width - 48, 260, 300);
        ShellSplitView.OpenPaneLength = shellPaneWidth;
        ContextSplitView.OpenPaneLength = contextPaneWidth;
        RoleManagementSplitView.OpenPaneLength = rolePaneWidth;

        TitleSubtitle.Visibility = width >= 900 ? Visibility.Visible : Visibility.Collapsed;
        StatusBadge.Visibility = width >= 780 ? Visibility.Visible : Visibility.Collapsed;
        MeetingHeaderLabel.Visibility = width >= 700 ? Visibility.Visible : Visibility.Collapsed;
        MeetingSummaryText.Visibility = width >= 1180 ? Visibility.Visible : Visibility.Collapsed;
        MeetingCommandBar.DefaultLabelPosition = width >= 1180
            ? CommandBarDefaultLabelPosition.Right
            : CommandBarDefaultLabelPosition.Collapsed;

        var secondaryPanesInline = width >= 1360;
        if (secondaryPanesInline)
        {
            ShellSplitView.DisplayMode = SplitViewDisplayMode.Inline;
            ShellSplitView.IsPaneOpen = true;
            ContextSplitView.DisplayMode = SplitViewDisplayMode.Inline;
            ContextSplitView.IsPaneOpen = MeetingPage.Visibility == Visibility.Visible && _contextPaneRequested;
            RoleManagementSplitView.DisplayMode = SplitViewDisplayMode.Inline;
            RoleManagementSplitView.IsPaneOpen = RoleManagementPage.Visibility == Visibility.Visible;
        }
        else if (width >= 900)
        {
            ShellSplitView.DisplayMode = SplitViewDisplayMode.Inline;
            ShellSplitView.IsPaneOpen = true;
            ContextSplitView.DisplayMode = SplitViewDisplayMode.Overlay;
            RoleManagementSplitView.DisplayMode = SplitViewDisplayMode.Overlay;
            if (_secondaryPanesWereInline || MeetingPage.Visibility != Visibility.Visible)
            {
                ContextSplitView.IsPaneOpen = false;
            }
            if (_secondaryPanesWereInline || RoleManagementPage.Visibility != Visibility.Visible)
            {
                RoleManagementSplitView.IsPaneOpen = false;
            }
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
        _secondaryPanesWereInline = secondaryPanesInline;
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
