using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.UI;
using ETSAssistant.WinUI3.Services;

namespace ETSAssistant.WinUI3.Pages;

public sealed partial class OverviewPage : Page
{
    private static readonly SolidColorBrush WaitingBadgeBrush = new(Color.FromArgb(31, 148, 163, 184));
    private static readonly SolidColorBrush ActiveBadgeBrush = new(Color.FromArgb(31, 59, 130, 246));
    private static readonly SolidColorBrush CompleteBadgeBrush = new(Color.FromArgb(31, 34, 197, 94));
    private static readonly SolidColorBrush UnansweredChipBrush = new(Color.FromArgb(20, 148, 163, 184));
    private static readonly SolidColorBrush CorrectChipBrush = new(Color.FromArgb(46, 34, 197, 94));
    private static readonly SolidColorBrush WrongChipBrush = new(Color.FromArgb(46, 239, 68, 68));

    private readonly AssistantService _service = AssistantService.Current;
    private readonly CurrentPaperService _paperService = new(AssistantService.Current.DataRoot);
    private readonly AnswerResetService _resetService = new(AssistantService.Current.DataRoot);
    private DispatcherQueueTimer? _timer;
    private CurrentPaperSnapshot? _currentPaper;
    private string _paperSignature = "\0";
    private bool _refreshingPaper;
    private bool? _initializeButtonWrapped;

    public OverviewPage()
    {
        InitializeComponent();
        DataPathText.Text = _service.DataRoot;
        Loaded += (_, _) =>
        {
            RefreshStatus();
            _ = RefreshPaperAsync();
            _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(3);
            _timer.Tick += async (_, _) =>
            {
                RefreshStatus();
                await RefreshPaperAsync();
            };
            _timer.Start();
        };
        Unloaded += (_, _) => _timer?.Stop();
    }

    private void ActionButtonsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool wrapInitializeButton = e.NewSize.Width < 720;
        if (_initializeButtonWrapped == wrapInitializeButton) return;

        _initializeButtonWrapped = wrapInitializeButton;
        if (wrapInitializeButton)
        {
            Grid.SetRow(InitializeButton, 1);
            Grid.SetColumn(InitializeButton, 0);
            Grid.SetColumnSpan(InitializeButton, 4);
            InitializeButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            Grid.SetRow(InitializeButton, 0);
            Grid.SetColumn(InitializeButton, 3);
            Grid.SetColumnSpan(InitializeButton, 1);
            InitializeButton.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }

