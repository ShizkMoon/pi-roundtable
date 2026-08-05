using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using PiRoundtable.Windows.Controls;
using PiRoundtable.Windows.Services;
using PiRoundtable.Windows.ViewModels;
using PiRoundtable.Windows.Models;
using PiRoundtable.Windows.Services.Updater;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace PiRoundtable.Windows;

public sealed partial class MainWindow : Window
{
    private bool _initialized;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    // A wide display must not open a private context without an explicit user action.
    private bool _contextPaneRequested;
    private bool _secondaryPanesWereInline;
    private bool _rolePaneWasInline;
    private bool _externalLinkDialogOpen;
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
    private readonly WindowsApplicationCompositionRoot _compositionRoot;
    private readonly WindowsUpdateService _updateService;
    private VerifiedUpdateManifest? _availableUpdate;
    private CancellationTokenSource? _updateOperationCancellation;
    private Task? _updateOperationTask;
    private readonly DispatcherQueueTimer _toolApprovalDeadlineTimer;

    public MainViewModel ViewModel { get; }

    internal MainWindow(WindowsApplicationCompositionRoot compositionRoot)
    {
        _compositionRoot = compositionRoot ?? throw new ArgumentNullException(nameof(compositionRoot));
        InitializeComponent();
        _updateService = _compositionRoot.UpdateService;
        ViewModel = _compositionRoot.CreateMainViewModel(DispatcherQueue);
        RootDataContext = ViewModel;
        ViewModel.PendingToolApprovals.CollectionChanged += PendingToolApprovals_CollectionChanged;
        _toolApprovalDeadlineTimer = DispatcherQueue.CreateTimer();
        _toolApprovalDeadlineTimer.Interval = TimeSpan.FromSeconds(1);
        _toolApprovalDeadlineTimer.IsRepeating = true;
        _toolApprovalDeadlineTimer.Tick += (_, _) => ViewModel.RefreshToolApprovalDeadlines();
        _toolApprovalDeadlineTimer.Start();
        InitializeTranscriptFollowing();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
        Activated += MainWindow_Activated;
        AppWindow.Closing += MainWindow_Closing;
        CurrentVersionText.Text = $"当前版本 {_updateService.CurrentVersion.ToString(3)} · stable · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        // Re-evaluate the Win32 high-contrast state when the user returns from
        // Windows Settings. ElementTheme.Default continues to track system colors.
        if (_initialized && args.WindowActivationState != WindowActivationState.Deactivated)
        {
            ApplyTheme();
        }
    }

    private object RootDataContext
    {
        set => ((FrameworkElement)Content).DataContext = value;
    }

