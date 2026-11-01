using System.ComponentModel;
using ETSAssistant.WinUI3.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace ETSAssistant.WinUI3.Pages;

public sealed partial class EtsPreviewPage : Page
{
    private readonly EtsWindowPreviewService _preview = EtsWindowPreviewService.Shared;
    private DispatcherQueueTimer? _windowMonitor;
    private AppWindow? _mainAppWindow;
    private bool _connecting;
    private bool _boundsUpdateQueued;
    private bool _suppressPreviewRestore;
    private DateTime _nextSearchAt = DateTime.MinValue;

    public EtsPreviewPage()
    {
        InitializeComponent();
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
        PreviewViewport.LayoutUpdated += PreviewViewport_LayoutUpdated;
        Loaded += (_, _) => LocalizationService.Current.Apply(EndEtsFlyout.Content as DependencyObject);
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep the preview usable when the assistant is narrowed to a small window.
        bool compact = e.NewSize.Width < 620 || e.NewSize.Height < 560;
        PageTitleText.FontSize = compact ? 24 : 30;
        PageTitleIcon.Width = compact ? 21 : 25;
        PageTitleIcon.Height = compact ? 21 : 25;
        PageDescriptionText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ResponsiveContent.RowSpacing = compact ? 8 : 14;
        ConnectionStatus.TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap;
        ConnectionStatus.TextTrimming = compact ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        ConnectionStatus.MaxWidth = compact
            ? Math.Min(210, Math.Max(100, e.NewSize.Width * 0.28))
            : double.PositiveInfinity;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (!AppPreferencesService.Current.IntegrateEtsMainProgramEnabled)
        {
            _preview.RestoreForDisabledIntegration();
            PreviewPlaceholder.Visibility = Visibility.Visible;
            ConnectionRing.IsActive = false;
            ConnectionStatus.Text = LocalizationService.Current.Translate(
                "请先在答题设置中启用“集成ETS主程序”。");
            return;
        }

        _preview.SetPreviewPageActive(true, PreviewViewport);
        _windowMonitor = DispatcherQueue.CreateTimer();
        _windowMonitor.Interval = TimeSpan.FromSeconds(1);
        _windowMonitor.Tick += (_, _) => CheckEmbeddedWindow();
        _windowMonitor.Start();
        AttachMainWindowTracking();
        TryFindRunningEts(automatic: true);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _windowMonitor?.Stop();
        _windowMonitor = null;
        if (_mainAppWindow is not null)
        {
            _mainAppWindow.Changed -= MainAppWindow_Changed;
            _mainAppWindow = null;
        }
        // Keep ETS embedded under the assistant, but hide it while another page is visible.
        _preview.SetPreviewPageActive(false);
    }

    private void PreviewViewport_LayoutUpdated(object? sender, object e) => QueuePreviewBoundsUpdate();

    private void AttachMainWindowTracking()
    {
        if (MainWindow.Current is not { } main) return;
        nint hwnd = WindowNative.GetWindowHandle(main);
        WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
        _mainAppWindow = AppWindow.GetFromWindowId(id);
        _mainAppWindow.Changed += MainAppWindow_Changed;
    }