    private void RefreshStatus()
    {
        _service.RefreshEts();
        bool found = _service.EtsExe is not null;
        bool running = found && _service.IsEtsRunning;
        StatusText.Text = !found ? "未找到 ETS" : running ? "ETS 正在运行" : "已就绪，等待 ETS 启动";
        Color color = !found ? Color.FromArgb(255, 220, 38, 38) : running
            ? Color.FromArgb(255, 34, 197, 94) : Color.FromArgb(255, 22, 163, 74);
        StatusDot.Fill = new SolidColorBrush(color);
        EtsPathText.Text = _service.EtsExe ?? "请先安装 ETS，再重新扫描。";
        ListenerText.Text = _service.IsAutostartEnabled
            ? "监听器：已设置为登录 Windows 后自启动。"
            : "监听器：尚未设置登录自启动，可在答题设置中开启。";
        CredentialStatusText.Text = $"凭据：DeepSeek {(ApiKeyStore.HasKey ? "已配置" : "未配置")} · 阿里云 TTS " +
            $"{(AliyunTtsCredentialStore.HasCredentials ? "已配置" : "未配置")}";
        AppPreferencesService preferences = AppPreferencesService.Current;
        AssistantSettings config = _service.LoadSettings();
        AnswerConfigText.Text = $"正确率 {config.Accuracy:P0} · 跳题间隔 {config.SkipInterval:0.##} 秒 · " +
            $"口语模式 {(preferences.SpeakingMode == "flexible" ? "生成" : "标答")}";
        EtsErrorBar.IsOpen = !found;
        if (!string.IsNullOrWhiteSpace(_service.LastError))
        {
            InfoBarPresenter.Show(GeneralInfoBar, "需要注意", _service.LastError, InfoBarSeverity.Warning);
        }
        else if (found) GeneralInfoBar.IsOpen = false;
    }

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        _service.RefreshEts();
        _service.LastError = string.Empty;
        RefreshStatus();
    }

    private async void ChooseEtsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                CommitButtonText = "选择此文件夹"
            };
            picker.FileTypeFilter.Add("*");

            MainWindow window = MainWindow.Current
                ?? throw new InvalidOperationException("窗口尚未准备好，请稍后重试。");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            EtsDirectoryValidation validation = _service.ValidateEtsDirectory(folder.Path);
            if (!validation.IsValid)
            {
                await ShowDialogAsync("目录校验失败", validation.Message, "知道了");
                return;
            }

            string versionLine = string.IsNullOrWhiteSpace(validation.FileVersion)
                ? string.Empty
                : $"\n版本：{validation.FileVersion}";
            var confirm = new ContentDialog
            {
                Title = "校验通过，应用这个目录？",
                Content = $"ETS 安装目录：\n{validation.DirectoryPath}\n\n启动程序：\n{validation.ExecutablePath}{versionLine}\n\n" +
                          "应用后会更新登录启动项和 ETS 快捷方式，并重启后台监听器。",
                PrimaryButtonText = "应用",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(confirm) != ContentDialogResult.Primary) return;

            string warnings = _service.ApplyEtsDirectory(validation.DirectoryPath);
            RefreshStatus();
            string title = warnings.Length == 0 ? "已应用新的 ETS 目录" : "目录已保存，但有需要注意的问题";
            string message = warnings.Length == 0
                ? $"后续启动和监听将使用 {validation.ExecutablePath}。"
                : warnings;
            InfoBarPresenter.Show(GeneralInfoBar, title, message,
                warnings.Length == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    private async void InitializeAndOpen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _service.RefreshEts();
            string warnings = _service.InitializeAndOpen(_service.LoadSettings());
            RefreshStatus();
            string title = warnings.Length == 0 ? "已完成初始化" : "ETS 已打开，但有需要注意的问题";
            string message = warnings.Length == 0 ? "监听器已启动，ETS 正在打开。" : warnings;
            InfoBarPresenter.Show(GeneralInfoBar, title, message,
                warnings.Length == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            if (AppPreferencesService.Current.IntegrateEtsMainProgramEnabled)
                MainWindow.Current?.NavigateToTag("etsPreview");
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    private async void OpenEts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _service.RefreshEts();
            _service.EnsureListener();
            _service.OpenEts();
            InfoBarPresenter.Show(GeneralInfoBar, "ETS 已启动", "后台监听器正在等待 ETS 页面加载。",
                InfoBarSeverity.Success);
            RefreshStatus();
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    private async Task RefreshPaperAsync()
    {
        if (_refreshingPaper) return;
        _refreshingPaper = true;
        try { ShowPaper(await _paperService.LoadAsync()); }
        catch (Exception) { }
        finally { _refreshingPaper = false; }
    }

    private void ShowPaper(CurrentPaperSnapshot? paper)
    {
        string signature = paper is null
            ? string.Empty
            : $"{paper.PackId}|{paper.Title}|{paper.Total}|{paper.Answered}|{paper.Correct}|{paper.LastActivity:O}|" +
              string.Join(",", paper.Answers.Select(answer => answer.SelectedAnswer)) + "|" +
              string.Join(",", paper.Answers.Select(answer => answer.IsListening ? "L" : "R"));
        if (signature == _paperSignature) return;
        _paperSignature = signature;
        _currentPaper = paper;
        if (paper is null)
        {
            PaperStateText.Text = "等待检测";
            PaperStateBadge.Background = WaitingBadgeBrush;
            PaperTitleText.Text = "尚未检测到正在作答的试卷";
            PaperMetaText.Text = "启动 ETS 并打开试卷后，这里会显示答题进度和本地答案。";
            PaperProgressPanel.Visibility = Visibility.Collapsed;
            AnswerChips.ItemsSource = null;
            ManualResetButton.IsEnabled = false;
            EmptyPaperActions.Visibility = Visibility.Visible;
            return;
        }

        ManualResetButton.IsEnabled = true;
        EmptyPaperActions.Visibility = Visibility.Collapsed;
        PaperStateText.Text = paper.Complete ? "已完成" : "已检测";
        PaperStateBadge.Background = paper.Complete ? CompleteBadgeBrush : ActiveBadgeBrush;
        PaperTitleText.Text = paper.Title;
        string activity = paper.LastActivity is DateTime last ? $" · 最后作答 {last:HH:mm:ss}" : string.Empty;
        PaperMetaText.Text = $"试卷编号 {paper.PackId} · 共 {paper.Total} 道选择题{activity}" +
                             (paper.Total == 0 ? " · 没有可读取的选择题答案" : string.Empty);
        PaperProgressText.Text = $"已答 {paper.Answered} / {paper.Total} 题";
        PaperPercentText.Text = paper.Total == 0 ? "0%" : $"{Math.Round(paper.Answered * 100.0 / paper.Total):0}%";
        PaperProgressBar.Maximum = Math.Max(paper.Total, 1);
        PaperProgressBar.Value = paper.Answered;
        int pending = paper.Answered - paper.Correct;
        PaperCorrectText.Text = $"本地记录：答案一致 {paper.Correct} 题" +
                                (pending > 0 ? $" · 待复习 {pending} 题" : string.Empty);
        PaperProgressPanel.Visibility = paper.Total == 0 ? Visibility.Collapsed : Visibility.Visible;
        AnswerChips.ItemsSource = paper.Answers.Select(answer => new AnswerChip(
            NumberText: answer.Number.ToString(),
            Answer: answer.CorrectAnswer,
            Tooltip: BuildAnswerTooltip(answer),
            Background: !answer.Answered ? UnansweredChipBrush : answer.Matches ? CorrectChipBrush : WrongChipBrush
        )).ToList();
    }

    private static string BuildAnswerTooltip(CurrentPaperAnswer answer)
    {
        string kind = answer.IsListening ? "听力题" : "选择题";
        string detail = $"{kind} 第 {answer.Number} 题\n正确答案：{answer.CorrectAnswer}";
        return answer.Answered
            ? detail + $"\n已选答案：{answer.SelectedAnswer}（{(answer.Matches ? "一致" : "不一致")}）"
            : detail + "\n尚未作答";
    }

    private async void CopyAnswers_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPaper is null || _currentPaper.Answers.Count == 0) return;
        try
        {
            string text = string.Join("  ", _currentPaper.Answers
                .Select(answer => $"{answer.Number}.{answer.CorrectAnswer}"));
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            InfoBarPresenter.Show(GeneralInfoBar, "答案已复制",
                $"已复制 {_currentPaper.Answers.Count} 道题的答案，可直接粘贴使用。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { await ShowErrorAsync($"无法复制答案：{ex.Message}"); }
    }

    private async void ManualReset_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPaper is null) return;
        var confirm = new ContentDialog
        {
            Title = "重新开始答题？",
            Content = "请先在 ETS 中手动切换到第 1 题，然后点击“开始重置”。\n\n" +
                      "助手会清空本轮的答题进度（历史记录仍会保留），并从当前题目重新作答。",
            PrimaryButtonText = "开始重置",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(confirm) != ContentDialogResult.Primary) return;

        ManualResetButton.IsEnabled = false;
        try
        {
            AnswerResetResult result = await _resetService.RequestAsync(_currentPaper.PackId);
            bool applied = result.Status == AnswerResetStatus.Applied;
            InfoBarPresenter.Show(GeneralInfoBar, applied ? "已重置答题进度" : "重置未完成", result.Message,
                applied ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            if (applied) await RefreshPaperAsync();
        }
        finally
        {
            ManualResetButton.IsEnabled = _currentPaper is not null;
        }
    }

    private async void OpenPaperFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPaper is null) return;
        string path = Path.Combine(_service.DataRoot, _currentPaper.PackId);
        try
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("当前试卷目录不存在。");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    private async void EndEts_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "结束 ETS 进程？",
            Content = "这会立即关闭 ETS 及其外壳进程；未保存的 ETS 操作可能丢失。",
            PrimaryButtonText = "结束 ETS",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.None,
            XamlRoot = XamlRoot
        };
        if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        try
        {
            int ended = _service.EndEtsProcesses();
            InfoBarPresenter.Show(GeneralInfoBar, ended > 0 ? "ETS 已结束" : "没有运行中的 ETS",
                ended > 0 ? $"已结束 {ended} 个 ETS 进程。" : "当前没有找到 ETS 进程。",
                InfoBarSeverity.Informational);
            RefreshStatus();
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.Current?.NavigateToTag("settings");
    }


    private sealed record AnswerChip(string NumberText, string Answer, string Tooltip, Brush Background);

    private async Task ShowDialogAsync(string title, string message, string closeText)
    {
        var dialog = new ContentDialog
        {
            Title = title, Content = message, CloseButtonText = closeText, XamlRoot = XamlRoot
        };
        await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog);
    }

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "无法完成操作", Content = message, CloseButtonText = "知道了", XamlRoot = XamlRoot
        };
        await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog);
    }
}