    private async void StartMeeting_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ViewModel.StartMeetingAsync());
    }

    private async void SetAgendaMode_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ViewModel.SetDiscussionModeAsync("agenda"));
    }

    private async void SetFreeDiscussionMode_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ViewModel.SetDiscussionModeAsync("free_discussion"));
    }

    private async void SetConvergenceMode_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ViewModel.SetDiscussionModeAsync("convergence"));
    }

    private async void PauseDiscussion_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ViewModel.SetDiscussionModeAsync("paused"));
    }

    private async void ResumeDiscussion_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ViewModel.ResumeDiscussionAsync());
    }

    private async void AdvanceAgenda_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ViewModel.AdvanceAgendaAsync());
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

    private async void MarkdownMessage_ExternalLinkRequested(
        object sender,
        ExternalLinkRequestedEventArgs args)
    {
        if (_externalLinkDialogOpen || sender is not Control invoker || Root.XamlRoot is null)
        {
            return;
        }
        _externalLinkDialogOpen = true;
        try
        {
            var address = new TextBox
            {
                Header = $"目标站点：{args.Uri.Host}",
                IsReadOnly = true,
                Text = args.Uri.AbsoluteUri,
                TextWrapping = TextWrapping.Wrap,
            };
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "在默认浏览器中打开外部链接？",
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "此链接来自模型生成内容。请核对完整地址；Pi Roundtable 不会在应用内加载该网页。",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        address,
                    },
                },
                PrimaryButtonText = "打开浏览器",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _ = await global::Windows.System.Launcher.LaunchUriAsync(args.Uri);
            }
        }
        finally
        {
            _externalLinkDialogOpen = false;
            invoker.Focus(FocusState.Programmatic);
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
            _publicForcePassesRemaining = 2;
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
            _privateForcePassesRemaining = 2;
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

    private void PendingToolApprovals_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (!_initialized || Root.XamlRoot is null)
        {
            return;
        }
        if (args.NewItems is { Count: > 0 } && args.NewItems[0] is ToolApprovalItem added)
        {
            var previousFocus = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
            ShowContextPanel(ToolApprovalPanel, previousFocus);
            FocusToolApproval(added);
            return;
        }
        if (args.OldItems is { Count: > 0 })
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var next = ViewModel.PendingToolApprovals.FirstOrDefault();
                if (next is not null)
                {
                    FocusToolApproval(next);
                }
                else
                {
                    _contextPaneInvoker?.Focus(FocusState.Programmatic);
                }
            });
        }
    }

    private void FocusToolApproval(ToolApprovalItem approval)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ToolApprovalList.ScrollIntoView(approval);
            if (ToolApprovalList.ContainerFromItem(approval) is ListViewItem container)
            {
                container.Focus(FocusState.Programmatic);
            }
            else
            {
                ToolApprovalList.Focus(FocusState.Programmatic);
            }
        });
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

    private async void MoveSession_Click(object sender, RoutedEventArgs e)
    {
        var current = ViewModel.SelectedSession;
        if (current is null)
        {
            return;
        }
        var groups = ViewModel.SessionGroups
            .Where(group => group.GroupId != current.GroupId)
            .ToArray();
        if (groups.Length == 0)
        {
            ViewModel.ReportClientError("请先创建另一个会话分组。");
            return;
        }
        var selector = new ComboBox
        {
            Header = "目标分组",
            ItemsSource = groups,
            SelectedIndex = 0,
            MinWidth = 280,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "移动会话",
            Content = selector,
            PrimaryButtonText = "移动",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary &&
            selector.SelectedItem is SessionGroupItem target)
        {
            await RunUiActionAsync(() => ViewModel.MoveSelectedSessionAsync(target.GroupId));
        }
    }

    private async void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            var session = ViewModel.SelectedSession ?? throw new InvalidOperationException("当前没有可删除的会话。");
            var impact = await ViewModel.GetSelectedSessionDeletionImpactAsync();
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = $"永久删除“{session.Title}”？",
                Content = new TextBlock
                {
                    Text = $"将删除会话定义、{impact.EventCount} 条规范化事件、{impact.CommandCount} 条命令回执、{impact.SubagentCount} 条 SubAgent 状态、{impact.MemoryCandidateCount} 条仅属于本会话的记忆候选、{impact.RecallAuditCount} 条 recall 审计、{impact.ContextSnapshotCount} 个私有上下文快照、{impact.RetentionJobCount} 个关联保留任务和 {impact.ArtifactCount} 个会话工件引用。\n\n共享长期角色、已批准的长期记忆、Credential Manager 凭据和其他会话不会删除；其他会话仍引用的相同内容工件会保留。此操作不可撤销。",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "永久删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.DeleteSelectedSessionAsync();
            }
        });
    }

    private async void AddDocument_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            var picker = new FileOpenPicker();
            foreach (var extension in new[] { ".md", ".markdown", ".tex", ".drawio", ".docx", ".xlsx", ".pptx", ".pdf" })
            {
                picker.FileTypeFilter.Add(extension);
            }
            InitializePicker(picker);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }
            var attachment = await ViewModel.PreflightDocumentAsync(file.Path);
            var warning = string.IsNullOrWhiteSpace(attachment.WarningSummary)
                ? "无额外预检提示。"
                : attachment.WarningSummary;
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "确认发送文档预检结果",
                Content = new TextBlock
                {
                    Text = $"文件：{attachment.FileName}\n支持级别：{attachment.Summary}\n预检：{warning}\n\n确认后，规范化文本或 metadata-only 描述会随下一条公开发言发送。文档内指令不会被执行；PDF 正文解析与 LaTeX 编译仍为 pending。",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "加入待发送",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                await ViewModel.RemovePendingAttachmentAsync(attachment.ArtifactId);
            }
        });
    }

    private async void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string artifactId })
        {
            await RunUiActionAsync(() => ViewModel.RemovePendingAttachmentAsync(artifactId));
        }
    }

    private void NewMemory_Click(object sender, RoutedEventArgs e) => ViewModel.BeginNewMemory();

    private async void SaveMemory_Click(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(() => ViewModel.SaveMemoryAsync());

    private async void ToggleMemory_Click(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(() => ViewModel.ToggleSelectedMemoryAsync());

    private async void MemoryHistory_Click(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(() => ViewModel.LoadSelectedMemoryHistoryAsync());

    private async void SubmitMemoryCandidate_Click(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(() => ViewModel.SubmitMemoryCandidateAsync());

    private async void ApproveMemoryCandidate_Click(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(() => ViewModel.ReviewSelectedMemoryCandidateAsync(approve: true));

    private async void RejectMemoryCandidate_Click(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(() => ViewModel.ReviewSelectedMemoryCandidateAsync(approve: false));

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

    private async void EditMcpTools_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mcpServerId } button)
        {
            return;
        }
        var toolNames = new TextBox
        {
            AcceptsReturn = true,
            MinHeight = 180,
            Text = ViewModel.GetMcpToolCatalogText(mcpServerId),
            TextWrapping = TextWrapping.NoWrap,
            Header = "每行一个 MCP 工具名称",
            PlaceholderText = "read_file\nsearch_docs",
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "复核 MCP 工具清单",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "名称必须与 MCP tools/list 返回值完全一致。保存目录不等于授权；角色仍需逐项勾选。删除名称会撤销所有角色对该工具的授权。",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    toolNames,
                },
            },
            PrimaryButtonText = "保存复核清单",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await RunUiActionAsync(() => ViewModel.UpdateMcpToolCatalogAsync(mcpServerId, toolNames.Text));
        }
        button.Focus(FocusState.Programmatic);
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

    private async void ExportSessionJson_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ExportSessionAsync(markdown: false));
    }

    private async void ExportSessionMarkdown_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ExportSessionAsync(markdown: true));
    }

    private async Task ExportSessionAsync(bool markdown)
    {
        var includePrivate = new CheckBox
        {
            Content = "明确包含私聊内容与 audience",
            IsChecked = false,
        };
        var scopeDialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "选择导出范围",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "默认只导出公开消息。导出包不会包含 API Key、Credential Manager 引用、DPAPI 密文、raw Pi 记录、工具参数/结果或模型私有推理。",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    includePrivate,
                },
            },
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await scopeDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var package = ViewModel.CreateSelectedSessionExport(includePrivate.IsChecked == true);
        var picker = new FileSavePicker
        {
            SuggestedFileName = MakeSafeExportFileName(package.Title),
        };
        if (markdown)
        {
            picker.FileTypeChoices.Add("Markdown 会话记录", [".md"]);
        }
        else
        {
            picker.FileTypeChoices.Add("Pi Roundtable 会话包", [".json"]);
        }
        InitializePicker(picker);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }
        if (markdown)
        {
            await File.WriteAllTextAsync(file.Path, SessionTransferService.RenderMarkdown(package));
        }
        else
        {
            await File.WriteAllBytesAsync(file.Path, SessionTransferService.SerializeJson(package));
        }
        UpdateStatusTextIfVisible($"已导出 {package.Messages.Count} 条{(package.IncludesPrivateMessages ? "公开/私聊" : "公开")}消息。", file.Path);
    }

    private async void ImportSessionPackage_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(ImportSessionPackageAsync);
    }

    private async Task ImportSessionPackageAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }
        var properties = await file.GetBasicPropertiesAsync();
        if (properties.Size > SessionTransferService.MaximumPackageBytes)
        {
            throw new InvalidDataException("会话包超过 32 MiB 预检限制。");
        }
        var preflight = SessionTransferService.Preflight(await File.ReadAllBytesAsync(file.Path));
        var range = preflight.FirstMessageAt is null
            ? "无消息"
            : $"{preflight.FirstMessageAt.Value.ToLocalTime():yyyy-MM-dd HH:mm} 至 {preflight.LastMessageAt!.Value.ToLocalTime():yyyy-MM-dd HH:mm}";
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "导入预检通过",
            Content = new TextBlock
            {
                Text = $"来源：{preflight.Package.Title}\n公开消息：{preflight.PublicMessageCount}\n私聊消息：{preflight.PrivateMessageCount}\n发言者：{preflight.SpeakerCount}\n时间范围：{range}\n\n确认后只会创建新的独立草稿会话；不会覆盖、合并或追加到任何现有会话。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "创建新草稿",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ImportSessionPackageAsync(preflight);
        }
    }

    private void InitializePicker(object picker)
    {
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private static string MakeSafeExportFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(title.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "Pi-Roundtable-Session" : safe[..Math.Min(safe.Length, 80)];
    }

    private void UpdateStatusTextIfVisible(string message, string path)
    {
        ViewModel.ReportClientStatus($"{message} 文件：{path}");
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_shutdownStarted || _updateOperationCancellation is not null)
        {
            return;
        }
        var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _updateOperationCancellation = cancellation;
        _updateOperationTask = completion.Task;
        SetUpdateControlsBusy(true);
        UpdateStatusText.Text = "正在下载并验证签名更新清单…";
        try
        {
            var result = await _updateService.CheckAsync(cancellation.Token);
            if (result.Availability == UpdateAvailability.Available)
            {
                _availableUpdate = result.Manifest;
                InstallUpdateButton.IsEnabled = true;
                UpdateStatusText.Text = $"发现已验证更新 {result.AvailableVersion.ToString(3)}（发布于 {result.Manifest.PublishedAt.ToLocalTime():yyyy-MM-dd HH:mm}）。";
            }
            else
            {
                _availableUpdate = null;
                InstallUpdateButton.IsEnabled = false;
                UpdateStatusText.Text = result.AvailableVersion < result.CurrentVersion
                    ? $"已安装版本 {result.CurrentVersion.ToString(3)} 高于通道版本 {result.AvailableVersion.ToString(3)}，不会降级。"
                    : $"当前已是最新版本 {result.CurrentVersion.ToString(3)}。";
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText.Text = "更新检查已取消。";
        }
        catch (Exception exception)
        {
            _availableUpdate = null;
            InstallUpdateButton.IsEnabled = false;
            UpdateStatusText.Text = "检查失败；未下载或执行任何安装包。";
            ViewModel.ReportClientError($"检查更新失败：{exception.Message}");
        }
        finally
        {
            try
            {
                cancellation.Dispose();
                if (ReferenceEquals(_updateOperationCancellation, cancellation))
                {
                    _updateOperationCancellation = null;
                }
                if (!_shutdownStarted)
                {
                    SetUpdateControlsBusy(false);
                }
            }
            finally
            {
                completion.TrySetResult();
                if (ReferenceEquals(_updateOperationTask, completion.Task))
                {
                    _updateOperationTask = null;
                }
            }
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_shutdownStarted || _availableUpdate is null || _updateOperationCancellation is not null)
        {
            return;
        }
        var manifest = _availableUpdate;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = $"安装 Pi Roundtable {manifest.Version.ToString(3)}？",
            Content = "客户端会先下载到用户数据目录并完成签名清单、精确大小与 SHA-256 校验。通过后将正常结束当前会话和本地 Runtime，再显示 Windows UAC。安装成功后自动重新打开客户端。",
            PrimaryButtonText = "下载并安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        var dialogResult = await dialog.ShowAsync();
        if (_shutdownStarted)
        {
            return;
        }
        if (dialogResult != ContentDialogResult.Primary)
        {
            InstallUpdateButton.Focus(FocusState.Programmatic);
            return;
        }

        var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _updateOperationCancellation = cancellation;
        _updateOperationTask = completion.Task;
        SetUpdateControlsBusy(true);
        UpdateProgressBar.Value = 0;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateStatusText.Text = "正在下载并逐字节验证更新包…";
        try
        {
            var progress = new Progress<double>(value =>
            {
                UpdateProgressBar.Value = Math.Clamp(value * 100, 0, 100);
                UpdateStatusText.Text = $"正在下载并验证更新包… {UpdateProgressBar.Value:0}%";
            });
            var staged = await _updateService.DownloadAndStageAsync(
                manifest,
                progress,
                cancellation.Token);
            using var helper = _updateService.LaunchInstallerHelper(staged);
            UpdateStatusText.Text = "更新包已验证；正在安全结束客户端并移交 Windows Installer…";
            Close();
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText.Text = "更新下载已取消；未执行安装包。";
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = "更新失败；现有安装保持不变。";
            ViewModel.ReportClientError($"安装更新失败：{exception.Message}");
        }
        finally
        {
            try
            {
                cancellation.Dispose();
                if (ReferenceEquals(_updateOperationCancellation, cancellation))
                {
                    _updateOperationCancellation = null;
                }
                if (!_shutdownStarted)
                {
                    SetUpdateControlsBusy(false);
                    UpdateProgressBar.Visibility = Visibility.Collapsed;
                }
            }
            finally
            {
                completion.TrySetResult();
                if (ReferenceEquals(_updateOperationTask, completion.Task))
                {
                    _updateOperationTask = null;
                }
            }
        }
    }

    private void SetUpdateControlsBusy(bool busy)
    {
        CheckForUpdatesButton.IsEnabled = !busy;
        InstallUpdateButton.IsEnabled = !busy && _availableUpdate is not null;
    }

    private void ApplyTheme()
    {
        var mode = ThemePolicy.ResolveMode(
            ViewModel.ThemeMode,
            ThemePolicy.IsWindowsHighContrastEnabled(),
            ThemePolicy.GetVisualQaOverride(
                Environment.GetEnvironmentVariable(ThemePolicy.VisualQaEnabledVariable),
                Environment.GetEnvironmentVariable(ThemePolicy.VisualQaOverrideVariable)));
        Root.RequestedTheme = mode switch
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
        DiscussionQueueText.Visibility = width >= 1180 ? Visibility.Visible : Visibility.Collapsed;
        DiscussionBudgetText.Visibility = width >= 860 ? Visibility.Visible : Visibility.Collapsed;
        DiscussionAgendaText.MaxWidth = width >= 1180 ? 460 : width >= 860 ? 280 : 150;

        // Once the inline navigation pane narrows the meeting column, reserve a full row for
        // the editable title so it never competes with the primary meeting commands.
        var meetingHeaderStacked = width < 1180;
        Grid.SetRow(MeetingTitleStack, 0);
        Grid.SetColumn(MeetingTitleStack, 0);
        Grid.SetColumnSpan(MeetingTitleStack, meetingHeaderStacked ? 2 : 1);
        Grid.SetRow(MeetingCommandBar, meetingHeaderStacked ? 1 : 0);
        Grid.SetColumn(MeetingCommandBar, meetingHeaderStacked ? 0 : 1);
        Grid.SetColumnSpan(MeetingCommandBar, meetingHeaderStacked ? 2 : 1);
        MeetingHeaderGrid.RowSpacing = meetingHeaderStacked ? 2 : 0;
        MeetingCommandBar.DefaultLabelPosition = width >= 1280
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
        _toolApprovalDeadlineTimer.Stop();
        ViewModel.PendingToolApprovals.CollectionChanged -= PendingToolApprovals_CollectionChanged;
        _updateOperationCancellation?.Cancel();
        var updateOperation = _updateOperationTask;
        try
        {
            var disposeViewModel = ViewModel.DisposeAsync().AsTask();
            if (updateOperation is null)
            {
                await disposeViewModel;
            }
            else
            {
                await Task.WhenAll(disposeViewModel, updateOperation);
            }
        }
        finally
        {
            ViewModel.TerminateRuntimeForAppExit();
            _compositionRoot.Dispose();
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
