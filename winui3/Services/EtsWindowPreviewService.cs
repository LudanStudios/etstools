using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ETSAssistant.WinUI3.Pages;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Windows.Foundation;

namespace ETSAssistant.WinUI3.Services;

/// <summary>
/// Temporarily positions the real ETS top-level window over the XAML preview area.
/// ETS manages its own top-level window and uses screen coordinates internally, so it must not be
/// reparented as a child. Keeping the HWND top-level preserves its native rendering and input handling.
/// </summary>
public sealed class EtsWindowPreviewService : IDisposable
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int GwlHwndParent = -8;
    private const uint GwOwner = 4;
    private const long WsChild = 0x40000000L;
    private const long WsVisible = 0x10000000L;
    private const long WsMinimize = 0x20000000L;
    private const long WsPopup = 0x80000000L;
    private const long WsBorder = 0x00800000L;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const long WsSysMenu = 0x00080000L;
    private const long WsExAppWindow = 0x00040000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExWindowEdge = 0x00000100L;
    private const long WsExClientEdge = 0x00000200L;
    private const long WsExStaticEdge = 0x00020000L;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint RdwInvalidate = 0x0001;
    private const uint RdwFrame = 0x0400;
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WineventOutOfContext = 0x0000;
    private const uint GaRootOwner = 3;
    private const int ObjIdWindow = 0;
    private const int ChildIdSelf = 0;
    private const int SwRestore = 9;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private static readonly TimeSpan StableWindowRequiredDuration = TimeSpan.FromSeconds(3);
    private const uint AwBlend = 0x00080000;
    private const uint PreviewFadeDurationMs = 240;
    // Keep the native region on the same origin and radius as the XAML preview frame.
    private const double PreviewCornerRadiusDip = 12;

    private static readonly object ActiveLock = new();
    private static readonly HashSet<EtsWindowPreviewService> ActivePreviews = [];
    public static EtsWindowPreviewService Shared { get; } = new();

    private readonly AssistantService _assistant = AssistantService.Current;
    private nint _mainWindow;
    private nint _embeddedWindow;
    private nint _foregroundHook;
    private nint _locationHook;
    private WinEventCallback? _foregroundCallback;
    private WinEventCallback? _locationCallback;
    private FrameworkElement? _previewViewport;
    private bool _previewPageActive;
    private bool _navigationPaneOverlayOpen;
    private OriginalWindowState? _originalState;
    private bool _disposed;
    private PixelBounds? _lastBounds;
    private int _locationCorrectionQueued;
    private nint _candidateWindow;
    private uint _candidateProcessId;
    private PixelBounds? _candidateBounds;
    private DateTime _candidateStableSinceUtc;

    public bool HasEmbeddedWindow => _embeddedWindow != 0;
    public bool IsEmbeddedWindowAlive => _embeddedWindow != 0 && IsWindow(_embeddedWindow);
    public bool IsWaitingForStableWindow { get; private set; }

    public void SetNavigationPaneOverlayOpen(bool isOpen)
    {
        if (_navigationPaneOverlayOpen != isOpen)
            DiagnosticLogService.Info("ets_preview.navigation_overlay.changed", $"isOpen={isOpen}");
        _navigationPaneOverlayOpen = isOpen;
        if (isOpen)
        {
            HidePreviewWindow();
            return;
        }

        if (_previewPageActive && _previewViewport is not null)
            UpdateBounds(_previewViewport);
    }

    public void SetPreviewPageActive(bool active, FrameworkElement? viewport = null)
    {
        if (_previewPageActive != active)
            DiagnosticLogService.Info(active ? "ets_preview.page.entered" : "ets_preview.page.left");
        _previewPageActive = active;
        if (!active)
        {
            _previewViewport = null;
            HidePreviewWindow();
            return;
        }

        if (viewport is not null)
        {
            _previewViewport = viewport;
            if (IsEmbeddedWindowAlive)
                UpdateBounds(viewport);
        }
    }

    public bool TryConnectToRunningEts(FrameworkElement viewport, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsEmbeddedWindowAlive)
        {
            UpdateBounds(viewport);
            return true;
        }

        EnsureMainWindow();
        cancellationToken.ThrowIfCancellationRequested();
        UpdateBounds(viewport);
        _assistant.RefreshEts();
        if (string.IsNullOrWhiteSpace(_assistant.EtsExe))
            throw new FileNotFoundException("找不到 ETS 安装目录。请先到答题设置中选择 ETS 安装目录。", "Ets.exe");

        // Search only. ETS must be started manually by the user.
        nint target = FindEtsWindow(_assistant.EtsExe);
        if (target == 0)
        {
            ResetCandidateWindow();
            return false;
        }

        if (!IsWindowVisible(target) || IsIconic(target) || !GetWindowRect(target, out NativeRect candidateRect))
        {
            ResetCandidateWindow(waiting: true);
            return false;
        }

        GetWindowThreadProcessId(target, out uint candidateProcessId);
        var candidateBounds = new PixelBounds(candidateRect.Left, candidateRect.Top,
            candidateRect.Right - candidateRect.Left, candidateRect.Bottom - candidateRect.Top);
        if (_candidateWindow != target || _candidateProcessId != candidateProcessId ||
            _candidateBounds != candidateBounds)
        {
            _candidateWindow = target;
            _candidateProcessId = candidateProcessId;
            _candidateBounds = candidateBounds;
            _candidateStableSinceUtc = DateTime.UtcNow;
            IsWaitingForStableWindow = true;
            return false;
        }

        if (DateTime.UtcNow - _candidateStableSinceUtc < StableWindowRequiredDuration)
        {
            IsWaitingForStableWindow = true;
            return false;
        }

        ResetCandidateWindow();
        cancellationToken.ThrowIfCancellationRequested();
        AttachWindow(target);
        UpdateBounds(viewport);
        GetWindowThreadProcessId(target, out uint processId);
        DiagnosticLogService.Info("ets_preview.window.attached", $"pid={processId}");
        return true;
    }

    private void ResetCandidateWindow(bool waiting = false)
    {
        _candidateWindow = 0;
        _candidateProcessId = 0;
        _candidateBounds = null;
        _candidateStableSinceUtc = DateTime.MinValue;
        IsWaitingForStableWindow = waiting;
    }

    public void UpdateBounds(FrameworkElement viewport)
    {
        if (!_previewPageActive || _navigationPaneOverlayOpen)
        {
            HidePreviewWindow();
            return;
        }

        if (_mainWindow == 0 || !IsWindow(_mainWindow) || viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0)
            return;
        if (_embeddedWindow != 0 && !IsWindow(_embeddedWindow))
        {
            DetachClosedWindow();
            return;
        }

        MainWindow? main = MainWindow.Current;
        if (main?.Content is not UIElement root || _mainWindow == 0) return;
        try
        {
            GeneralTransform transform = viewport.TransformToVisual(root);
            Point origin = transform.TransformPoint(new Point(0, 0));
            double scale = Math.Max(96u, GetDpiForWindow(_mainWindow)) / 96.0;
            var screenOrigin = new NativePoint();
            if (!ClientToScreen(_mainWindow, ref screenOrigin)) return;
            var bounds = new PixelBounds(
                screenOrigin.X + (int)Math.Round(origin.X * scale),
                screenOrigin.Y + (int)Math.Round(origin.Y * scale),
                Math.Max(1, (int)Math.Round(viewport.ActualWidth * scale)),
                Math.Max(1, (int)Math.Round(viewport.ActualHeight * scale)));
            _lastBounds = bounds;
            if (_embeddedWindow != 0)
            {
                if (!IsWindowVisible(_mainWindow) || IsIconic(_mainWindow))
                {
                    HidePreviewWindow();
                    return;
                }

                // Keep the preview visible when another app has focus beside the
                // assistant. Hide it only when that foreground window actually
                // covers the preview area, so ETS does not leak over other apps.
                if (IsForegroundWindowOccludingPreview(bounds))
                {
                    HidePreviewWindow();
                    return;
                }

                bool wasVisible = IsWindowVisible(_embeddedWindow);
                bool hasCurrentBounds = GetWindowRect(_embeddedWindow, out NativeRect current);
                bool windowBoundsMatch = hasCurrentBounds &&
                    current.Left == bounds.X && current.Top == bounds.Y &&
                    current.Right - current.Left == bounds.Width &&
                    current.Bottom - current.Top == bounds.Height;
                bool windowSizeMatches = hasCurrentBounds &&
                    current.Right - current.Left == bounds.Width &&
                    current.Bottom - current.Top == bounds.Height;

                // Do not rewrite ETS styles or force a non-client frame recalculation during
                // ordinary focus/input changes. ETS owns its message loop; those cross-process
                // changes can collide with its own handling of a click. The borderless style is
                // applied once in AttachWindow. Here we only move/resize when the host layout
                // actually changed, and refresh the region only after a size change.
                if (wasVisible && windowBoundsMatch)
                    return;

                // Keep ETS above the assistant without activating or raising it in the Z order.
                uint flags = SwpNoActivate | SwpNoZOrder;
                if (wasVisible) flags |= SwpShowWindow;
                SetWindowPosChecked(_embeddedWindow, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, flags);
                if (!windowSizeMatches)
                    ApplyPreviewCornerMask(_embeddedWindow, bounds.Width, bounds.Height);
                if (!wasVisible) FadeInPreviewWindow(_embeddedWindow);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or COMException or Win32Exception)
        {
            // Layout can briefly be disconnected while NavigationView changes display mode.
        }
    }

    /// <summary>
    /// Hide the attached ETS window while its preview page is not active. The window remains
    /// owned by the assistant, so navigating back can show and reposition it without reconnecting
    /// or restoring it as a separate window.
    /// </summary>
    public void HidePreviewWindow()
    {
        if (_embeddedWindow != 0 && IsWindow(_embeddedWindow) && IsWindowVisible(_embeddedWindow))
        {
            ShowWindow(_embeddedWindow, SwHide);
            DiagnosticLogService.Info("ets_preview.window.hidden");
        }
    }

    public void DetachClosedWindow()
    {
        DiagnosticLogService.Warn("ets_preview.window.closed_unexpectedly");
        StopEmbeddedWindowPositionTracking();
        _embeddedWindow = 0;
        _originalState = null;
        _mainWindow = 0;
        _lastBounds = null;
    }

    public static void RestoreAll()
    {
        EtsWindowPreviewService[] previews;
        lock (ActiveLock) previews = ActivePreviews.ToArray();
        foreach (EtsWindowPreviewService preview in previews)
        {
            try { preview.Dispose(); }
            catch (Exception) { }
        }
    }

    public void RestoreForDisabledIntegration()
    {
        _previewPageActive = false;
        _previewViewport = null;
        _navigationPaneOverlayOpen = false;
        RestoreEmbeddedWindow();
        ClearPreviewState();
        ResetCandidateWindow();
        DiagnosticLogService.Info("ets_preview.integration.disabled");
    }

    private void EnsureMainWindow()
    {
        if (_mainWindow != 0 && IsWindow(_mainWindow))
        {
            EnsureForegroundTracking();
            return;
        }
        MainWindow main = MainWindow.Current ?? throw new InvalidOperationException("助手窗口尚未就绪。");
        _mainWindow = WindowNative.GetWindowHandle(main);
        if (_mainWindow == 0 || !IsWindow(_mainWindow))
            throw new InvalidOperationException("助手窗口尚未就绪。");
        lock (ActiveLock) ActivePreviews.Add(this);
        EnsureForegroundTracking();
    }

    private void EnsureForegroundTracking()
    {
        if (_foregroundHook != 0) return;
        _foregroundCallback = ForegroundWindowChanged;
        _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0,
            _foregroundCallback, 0, 0, WineventOutOfContext);
    }

    private void ForegroundWindowChanged(nint hook, uint eventType, nint window, int objectId,
        int childId, uint eventThread, uint eventTime)
    {
        if (_disposed || !_previewPageActive || !IsEmbeddedWindowAlive) return;

        FrameworkElement? viewport = _previewViewport;
        if (viewport is null) return;
        MainWindow.Current?.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed && _previewPageActive && IsEmbeddedWindowAlive)
                UpdateBounds(viewport);
        });
    }

    private bool IsForegroundWindowOccludingPreview(PixelBounds previewBounds)
    {
        nint foreground = GetForegroundWindow();
        if (IsAssistantOrEtsForeground(foreground) || !IsWindow(foreground) ||
            !GetWindowRect(foreground, out NativeRect foregroundBounds))
            return false;

        return foregroundBounds.Left < previewBounds.X + previewBounds.Width &&
               foregroundBounds.Right > previewBounds.X &&
               foregroundBounds.Top < previewBounds.Y + previewBounds.Height &&
               foregroundBounds.Bottom > previewBounds.Y;
    }

    private bool IsAssistantOrEtsForeground(nint window)
    {
        if (window == 0) return false;
        nint rootOwner = GetAncestor(window, GaRootOwner);
        return window == _mainWindow || window == _embeddedWindow || rootOwner == _mainWindow;
    }

    private void AttachWindow(nint target)
    {
        if (_mainWindow == 0 || !IsWindow(target))
            throw new InvalidOperationException("ETS 窗口已关闭，无法创建预览。");

        GetWindowThreadProcessId(_mainWindow, out uint assistantProcessId);
        GetWindowThreadProcessId(target, out uint targetProcessId);
        if (target == _mainWindow || assistantProcessId == 0 || targetProcessId == 0 ||
            targetProcessId == assistantProcessId)
            throw new InvalidOperationException("预览目标不是独立的 ETS 窗口，已保留助手主窗口不变。");

        OriginalWindowState state = CaptureState(target);
        _originalState = state;
        _embeddedWindow = target;
        try
        {
            // The candidate window was observed as visible and non-minimized; hide it before
            // changing owner/style so the page transition never exposes an intermediate frame.
            ShowWindow(target, SwHide);

            EnsureBorderlessPreviewStyle(target, clearVisibleStyle: true);
            SetWindowLongChecked(target, GwlHwndParent, _mainWindow);
            PixelBounds bounds = _lastBounds ?? throw new InvalidOperationException("预览区域尚未完成布局。");
            // Keep ETS as a top-level owned window and place it over the XAML viewport.
            SetWindowPosChecked(target, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                SwpNoActivate | SwpFrameChanged);
            ApplyPreviewCornerMask(target, bounds.Width, bounds.Height);
            // Defer repainting instead of synchronously sending paint messages into ETS.
            RedrawWindow(target, 0, 0, RdwInvalidate | RdwFrame);
            FadeInPreviewWindow(target);
            EnsureEmbeddedWindowPositionTracking(target);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Error("ets_preview.window.attach_failed", ex);
            RestoreEmbeddedWindow();
            throw;
        }
    }

    private void EnsureEmbeddedWindowPositionTracking(nint window)
    {
        if (_locationHook != 0) return;
        GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0)
            throw new InvalidOperationException("无法获取 ETS 窗口进程，不能锁定预览位置。");

        _locationCallback = EmbeddedWindowLocationChanged;
        _locationHook = SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, 0,
            _locationCallback, processId, 0, WineventOutOfContext);
        if (_locationHook != 0) return;

        int error = Marshal.GetLastWin32Error();
        _locationCallback = null;
        throw new Win32Exception(error, "无法锁定 ETS 预览窗口位置。");
    }

    private void EmbeddedWindowLocationChanged(nint hook, uint eventType, nint window, int objectId,
        int childId, uint eventThread, uint eventTime)
    {
        try
        {
            if (_disposed || !_previewPageActive || window != _embeddedWindow ||
                objectId != ObjIdWindow || childId != ChildIdSelf)
                return;

            // Keep the WinEvent callback side-effect free. ETS can raise location events while it is
            // processing its own window messages; changing its styles or position inline can re-enter
            // that code. Coalesce the correction and run it on the assistant UI queue after the event.
            if (Interlocked.Exchange(ref _locationCorrectionQueued, 1) != 0) return;

            DispatcherQueue? dispatcher = MainWindow.Current?.DispatcherQueue;
            if (dispatcher?.TryEnqueue(() =>
            {
                try
                {
                    if (_disposed || !_previewPageActive || !IsEmbeddedWindowAlive) return;
                    FrameworkElement? viewport = _previewViewport;
                    if (viewport is not null) UpdateBounds(viewport);
                }
                catch (Exception ex)
                {
                    // The ETS window or preview viewport can disappear while this correction is queued.
                    Debug.WriteLine($"ETS preview location correction was skipped: {ex}");
                }
                finally
                {
                    Interlocked.Exchange(ref _locationCorrectionQueued, 0);
                }
            }) != true)
            {
                Interlocked.Exchange(ref _locationCorrectionQueued, 0);
            }
        }
        catch (Exception ex)
        {
            // Never let a transient native/window teardown error escape a WinEvent callback.
            Debug.WriteLine($"ETS preview location event was skipped: {ex}");
            Interlocked.Exchange(ref _locationCorrectionQueued, 0);
        }
    }

    private void StopEmbeddedWindowPositionTracking()
    {
        if (_locationHook != 0)
        {
            UnhookWinEvent(_locationHook);
            _locationHook = 0;
        }
        _locationCallback = null;
    }

    private static bool EnsureBorderlessPreviewStyle(nint window, bool clearVisibleStyle = false)
    {
        long style = GetWindowLongPtrW(window, GwlStyle).ToInt64();
        long removedStyles = WsChild | WsCaption | WsBorder | WsThickFrame | WsMinimizeBox |
                             WsMaximizeBox | WsSysMenu | WsMinimize;
        if (clearVisibleStyle) removedStyles |= WsVisible;
        long borderlessStyle = (style & ~removedStyles) | WsPopup;
        bool changed = borderlessStyle != style;
        if (changed)
            SetWindowLongChecked(window, GwlStyle, new nint(borderlessStyle));

        long exStyle = GetWindowLongPtrW(window, GwlExStyle).ToInt64();
        long borderlessExStyle = (exStyle & ~(WsExAppWindow | WsExWindowEdge | WsExClientEdge | WsExStaticEdge)) |
                                 WsExToolWindow;
        if (borderlessExStyle != exStyle)
        {
            SetWindowLongChecked(window, GwlExStyle, new nint(borderlessExStyle));
            changed = true;
        }

        return changed;
    }

    private static void FadeInPreviewWindow(nint window)
    {
        if (!AnimateWindow(window, PreviewFadeDurationMs, AwBlend))
            ShowWindow(window, SwShowNoActivate);
    }

    private static OriginalWindowState CaptureState(nint window)
    {
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        bool hasPlacement = GetWindowPlacement(window, ref placement);
        if (!hasPlacement)
            placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        nint originalRegion = CreateRectRgn(0, 0, 0, 0);
        if (originalRegion != 0 && GetWindowRgn(window, originalRegion) == 0)
        {
            DeleteObject(originalRegion);
            originalRegion = 0;
        }
        GetWindowRect(window, out NativeRect rect);
        nint owner = GetWindow(window, GwOwner);
        return new OriginalWindowState(owner, GetWindowLongPtrW(window, GwlStyle),
            GetWindowLongPtrW(window, GwlExStyle), placement, rect, IsWindowVisible(window), hasPlacement,
            originalRegion);
    }

    private void ApplyPreviewCornerMask(nint window, int width, int height)
    {
        // The XAML radius is measured in the assistant window's DPI. ETS may use a
        // different DPI awareness context, so use the host scale for the native mask.
        double scale = Math.Max(96u, GetDpiForWindow(_mainWindow)) / 96.0;
        int diameter = Math.Max(1, (int)Math.Round(PreviewCornerRadiusDip * 2 * scale));
        diameter = Math.Min(diameter, Math.Min(width, height));
        nint region = CreateRoundRectRgn(0, 0, width, height, diameter, diameter);
        if (region == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 ETS 预览圆角遮罩。");

        if (SetWindowRgn(window, region, true) == 0)
        {
            int error = Marshal.GetLastWin32Error();
            DeleteObject(region);
            throw new Win32Exception(error, "无法应用 ETS 预览圆角遮罩。");
        }
        // SetWindowRgn transfers ownership of the region to Windows on success.
    }

    private static void SetWindowLongChecked(nint window, int index, nint value)
    {
        SetLastError(0);
        nint previous = SetWindowLongPtrW(window, index, value);
        int error = Marshal.GetLastWin32Error();
        if (previous == 0 && error != 0)
            throw new Win32Exception(error, "无法修改 ETS 窗口样式。");
    }

    private static void SetWindowPosChecked(nint window, nint insertAfter, int x, int y,
        int width, int height, uint flags)
    {
        if (!SetWindowPos(window, insertAfter, x, y, width, height, flags))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法调整 ETS 预览窗口大小。");
    }

    private void RestoreEmbeddedWindow()
    {
        StopEmbeddedWindowPositionTracking();
        nint window = _embeddedWindow;
        OriginalWindowState? state = _originalState;
        _embeddedWindow = 0;
        _originalState = null;
        if (window == 0 || state is null) return;
        if (!IsWindow(window))
        {
            if (state.OriginalRegion != 0) DeleteObject(state.OriginalRegion);
            return;
        }

        try
        {
            nint originalRegion = state.OriginalRegion;
            state.OriginalRegion = 0;
            if (SetWindowRgn(window, originalRegion, true) == 0 && originalRegion != 0)
                DeleteObject(originalRegion);
            SetWindowLongPtrW(window, GwlHwndParent, state.Owner);
            SetWindowLongPtrW(window, GwlStyle, state.Style);
            SetWindowLongPtrW(window, GwlExStyle, state.ExtendedStyle);
            SetWindowPos(window, 0, state.Rect.Left, state.Rect.Top,
                Math.Max(1, state.Rect.Right - state.Rect.Left),
                Math.Max(1, state.Rect.Bottom - state.Rect.Top),
                SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            if (state.HasPlacement)
            {
                WindowPlacement placement = state.Placement;
                SetWindowPlacement(window, ref placement);
            }
            if (state.WasVisible)
                ShowWindow(window, state.Placement.ShowCommand == 0 ? SwRestore : state.Placement.ShowCommand);
            else
                ShowWindow(window, 0);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            // Best effort restoration when ETS is concurrently closing.
            DiagnosticLogService.Warn("ets_preview.window.restore_incomplete", ex.Message);
        }
        DiagnosticLogService.Info("ets_preview.window.restored");
    }

    private static nint FindEtsWindow(string etsExe)
    {
        string? root = Path.GetDirectoryName(etsExe);
        if (string.IsNullOrWhiteSpace(root)) return 0;
        string expectedDirectory = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var pathMatches = new Dictionary<uint, bool>();
        var candidates = new List<(nint Window, long Area, bool IsShellClass, bool HasBrandedTitle)>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            GetWindowThreadProcessId(window, out uint processId);
            if (!GetWindowRect(window, out NativeRect rect)) return true;
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width < 300 || height < 200) return true;

            if (!pathMatches.TryGetValue(processId, out bool fromInstallDirectory))
            {
                fromInstallDirectory = IsProcessFromDirectory(processId, expectedDirectory);
                pathMatches[processId] = fromInstallDirectory;
            }

            string className = GetWindowClassName(window);
            bool isShellClass = className.Equals("SHELL", StringComparison.OrdinalIgnoreCase);
            bool hasBrandedTitle = HasEtsWindowTitle(window);
            // ETS logs identify its actual top-level browser as class SHELL.
            // Do not let a similarly titled helper window from another process win the search.
            if (fromInstallDirectory || (isShellClass && hasBrandedTitle))
                candidates.Add((window, (long)width * height, isShellClass, hasBrandedTitle));
            return true;
        }, 0);

        return candidates.OrderByDescending(candidate => candidate.IsShellClass)
            .ThenByDescending(candidate => candidate.HasBrandedTitle)
            .ThenByDescending(candidate => candidate.Area)
            .Select(candidate => candidate.Window)
            .FirstOrDefault();
    }

    private static bool IsProcessFromDirectory(uint processId, string expectedDirectory)
    {
        try
        {
            using Process process = Process.GetProcessById(checked((int)processId));
            string? path = process.MainModule?.FileName;
            return path is not null && Path.GetFullPath(path).StartsWith(expectedDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or
                                   UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool HasEtsWindowTitle(nint window)
    {
        return GetWindowTitle(window).Contains("E听说", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWindowTitle(nint window)
    {
        int length = GetWindowTextLengthW(window);
        if (length <= 0) return string.Empty;
        var title = new StringBuilder(length + 1);
        GetWindowTextW(window, title, title.Capacity);
        return title.ToString();
    }

    private static string GetWindowClassName(nint window)
    {
        var className = new StringBuilder(256);
        int length = GetClassNameW(window, className, className.Capacity);
        return length > 0 ? className.ToString() : string.Empty;
    }

    private void ClearPreviewState()
    {
        _mainWindow = 0;
        _lastBounds = null;
        _previewViewport = null;
        _previewPageActive = false;
        _navigationPaneOverlayOpen = false;
        if (_foregroundHook != 0)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = 0;
        }
        _foregroundCallback = null;
        StopEmbeddedWindowPositionTracking();
        lock (ActiveLock) ActivePreviews.Remove(this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RestoreEmbeddedWindow();
        ClearPreviewState();
    }

    private readonly record struct PixelBounds(int X, int Y, int Width, int Height);

    private sealed class OriginalWindowState(nint owner, nint style, nint extendedStyle,
        WindowPlacement placement, NativeRect rect, bool wasVisible, bool hasPlacement, nint originalRegion)
    {
        public nint Owner { get; } = owner;
        public nint Style { get; } = style;
        public nint ExtendedStyle { get; } = extendedStyle;
        public WindowPlacement Placement { get; set; } = placement;
        public NativeRect Rect { get; } = rect;
        public bool WasVisible { get; } = wasVisible;
        public bool HasPlacement { get; } = hasPlacement;
        public nint OriginalRegion { get; set; } = originalRegion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);
    private delegate void WinEventCallback(nint hook, uint eventType, nint window, int objectId,
        int childId, uint eventThread, uint eventTime);

    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint insertAfter,
        int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AnimateWindow(nint window,
        uint duration, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRoundRectRgn(
        int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteObject(nint obj);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(nint window, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RedrawWindow(nint window,
        nint updateRect, nint updateRegion, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(nint window, ref WindowPlacement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(nint window, ref WindowPlacement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWinEventHook(uint eventMin,
        uint eventMax, nint eventHook, WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLengthW(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder className, int maxCount);
    [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
}