    private void MainAppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange || args.DidVisibilityChange)
            QueuePreviewBoundsUpdate();
    }

    private void PreviewViewport_SizeChanged(object sender, SizeChangedEventArgs e) => QueuePreviewBoundsUpdate();

    private void QueuePreviewBoundsUpdate()
    {
        if (!IsLoaded || _boundsUpdateQueued) return;
        _boundsUpdateQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _boundsUpdateQueued = false;
                UpdatePreviewBounds();
            }))
            _boundsUpdateQueued = false;
    }

    private void UpdatePreviewBounds()
    {
        if (IsLoaded) _preview.UpdateBounds(PreviewViewport);
    }

    private void CheckEmbeddedWindow()
    {
        if (_preview.HasEmbeddedWindow)
        {
            if (_preview.IsEmbeddedWindowAlive)
            {
                UpdatePreviewBounds();
                return;
            }

            _preview.DetachClosedWindow();
            PreviewPlaceholder.Visibility = Visibility.Visible;
            ConnectionStatus.Text = LocalizationService.Current.Translate("ETS 窗口已关闭；正在自动查找已运行的 ETS。");
            ConnectionRing.IsActive = false;
            ConnectButton.IsEnabled = true;
        }

        if (DateTime.UtcNow >= _nextSearchAt)
            TryFindRunningEts(automatic: true);
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e) => TryFindRunningEts(automatic: false);

    private void StartEtsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool alreadyEmbedded = _preview.IsEmbeddedWindowAlive;
            AssistantService service = AssistantService.Current;
            service.RefreshEts();
            service.EnsureListener();
            service.OpenEts();
            _nextSearchAt = DateTime.MinValue;
            ConnectionStatus.Text = alreadyEmbedded
                ? string.Empty
                : LocalizationService.Current.Translate("ETS 正在启动；程序预览会自动连接。");
            ConnectionRing.IsActive = !alreadyEmbedded;
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Error("ets_preview.ets_start.failed", ex);
            ConnectionRing.IsActive = false;
            PreviewPlaceholder.Visibility = Visibility.Visible;
            ConnectionStatus.Text = ex.Message;
        }
    }

    private void CancelEndEtsFlyout_Click(object sender, RoutedEventArgs e)
    {
        EndEtsFlyout.Hide();
    }

    private void EndEtsFlyout_Opened(object sender, object e)
    {
        DiagnosticLogService.Info("ets_preview.terminate_confirmation.opened");
        _suppressPreviewRestore = false;
        _preview.HidePreviewWindow();
    }

    private void EndEtsFlyout_Closed(object sender, object e)
    {
        DiagnosticLogService.Info("ets_preview.terminate_confirmation.closed");
        if (!_suppressPreviewRestore && IsLoaded)
            _preview.UpdateBounds(PreviewViewport);
    }

    private void ConfirmEndEts_Click(object sender, RoutedEventArgs e)
    {
        _suppressPreviewRestore = true;
        EndEtsFlyout.Hide();
        try
        {
            int ended = AssistantService.Current.EndEtsProcesses();
            if (ended > 0)
            {
                if (!_preview.IsEmbeddedWindowAlive)
                {
                    if (_preview.HasEmbeddedWindow) _preview.DetachClosedWindow();
                    PreviewPlaceholder.Visibility = Visibility.Visible;
                }
                ConnectionRing.IsActive = false;
                ConnectionStatus.Text = $"{LocalizationService.Current.Translate("已结束")} {ended} " +
                                        LocalizationService.Current.Translate("个 ETS 进程。");
            }
            else
            {
                ConnectionStatus.Text = LocalizationService.Current.Translate("当前没有运行中的 ETS 进程。");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Error("ets_preview.terminate.failed", ex);
            ConnectionStatus.Text = ex.Message;
        }
    }

    private void TryFindRunningEts(bool automatic)
    {
        if (!AppPreferencesService.Current.IntegrateEtsMainProgramEnabled) return;
        if (_connecting || (automatic && DateTime.UtcNow < _nextSearchAt)) return;
        if (!automatic) DiagnosticLogService.Info("ets_preview.connection.search_requested");
        _connecting = true;
        _nextSearchAt = DateTime.UtcNow.AddSeconds(2);
        ConnectButton.IsEnabled = false;
        ConnectionRing.IsActive = !automatic;
        if (!automatic)
            ConnectionStatus.Text = LocalizationService.Current.Translate("正在查找已运行的 ETS 窗口…");

        try
        {
            if (_preview.TryConnectToRunningEts(PreviewViewport, CancellationToken.None))
            {
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
                ConnectionStatus.Text = string.Empty;
            }
            else
            {
                PreviewPlaceholder.Visibility = Visibility.Visible;
                ConnectionStatus.Text = _preview.IsWaitingForStableWindow
                    ? LocalizationService.Current.Translate("ETS 正在初始化；窗口稳定后会自动连接预览。")
                    : LocalizationService.Current.Translate(
                        "未检测到运行中的 ETS；此页会自动查找，不会自动启动。请先手动启动 ETS。");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            DiagnosticLogService.Warn("ets_preview.connection.permission_denied", ex.Message);
            PreviewPlaceholder.Visibility = Visibility.Visible;
            ConnectionStatus.Text = $"{ex.Message} 助手已以管理员权限运行，请确认 ETS 主窗口处于当前桌面会话。";
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Error("ets_preview.connection.failed", ex);
            ConnectionStatus.Text = ex.Message;
            PreviewPlaceholder.Visibility = Visibility.Visible;
        }
        finally
        {
            _connecting = false;
            ConnectionRing.IsActive = false;
            ConnectButton.IsEnabled = true;
        }
    }

}
