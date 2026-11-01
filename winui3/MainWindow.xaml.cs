using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using ETSAssistant.WinUI3.Services;
using WinRT.Interop;
using Windows.Graphics;

namespace ETSAssistant.WinUI3;

public sealed partial class MainWindow : Window
{
    internal new static MainWindow? Current { get; private set; }
    internal Microsoft.UI.Xaml.Controls.WebView2 AnalysisPdfRenderer => SharedPdfRendererControl;
    internal bool IsShowingPage(Page page) => ReferenceEquals(PageFrame.Content, page);
    private readonly CurrentPaperService _paperService = new(AssistantService.Current.DataRoot);
    private readonly SpeakingAnswerService _speakingAnswers = new(AssistantService.Current.DataRoot);
    private DispatcherQueueTimer? _paperTimer;
    private DispatcherQueueTimer? _audioSessionTimer;
    private string? _lastPrepPackId;
    private string? _promptedMissingCredentialsPackId;
    private bool _checkingSpeakingPaper;
    private bool _checkingAudioSession;
    private bool _audioSessionActive;
    private SpeakingPrepWindow? _prepWindow;
    private DispatcherQueueTimer? _paneClosingTimer;
    private string? _currentPageName;
    private bool _windowOpenedLogged;
    private string? _lastAudioSessionError;
    private string? _lastSpeakingPaperError;

    public MainWindow()
    {
        InitializeComponent();
        Current = this;
        AnalysisActivityService.Current.Changed += UpdateActivityNavigation;
        UpdateActivityNavigation();
        Closed += async (_, _) =>
        {
            DiagnosticLogService.Info("etstool.window.closed");
            AnalysisActivityService.Current.Changed -= UpdateActivityNavigation;
            AppPreferencesService.Current.EtsProgramIntegrationChanged -= OnEtsProgramIntegrationChanged;
            EtsWindowPreviewService.RestoreAll();
            _audioSessionTimer?.Stop();
            try { await DefaultAudioInputService.Current.EndSessionAsync(); }
            catch (Exception) { }
            Current = null;
        };
        Title = "ETS Tool by @azursmelt";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
        SetWindowSize(1120, 800);
        SetAppIcon();
        SetTitleBarColors();

        AppPreferencesService preferences = AppPreferencesService.Current;
        DiagnosticLogService.SetVerboseEnabled(preferences.VerboseLoggingEnabled);
        EtsPreviewNavigationItem.Visibility = preferences.IntegrateEtsMainProgramEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        preferences.EtsProgramIntegrationChanged += OnEtsProgramIntegrationChanged;
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
                DiagnosticLogService.Info("etstool.window.deactivated");
            else
            {
                if (!_windowOpenedLogged)
                {
                    _windowOpenedLogged = true;
                    DiagnosticLogService.Info("etstool.window.opened");
                }
                else
                    DiagnosticLogService.Info("etstool.window.activated");
            }
        };
        foreach (object item in RootNavigation.MenuItems)
        {
            if (item is NavigationViewItem nav && (nav.Tag as string) == "tools")
                nav.Visibility = preferences.DeveloperToolsUnlocked ? Visibility.Visible : Visibility.Collapsed;
        }
        LocalizationService.Current.Load(preferences.Language);
        ApplyTheme();
        ApplyLocalization();
        PageFrame.Navigated += (_, args) =>
        {
            string pageName = args.Content?.GetType().Name ?? "UnknownPage";
            if (_currentPageName is not null && !string.Equals(_currentPageName, pageName, StringComparison.Ordinal))
                DiagnosticLogService.Info("navigation.page.closed", $"page={_currentPageName}");
            DiagnosticLogService.Info("navigation.page.opened", $"page={pageName}");
            _currentPageName = pageName;
            if (args.Content is DependencyObject page)
                DispatcherQueue.TryEnqueue(() =>
                {
                    LocalizationService.Current.Apply(page);
                    if (page is Page responsivePage)
                        ResponsiveLayoutService.Attach(responsivePage);
                    if (page is FrameworkElement interactivePage)
                        DiagnosticLogService.TrackInteractiveControls(interactivePage, pageName);
                });
        };
        preferences.ThemeChanged += ApplyTheme;
        preferences.LanguageChanged += OnLanguageChanged;

