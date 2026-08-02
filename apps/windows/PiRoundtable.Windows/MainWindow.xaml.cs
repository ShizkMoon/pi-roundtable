using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using PiRoundtable.Windows.ViewModels;
using PiRoundtable.Windows.Models;
using Windows.Graphics;

namespace PiRoundtable.Windows;

public sealed partial class MainWindow : Window
{
    private bool _initialized;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private bool _contextPaneRequested = true;
    private bool _secondaryPanesWereInline;
    private bool _rolePaneWasInline;
    private Control? _contextPaneInvoker;
    private readonly HashSet<TranscriptItem> _observedPublicItems = [];
    private readonly HashSet<TranscriptItem> _observedPrivateItems = [];
    private ObservableCollection<TranscriptItem>? _observedPublicTranscript;
    private ObservableCollection<TranscriptItem>? _observedPrivateTranscript;
    private ScrollViewer? _publicTranscriptScrollViewer;
    private ScrollViewer? _privateTranscriptScrollViewer;
    private DispatcherQueueTimer? _publicFollowTimer;
    private DispatcherQueueTimer? _privateFollowTimer;
    private bool _publicFollowsLatest = true;
    private bool _privateFollowsLatest = true;
    private int _publicForcePassesRemaining;
    private int _privateForcePassesRemaining;

    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel(DispatcherQueue);
        RootDataContext = ViewModel;
        InitializeTranscriptFollowing();
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
        await SendPublicPromptAsync();
    }

    private async void SendPrivate_Click(object sender, RoutedEventArgs e)
    {
        await SendPrivatePromptAsync();
    }

    private async void SendPromptAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SendPublicPromptAsync();
    }

    private async void SendPrivateAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SendPrivatePromptAsync();
    }

    private Task SendPublicPromptAsync() => RunUiActionAsync(async () =>
    {
        if (await ViewModel.SendPromptAsync(PromptBox.Text))
        {
            PromptBox.Text = string.Empty;
            QueuePublicFollow(force: true);
        }
    });

    private Task SendPrivatePromptAsync() => RunUiActionAsync(async () =>
    {
        if (await ViewModel.SendPrivateMessageAsync(PrivatePromptBox.Text))
        {
            PrivatePromptBox.Text = string.Empty;
            QueuePrivateFollow(force: true);
        }
    });

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

    private async void RetryTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string messageId })
        {
            await RunUiActionAsync(async () =>
            {
                if (await ViewModel.RetryTranscriptAsync(messageId))
                {
                    QueuePublicFollow(force: true);
                }
            });
        }
    }

    private void ConfigureProvider_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.BeginNewProvider();
        ProviderApiKeyBox.Password = string.Empty;
        NavigateToPage(SettingsPage);
        DispatcherQueue.TryEnqueue(() => ProviderDisplayNameBox.Focus(FocusState.Programmatic));
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
        ApplyInitialWindowBounds();
        await RunUiActionAsync(() => ViewModel.InitializeAsync());
        ApplyTheme();
        ShowPage(MeetingPage);
        ApplyAdaptiveLayout(Root.ActualWidth);
        QueuePublicFollow(force: true);
        QueuePrivateFollow(force: true);
    }

    private void InitializeTranscriptFollowing()
    {
        _publicFollowTimer = DispatcherQueue.CreateTimer();
        _publicFollowTimer.Interval = TimeSpan.FromMilliseconds(80);
        _publicFollowTimer.IsRepeating = false;
        _publicFollowTimer.Tick += PublicFollowTimer_Tick;

        _privateFollowTimer = DispatcherQueue.CreateTimer();
        _privateFollowTimer.Interval = TimeSpan.FromMilliseconds(80);
        _privateFollowTimer.IsRepeating = false;
        _privateFollowTimer.Tick += PrivateFollowTimer_Tick;

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        RebindPublicTranscript(resetFollow: true);
        RebindPrivateTranscript(resetFollow: true);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Transcript))
        {
            RebindPublicTranscript(resetFollow: true);
        }
        else if (e.PropertyName == nameof(MainViewModel.PrivateMessages))
        {
            RebindPrivateTranscript(resetFollow: true);
        }
    }

    private void RebindPublicTranscript(bool resetFollow)
    {
        if (ReferenceEquals(_observedPublicTranscript, ViewModel.Transcript))
        {
            if (resetFollow)
            {
                QueuePublicFollow(force: true);
            }
            return;
        }

        if (_observedPublicTranscript is not null)
        {
            _observedPublicTranscript.CollectionChanged -= PublicTranscript_CollectionChanged;
        }
        foreach (var item in _observedPublicItems)
        {
            item.PropertyChanged -= PublicTranscriptItem_PropertyChanged;
        }
        _observedPublicItems.Clear();

        _observedPublicTranscript = ViewModel.Transcript;
        _observedPublicTranscript.CollectionChanged += PublicTranscript_CollectionChanged;
        foreach (var item in _observedPublicTranscript)
        {
            ObservePublicItem(item);
        }

        if (resetFollow)
        {
            _publicFollowsLatest = true;
            QueuePublicFollow(force: true);
        }
        UpdatePublicJumpButton();
    }

    private void RebindPrivateTranscript(bool resetFollow)
    {
        if (ReferenceEquals(_observedPrivateTranscript, ViewModel.PrivateMessages))
        {
            if (resetFollow)
            {
                QueuePrivateFollow(force: true);
            }
            return;
        }

        if (_observedPrivateTranscript is not null)
        {
            _observedPrivateTranscript.CollectionChanged -= PrivateTranscript_CollectionChanged;
        }
        foreach (var item in _observedPrivateItems)
        {
            item.PropertyChanged -= PrivateTranscriptItem_PropertyChanged;
        }
        _observedPrivateItems.Clear();

        _observedPrivateTranscript = ViewModel.PrivateMessages;
        _observedPrivateTranscript.CollectionChanged += PrivateTranscript_CollectionChanged;
        foreach (var item in _observedPrivateTranscript)
        {
            ObservePrivateItem(item);
        }

        if (resetFollow)
        {
            _privateFollowsLatest = true;
            QueuePrivateFollow(force: true);
        }
        UpdatePrivateJumpButton();
    }

    private void PublicTranscript_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateObservedItems(
            e,
            _observedPublicTranscript,
            _observedPublicItems,
            ObservePublicItem,
            item => item.PropertyChanged -= PublicTranscriptItem_PropertyChanged);
        QueuePublicFollow();
        UpdatePublicJumpButton();
    }

    private void PrivateTranscript_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateObservedItems(
            e,
            _observedPrivateTranscript,
            _observedPrivateItems,
            ObservePrivateItem,
            item => item.PropertyChanged -= PrivateTranscriptItem_PropertyChanged);
        QueuePrivateFollow();
        UpdatePrivateJumpButton();
    }

    private static void UpdateObservedItems(
        NotifyCollectionChangedEventArgs change,
        ObservableCollection<TranscriptItem>? collection,
        HashSet<TranscriptItem> observed,
        Action<TranscriptItem> observe,
        Action<TranscriptItem> unobserve)
    {
        if (change.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var item in observed)
            {
                unobserve(item);
            }
            observed.Clear();
            if (collection is not null)
            {
                foreach (var item in collection)
                {
                    observe(item);
                }
            }
            return;
        }

        if (change.OldItems is not null)
        {
            foreach (TranscriptItem item in change.OldItems)
            {
                unobserve(item);
                observed.Remove(item);
            }
        }
        if (change.NewItems is not null)
        {
            foreach (TranscriptItem item in change.NewItems)
            {
                observe(item);
            }
        }
    }

    private void ObservePublicItem(TranscriptItem item)
    {
        if (_observedPublicItems.Add(item))
        {
            item.PropertyChanged += PublicTranscriptItem_PropertyChanged;
        }
    }

    private void ObservePrivateItem(TranscriptItem item)
    {
        if (_observedPrivateItems.Add(item))
        {
            item.PropertyChanged += PrivateTranscriptItem_PropertyChanged;
        }
    }

    private void PublicTranscriptItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _observedPublicTranscript?.LastOrDefault()) &&
            (e.PropertyName == nameof(TranscriptItem.Text) || e.PropertyName == nameof(TranscriptItem.State)))
        {
            QueuePublicFollow();
        }
    }

    private void PrivateTranscriptItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _observedPrivateTranscript?.LastOrDefault()) &&
            (e.PropertyName == nameof(TranscriptItem.Text) || e.PropertyName == nameof(TranscriptItem.State)))
        {
            QueuePrivateFollow();
        }
    }

    private void TranscriptList_Loaded(object sender, RoutedEventArgs e)
    {
        AttachScrollViewer(TranscriptList, ref _publicTranscriptScrollViewer, PublicTranscriptScrollViewer_ViewChanged);
        QueuePublicFollow(force: true);
    }

    private void PrivateTranscriptList_Loaded(object sender, RoutedEventArgs e)
    {
        AttachScrollViewer(PrivateTranscriptList, ref _privateTranscriptScrollViewer, PrivateTranscriptScrollViewer_ViewChanged);
        QueuePrivateFollow(force: true);
    }

    private static void AttachScrollViewer(
        ListView list,
        ref ScrollViewer? current,
        EventHandler<ScrollViewerViewChangedEventArgs> handler)
    {
        var next = FindDescendant<ScrollViewer>(list);
        if (ReferenceEquals(next, current))
        {
            return;
        }
        if (current is not null)
        {
            current.ViewChanged -= handler;
        }
        current = next;
        if (current is not null)
        {
            current.ViewChanged += handler;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }
            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }
        return null;
    }

    private void PublicTranscriptScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            _publicFollowsLatest = TranscriptFollowPolicy.IsAtLatest(
                scrollViewer.VerticalOffset,
                scrollViewer.ScrollableHeight);
            UpdatePublicJumpButton();
        }
    }

    private void PrivateTranscriptScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            _privateFollowsLatest = TranscriptFollowPolicy.IsAtLatest(
                scrollViewer.VerticalOffset,
                scrollViewer.ScrollableHeight);
            UpdatePrivateJumpButton();
        }
    }

    private void QueuePublicFollow(bool force = false)
    {
        if (force)
        {
            _publicForcePassesRemaining = 6;
            _publicFollowsLatest = true;
        }
        if (!force && !_publicFollowsLatest)
        {
            return;
        }
        _publicFollowTimer?.Stop();
        _publicFollowTimer?.Start();
    }

    private void QueuePrivateFollow(bool force = false)
    {
        if (force)
        {
            _privateForcePassesRemaining = 6;
            _privateFollowsLatest = true;
        }
        if (!force && !_privateFollowsLatest)
        {
            return;
        }
        _privateFollowTimer?.Stop();
        _privateFollowTimer?.Start();
    }

    private void PublicFollowTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        var forcePass = _publicForcePassesRemaining > 0;
        if (forcePass)
        {
            _publicForcePassesRemaining--;
        }
        var shouldFollow = forcePass || _publicFollowsLatest;
        if (shouldFollow)
        {
            ScrollToLatest(TranscriptList, _observedPublicTranscript, _publicTranscriptScrollViewer);
            _publicFollowsLatest = true;
            UpdatePublicJumpButton();
        }
        if (_publicForcePassesRemaining > 0)
        {
            sender.Start();
        }
    }

    private void PrivateFollowTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        var forcePass = _privateForcePassesRemaining > 0;
        if (forcePass)
        {
            _privateForcePassesRemaining--;
        }
        var shouldFollow = forcePass || _privateFollowsLatest;
        if (shouldFollow)
        {
            ScrollToLatest(PrivateTranscriptList, _observedPrivateTranscript, _privateTranscriptScrollViewer);
            _privateFollowsLatest = true;
            UpdatePrivateJumpButton();
        }
        if (_privateForcePassesRemaining > 0)
        {
            sender.Start();
        }
    }

    private void ScrollToLatest(
        ListView list,
        ObservableCollection<TranscriptItem>? items,
        ScrollViewer? scrollViewer)
    {
        var latest = items?.LastOrDefault();
        if (latest is null)
        {
            return;
        }

        list.ScrollIntoView(latest);
        list.UpdateLayout();
        scrollViewer?.ChangeView(null, scrollViewer.ScrollableHeight, null, disableAnimation: true);
    }

    private void TranscriptJumpToLatest_Click(object sender, RoutedEventArgs e) => QueuePublicFollow(force: true);

    private void PrivateJumpToLatest_Click(object sender, RoutedEventArgs e) => QueuePrivateFollow(force: true);

    private void UpdatePublicJumpButton()
    {
        TranscriptJumpToLatestButton.Visibility = !_publicFollowsLatest && _observedPublicTranscript?.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdatePrivateJumpButton()
    {
        PrivateJumpToLatestButton.Visibility = !_privateFollowsLatest && _observedPrivateTranscript?.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        var confirmation = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "结束这场会议？",
            Content = "结束后会议进入终态，不能再恢复。若只是暂时离开，请选择“暂停”。",
            PrimaryButtonText = "结束会议",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() == ContentDialogResult.Primary)
        {
            await RunUiActionAsync(() => ViewModel.CloseMeetingAsync());
        }
    }

    private async void SuspendMeeting_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ViewModel.SuspendMeetingAsync());
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
        ShowContextPanel(PrivateChatPanel, sender as Control);
    }

    private void OpenInvitationPane_Click(object sender, RoutedEventArgs e)
    {
        ShowContextPanel(InvitationPanel, sender as Control);
    }

    private void OpenToolApprovalPane_Click(object sender, RoutedEventArgs e)
    {
        ShowContextPanel(ToolApprovalPanel, sender as Control);
    }

    private async void ApproveToolApproval_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string approvalId })
        {
            await RunUiActionAsync(() => ViewModel.ResolveToolApprovalAsync(approvalId, approved: true));
        }
    }

    private async void DenyToolApproval_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string approvalId })
        {
            await RunUiActionAsync(() => ViewModel.ResolveToolApprovalAsync(approvalId, approved: false));
        }
    }

    private void CloseContextPane_Click(object sender, RoutedEventArgs e)
    {
        _contextPaneRequested = false;
        ContextSplitView.IsPaneOpen = false;
        _contextPaneInvoker?.Focus(FocusState.Programmatic);
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
        ShowContextPanel(RoleDetailPanel, sender as Control);
    }

    private void ShowContextPanel(FrameworkElement panel, Control? invoker = null)
    {
        PrivateChatPanel.Visibility = panel == PrivateChatPanel ? Visibility.Visible : Visibility.Collapsed;
        RoleDetailPanel.Visibility = panel == RoleDetailPanel ? Visibility.Visible : Visibility.Collapsed;
        InvitationPanel.Visibility = panel == InvitationPanel ? Visibility.Visible : Visibility.Collapsed;
        ToolApprovalPanel.Visibility = panel == ToolApprovalPanel ? Visibility.Visible : Visibility.Collapsed;
        if (invoker is not null)
        {
            _contextPaneInvoker = invoker;
        }
        _contextPaneRequested = true;
        ContextSplitView.IsPaneOpen = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            Control focusTarget;
            if (panel == PrivateChatPanel)
            {
                focusTarget = PrivatePromptBox;
            }
            else if (panel == InvitationPanel)
            {
                focusTarget = TemporaryRoleNameBox;
            }
            else if (panel == RoleDetailPanel)
            {
                focusTarget = RoleDetailBackButton;
            }
            else
            {
                focusTarget = ToolApprovalPanel;
            }
            focusTarget.Focus(FocusState.Programmatic);
        });
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

    private void ApplyInitialWindowBounds()
    {
        var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var horizontalMargin = (int)Math.Round(48 * scale);
        var verticalMargin = (int)Math.Round(48 * scale);
        var targetWidth = Math.Min((int)Math.Round(1280 * scale), workArea.Width - horizontalMargin);
        var targetHeight = Math.Min((int)Math.Round(800 * scale), workArea.Height - verticalMargin);
        AppWindow.Resize(new SizeInt32(
            Math.Max((int)Math.Round(720 * scale), targetWidth),
            Math.Max((int)Math.Round(560 * scale), targetHeight)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Round(720 * scale);
            presenter.PreferredMinimumHeight = (int)Math.Round(560 * scale);
        }
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
        ParticipantStrip.Visibility = width >= 980 ? Visibility.Visible : Visibility.Collapsed;
        ParticipantCompactText.Visibility = width < 980 ? Visibility.Visible : Visibility.Collapsed;
        RuntimeStateText.MaxWidth = width >= 900 ? 320 : 180;
        MeetingCommandBar.DefaultLabelPosition = width >= 1180
            ? CommandBarDefaultLabelPosition.Right
            : CommandBarDefaultLabelPosition.Collapsed;

        var secondaryPanesInline = width >= 1520;
        var rolePaneInline = width >= 1260;
        if (secondaryPanesInline)
        {
            ShellSplitView.DisplayMode = SplitViewDisplayMode.Inline;
            ShellSplitView.IsPaneOpen = true;
            ContextSplitView.DisplayMode = SplitViewDisplayMode.Inline;
            ContextSplitView.IsPaneOpen = MeetingPage.Visibility == Visibility.Visible && _contextPaneRequested;
            RoleManagementSplitView.DisplayMode = rolePaneInline
                ? SplitViewDisplayMode.Inline
                : SplitViewDisplayMode.Overlay;
            RoleManagementSplitView.IsPaneOpen = rolePaneInline && RoleManagementPage.Visibility == Visibility.Visible;
        }
        else if (width >= 900)
        {
            ShellSplitView.DisplayMode = SplitViewDisplayMode.Inline;
            ShellSplitView.IsPaneOpen = true;
            ContextSplitView.DisplayMode = SplitViewDisplayMode.Overlay;
            RoleManagementSplitView.DisplayMode = rolePaneInline
                ? SplitViewDisplayMode.Inline
                : SplitViewDisplayMode.Overlay;
            if (_secondaryPanesWereInline || MeetingPage.Visibility != Visibility.Visible)
            {
                ContextSplitView.IsPaneOpen = false;
            }
            if ((_rolePaneWasInline && !rolePaneInline) || RoleManagementPage.Visibility != Visibility.Visible)
            {
                RoleManagementSplitView.IsPaneOpen = false;
            }
            else if (rolePaneInline)
            {
                RoleManagementSplitView.IsPaneOpen = RoleManagementPage.Visibility == Visibility.Visible;
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
        _rolePaneWasInline = rolePaneInline;
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