        DispatcherQueueTimer localizationTimer = DispatcherQueue.CreateTimer();
        localizationTimer.Interval = TimeSpan.FromSeconds(1);
        localizationTimer.Tick += (_, _) => ApplyLocalization();
        localizationTimer.Start();
        Closed += (_, _) => localizationTimer.Stop();

        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        PageFrame.Navigate(typeof(Pages.OverviewPage));
        RootGrid.Loaded += async (_, _) =>
        {
            UpdatePaneToggleButtonStyle(RootNavigation.DisplayMode);
            _audioSessionTimer = DispatcherQueue.CreateTimer();
            _audioSessionTimer.Interval = TimeSpan.FromSeconds(1);
            _audioSessionTimer.Tick += async (_, _) => await SyncAudioSessionWithEtsAsync();
            _audioSessionTimer.Start();
            await SyncAudioSessionWithEtsAsync();
            await ShowWelcomeAsync();
            await CheckVirtualCableAsync();
            await CheckStartupForUpdatesAsync();
        };

        _paperTimer = DispatcherQueue.CreateTimer();
        _paperTimer.Interval = TimeSpan.FromSeconds(3);
        _paperTimer.Tick += async (_, _) => await CheckSpeakingPaperAsync();
        _paperTimer.Start();
        Closed += (_, _) => _paperTimer.Stop();
    }

    private async Task SyncAudioSessionWithEtsAsync()
    {
        if (_checkingAudioSession) return;
        _checkingAudioSession = true;
        try
        {
            AssistantService assistant = AssistantService.Current;
            bool etsRunning = assistant.IsEtsProcessRunning;
            if (!etsRunning)
            {
                if (_audioSessionActive)
                    await DefaultAudioInputService.Current.EndSessionAsync();
                _audioSessionActive = false;
                _lastAudioSessionError = null;
                return;
            }

            DefaultAudioInputStatus? currentInput = await DefaultAudioInputService.Current.GetStatusAsync();
            if (currentInput?.IsVirtualCable == true)
            {
                _audioSessionActive = true;
                _lastAudioSessionError = null;
                return;
            }

            string result = await DefaultAudioInputService.Current.BeginSessionAsync();
            DefaultAudioInputStatus? input = await DefaultAudioInputService.Current.GetStatusAsync();
            if (input?.IsVirtualCable != true)
            {
                string detail = $"ETS 正在运行，但默认录音设备未切换到 VB-CABLE。当前：{input?.Name ?? "无法读取"}；{result}";
                if (!string.Equals(_lastAudioSessionError, detail, StringComparison.Ordinal))
                {
                    DiagnosticLogService.Warn("audio_session.sync.virtual_mic_not_active", detail);
                    _lastAudioSessionError = detail;
                }
                _audioSessionActive = false;
                return;
            }

            _audioSessionActive = true;
            _lastAudioSessionError = null;
        }
        catch (Exception ex)
        {
            if (!string.Equals(_lastAudioSessionError, ex.Message, StringComparison.Ordinal))
            {
                DiagnosticLogService.Warn("audio_session.sync.failed", ex.Message);
                _lastAudioSessionError = ex.Message;
            }
        }
        finally { _checkingAudioSession = false; }
    }

    private async Task CheckSpeakingPaperAsync()
    {
        if (_checkingSpeakingPaper) return;
        _checkingSpeakingPaper = true;
        try
        {
            string? packId = await CurrentPaperService.FindActivePackIdAsync();
            if (packId is null)
            {
                _lastPrepPackId = null;
                _promptedMissingCredentialsPackId = null;
                return;
            }
            if (string.Equals(packId, _lastPrepPackId, StringComparison.Ordinal)) return;

            IReadOnlyList<SpeakingQuestion> questions = _speakingAnswers.Load(packId);
            if (questions.Count == 0) return;

            bool credentialsMissing = !AliyunTtsCredentialStore.HasCredentials ||
                AppPreferencesService.Current.SpeakingMode == "flexible" && !ApiKeyStore.HasKey;
            if (credentialsMissing)
            {
                if (!string.Equals(packId, _promptedMissingCredentialsPackId, StringComparison.Ordinal))
                {
                    _promptedMissingCredentialsPackId = packId;
                    await PromptForMissingCredentialsAsync();
                }
                return;
            }

            _lastPrepPackId = packId;
            _promptedMissingCredentialsPackId = null;

            // 这个窗口既是 TTS 生成进度，也是“开始自动口语”的唯一入口。
            // 上一轮已经生成过音频时也必须重新打开：用户可能在考试中途退出，
            // 重新进入同一份试卷后还要继续作答后面的口语题。
            // SpeakingPrepService 会直接复用仍然有效的音频，不会重复调用接口。
            string title = _speakingAnswers.LoadAllPapers()
                .FirstOrDefault(paper => paper.PackId == packId)?.Title ?? $"试卷 {packId}";
            ShowSpeakingPrep(packId, title, questions);
        }
        catch (Exception ex)
        {
            if (!string.Equals(_lastSpeakingPaperError, ex.Message, StringComparison.Ordinal))
            {
                DiagnosticLogService.Warn("speaking_prep.monitor.failed", ex.Message);
                _lastSpeakingPaperError = ex.Message;
            }
        }
        finally
        {
            _checkingSpeakingPaper = false;
        }
    }

    private void ShowSpeakingPrep(string packId, string title, IReadOnlyList<SpeakingQuestion> questions)
    {
        try { _prepWindow?.Close(); }
        catch (Exception) { }
        var window = new SpeakingPrepWindow();
        window.Closed += (_, _) =>
        {
            if (_prepWindow == window) _prepWindow = null;
        };
        _prepWindow = window;
        window.Start(packId, title, questions);
        window.Activate();
    }

    private async Task CheckVirtualCableAsync()
    {
        AppPreferencesService preferences = AppPreferencesService.Current;
        if (preferences.VirtualCableReminderShown) return;

        var cable = new VirtualCableService();
        try { await cable.RefreshAsync(); }
        catch (Exception) { return; }
        if (cable.IsReady) return;

        string message = !cable.IsAvailable
            ? "未检测到 VB-CABLE 虚拟声卡。需要时请到“答题设置”安装或检查虚拟声卡驱动。"
            : "检测到 CABLE Input，但没有找到录音端 CABLE Output。请检查录音设备是否启用。";
        var dialog = new ContentDialog
        {
            Title = "虚拟声卡未配置",
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "打开录音设备",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        ContentDialogResult result = await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog);
        preferences.MarkVirtualCableReminderShown();
        if (result != ContentDialogResult.Primary) return;
        VirtualCableSetupService.OpenRecordingDevices();
    }

    private async Task ShowWelcomeAsync()
    {
        AppPreferencesService preferences = AppPreferencesService.Current;
        if (preferences.WelcomeCompleted) return;

        bool hasDeepSeek = ApiKeyStore.HasKey;
        bool hasAliyun = AliyunTtsCredentialStore.HasCredentials;
        if (hasDeepSeek && hasAliyun)
        {
            preferences.MarkWelcomeCompleted();
            return;
        }

        var lines = new List<string>();
        if (!hasDeepSeek) lines.Add("• DeepSeek API Key（口语生成）");
        if (!hasAliyun) lines.Add("• 阿里云 TTS RAM 凭据与 AppID（语音合成）");

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "欢迎使用 ETS Tool。以下凭据尚未配置：", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock
        {
            Text = string.Join(Environment.NewLine, lines),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = "DeepSeek 口语生成和试卷解析需要 DeepSeek API Key；TTS 语音合成需要阿里云 RAM 凭据与 AppID。凭据保存在本机。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap
        });

        var dialog = new ContentDialog
        {
            Title = "欢迎使用 ETS Tool",
            Content = content,
            PrimaryButtonText = "去填写",
            CloseButtonText = "完成",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        ContentDialogResult result = await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog);
        preferences.MarkCredentialsReminderShown();
        preferences.MarkWelcomeCompleted();
        if (result != ContentDialogResult.Primary) return;
        NavigateToTag("settings");
        if (PageFrame.Content is Pages.SettingsPage settingsPage) settingsPage.ExpandCredentials();
    }

    public async Task PromptForMissingCredentialsAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "未填写有效的凭据",
            Content = "无法完成指定操作。",
            PrimaryButtonText = "去填写",
            CloseButtonText = "完成",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        NavigateToTag("settings");
        if (PageFrame.Content is Pages.SettingsPage settingsPage) settingsPage.ExpandCredentials();
    }

    private async Task CheckStartupForUpdatesAsync()
    {
        AppPreferencesService preferences = AppPreferencesService.Current;
        if (preferences.InternalTestNoUpdatesEnabled || !preferences.AutoUpdateEnabled ||
            preferences.LastUpdateCheckUtc is DateTimeOffset last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(24))
            return;
        try
        {
            UpdateCheckResult result = await new GitHubUpdateService().CheckAsync();
            preferences.MarkUpdateCheck(result.CheckedAt);
            if (!result.UpdateAvailable || RootGrid.XamlRoot is null) return;
            var dialog = new ContentDialog
            {
                Title = "发现 ETS Tool 更新",
                Content = result.Message + "\n\n是否打开 GitHub 查看？",
                PrimaryButtonText = "查看更新",
                CloseButtonText = "稍后",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };
            if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog) == ContentDialogResult.Primary)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(result.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warn("update_check.failed", ex.Message);
            /* A transient network failure must not interrupt startup. */
        }
    }

    public async Task UnlockDeveloperToolsAsync()
    {
        AppPreferencesService preferences = AppPreferencesService.Current;
        if (preferences.DeveloperToolsUnlocked)
        {
            NavigateToTag("tools");
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "显示开发者工具？",
            Content = "开发者工具包含抓包、详细日志、虚拟麦克风操作和完全重置。完全重置会删除本地配置与凭据。确定显示该页面？",
            PrimaryButtonText = "显示开发者工具",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.None,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        preferences.UnlockDeveloperTools();
        NavigateToTag("tools");
    }

    public void NavigateToTag(string tag)
    {
        if (tag == "etsPreview" && !AppPreferencesService.Current.IntegrateEtsMainProgramEnabled)
            return;

        foreach (object item in RootNavigation.MenuItems.Concat(RootNavigation.FooterMenuItems))
        {
            if (item is NavigationViewItem nav && string.Equals(nav.Tag as string, tag, StringComparison.Ordinal))
            {
                if (tag == "tools") nav.Visibility = Visibility.Visible;
                RootNavigation.SelectedItem = nav;
                break;
            }
        }
    }

    private void OnEtsProgramIntegrationChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnEtsProgramIntegrationChanged);
            return;
        }

        bool enabled = AppPreferencesService.Current.IntegrateEtsMainProgramEnabled;
        EtsPreviewNavigationItem.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (enabled) return;

        if (((RootNavigation.SelectedItem as NavigationViewItem)?.Tag as string) == "etsPreview")
            NavigateToTag("overview");
        EtsWindowPreviewService.Shared.RestoreForDisabledIntegration();
    }

    private void ApplyTheme()
    {
        if (Content is FrameworkElement root)
        {
            AppPreferencesService.Current.ApplyTheme(root);
            BackdropFallback.Visibility = MicaController.IsSupported()
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private void ApplyLocalization()
    {
        LocalizationService localization = LocalizationService.Current;
        localization.Apply(Content as DependencyObject);
        foreach (object item in RootNavigation.MenuItems) localization.Apply(item as DependencyObject);
        foreach (object item in RootNavigation.FooterMenuItems) localization.Apply(item as DependencyObject);
    }

    private void OnLanguageChanged()
    {
        ApplyLocalization();
        if (PageFrame.CurrentSourcePageType is Type page) PageFrame.Navigate(page);
    }

    private void RootNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        if ((item.Tag as string) == "etsPreview" && !AppPreferencesService.Current.IntegrateEtsMainProgramEnabled)
        {
            NavigateToTag("overview");
            return;
        }
        DiagnosticLogService.Info("navigation.tab.selected",
            $"tag={item.Tag?.ToString() ?? "unknown"} title={item.Content?.ToString() ?? "unknown"}");
        Type? page = item.Tag?.ToString() switch
        {
            "overview" => typeof(Pages.OverviewPage),
            "etsPreview" => typeof(Pages.EtsPreviewPage),
            "papers" => typeof(Pages.PapersPage),
            "settings" => typeof(Pages.SettingsPage),
            "logs" => typeof(Pages.LogsPage),
            "tools" => typeof(Pages.ToolsPage),
            "guide" => typeof(Pages.GuidePage),
            "activities" => typeof(Pages.ActivityPage),
            "preferences" => typeof(Pages.PreferencesPage),
            "about" => typeof(Pages.AboutPage),
            _ => null
        };
        if (page is not null && PageFrame.CurrentSourcePageType != page) PageFrame.Navigate(page);
    }

    private void UpdateActivityNavigation()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(UpdateActivityNavigation);
            return;
        }
        int running = AnalysisActivityService.Current.RunningCount;
        ActivityNavigationRing.IsActive = running > 0;
        ToolTipService.SetToolTip(ActivitiesNavigationItem,
            running == 0 ? "查看当前活动和最近任务" : $"{running} 项活动正在进行");
    }

    private void RootNavigation_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        UpdatePaneToggleButtonStyle(args.DisplayMode);
        UpdateNavigationPaneOverlayState();
    }

    private void RootNavigation_PaneOpening(NavigationView sender, object args)
    {
        DiagnosticLogService.Info("navigation.pane.opened");
        _paneClosingTimer?.Stop();
        EtsWindowPreviewService.Shared.SetNavigationPaneOverlayOpen(
            sender.DisplayMode != NavigationViewDisplayMode.Expanded);
    }

    private void RootNavigation_PaneClosing(NavigationView sender, object args)
    {
        DiagnosticLogService.Info("navigation.pane.closing");
        _paneClosingTimer?.Stop();
        _paneClosingTimer ??= DispatcherQueue.CreateTimer();
        _paneClosingTimer.Interval = TimeSpan.FromMilliseconds(350);
        _paneClosingTimer.Tick -= PaneClosingTimer_Tick;
        _paneClosingTimer.Tick += PaneClosingTimer_Tick;
        _paneClosingTimer.Start();
    }

    private void PaneClosingTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        UpdateNavigationPaneOverlayState();
    }

    private void UpdateNavigationPaneOverlayState()
    {
        bool isOverlay = RootNavigation.IsPaneOpen &&
                         RootNavigation.DisplayMode != NavigationViewDisplayMode.Expanded;
        if (isOverlay)
            _paneClosingTimer?.Stop();
        EtsWindowPreviewService.Shared.SetNavigationPaneOverlayOpen(isOverlay);
    }

    private void UpdatePaneToggleButtonStyle(NavigationViewDisplayMode displayMode)
    {
        if (displayMode == NavigationViewDisplayMode.Expanded)
        {
            RootNavigation.ClearValue(NavigationView.PaneToggleButtonStyleProperty);
            return;
        }

        if (RootNavigation.Resources["WindowedPaneToggleButtonStyle"] is Style style)
            RootNavigation.PaneToggleButtonStyle = style;
    }

    private void SetWindowSize(int width, int height)
    {
        nint hwnd = WindowNative.GetWindowHandle(this);
        WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
        AppWindow appWindow = AppWindow.GetFromWindowId(id);
        double scale = Math.Max(96u, GetDpiForWindow(hwnd)) / 96.0;
        if (appWindow.Presenter is OverlappedPresenter presenter)
            presenter.PreferredMinimumWidth = (int)Math.Round(450 * scale);
        RectInt32 workArea = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary).WorkArea;
        int margin = (int)Math.Round(24 * scale);
        int pixelWidth = Math.Min((int)Math.Round(width * scale), Math.Max(1, workArea.Width - margin * 2));
        int pixelHeight = Math.Min((int)Math.Round(height * scale), Math.Max(1, workArea.Height - margin * 2));
        appWindow.MoveAndResize(new RectInt32(
            workArea.X + (workArea.Width - pixelWidth) / 2,
            workArea.Y + (workArea.Height - pixelHeight) / 2,
            pixelWidth, pixelHeight));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    private void SetAppIcon()
    {
        nint hwnd = WindowNative.GetWindowHandle(this);
        WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
        AppWindow.GetFromWindowId(id).SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ProgramIcon.ico"));
    }

    private void SetTitleBarColors()
    {
        nint hwnd = WindowNative.GetWindowHandle(this);
        WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
        AppWindowTitleBar titleBar = AppWindow.GetFromWindowId(id).TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(38, 255, 255, 255);
        titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(64, 255, 255, 255);
    }
}
