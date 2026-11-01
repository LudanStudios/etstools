using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Storage.Pickers;
using WinRT.Interop;
using ETSAssistant.WinUI3.Services;

namespace ETSAssistant.WinUI3.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly AssistantService _service = AssistantService.Current;
    private bool _loading;
    private bool _virtualCableBusy;
    private bool _csvPickerActive;

    public SettingsPage()
    {
        InitializeComponent();
        LoadValues();
    }

    private void LoadValues()
    {
        var settings = _service.LoadSettings();
        AccuracySlider.Value = settings.Accuracy;
        AccuracyText.Text = $"{Math.Round(settings.Accuracy * 100):0}%";
        SkipBox.Value = settings.SkipInterval;
        AutostartCheck.IsChecked = settings.StartAtLogin;
        EtsPathText.Text = _service.EtsExe ?? "未找到 ETS 安装目录。";
        _loading = true;
        SpeakingModeBox.SelectedIndex = AppPreferencesService.Current.SpeakingMode == "flexible" ? 1 : 0;
        EtsProgramIntegrationCheck.IsChecked = AppPreferencesService.Current.IntegrateEtsMainProgramEnabled;
        _loading = false;
        UpdateApiKeyStatus();
        UpdateAliyunStatus();
        _ = RefreshVirtualCableStatusAsync();
    }

    public void ExpandCredentials() => CredentialsExpander.IsExpanded = true;

    private async Task RefreshVirtualCableStatusAsync()
    {
        try
        {
            var cable = new VirtualCableService();
            await cable.RefreshAsync();
            CableStatusText.Text = cable.IsReady
                ? $"状态：已就绪（{cable.Current!.Name} → {cable.Capture!.Name}）"
                : cable.IsAvailable
                    ? $"状态：已检测到播放端 {cable.Current!.Name}，但没有检测到 CABLE Output。"
                    : "状态：未检测到 VB-CABLE 虚拟声卡。";
        }
        catch (Exception ex)
        {
            CableStatusText.Text = "状态检测失败：" + ex.Message;
        }
    }

    private async void RefreshVirtualCable_Click(object sender, RoutedEventArgs e) =>
        await RefreshVirtualCableStatusAsync();

    private async void InstallVirtualCable_Click(object sender, RoutedEventArgs e) =>
        await RunVirtualCableSetupAsync(uninstall: false);

    private async void UninstallVirtualCable_Click(object sender, RoutedEventArgs e) =>
        await RunVirtualCableSetupAsync(uninstall: true);

    private async Task RunVirtualCableSetupAsync(bool uninstall)
    {
        if (_virtualCableBusy) return;
        _virtualCableBusy = true;
        InstallVirtualCableButton.IsEnabled = false;
        UninstallVirtualCableButton.IsEnabled = false;
        RefreshVirtualCableButton.IsEnabled = false;

        try
        {
            if (!VirtualCableSetupService.HasInstaller(_service.AppRoot))
            {
                InfoBarPresenter.Show(CableInfoBar, "找不到 VB-CABLE 驱动包",
                    $"请检查应用目录中的文件：{VirtualCableSetupService.PackagePath(_service.AppRoot)}",
                    InfoBarSeverity.Error);
                return;
            }

            string action = uninstall ? "卸载" : "安装";
            var dialog = new ContentDialog
            {
                Title = uninstall ? "卸载 VB-CABLE 驱动" : "安装 VB-CABLE 驱动",
                Content = uninstall
                    ? "接下来会打开 VB-CABLE 官方设置程序。请在窗口中点击“Remove Driver”移除驱动。卸载完成后会询问是否立即重启。"
                    : "接下来会打开 VB-CABLE 官方设置程序。请在窗口中点击“Install Driver”安装驱动，并允许管理员权限。安装完成后建议重启电脑。",
                PrimaryButtonText = uninstall ? "打开卸载程序" : "打开安装程序",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

            InfoBarPresenter.Show(CableInfoBar, $"正在{action}",
                "官方设置程序会请求管理员权限。请在程序窗口中完成对应操作后关闭它。",
                InfoBarSeverity.Informational);
            int exitCode = await VirtualCableSetupService.RunSetupAsync(_service.AppRoot);
            if (exitCode != 0)
            {
                InfoBarPresenter.Show(CableInfoBar, $"{action}程序已退出",
                    $"VB-CABLE 设置程序退出代码：{exitCode}。", InfoBarSeverity.Warning);
                return;
            }

            await RefreshVirtualCableStatusAsync();
            if (uninstall)
            {
                await OfferCableRestartAsync();
            }
            else
            {
                InfoBarPresenter.Show(CableInfoBar, "VB-CABLE 设置程序已关闭",
                    "如果已完成安装但设备尚未出现，请重启 Windows 后再刷新状态。",
                    InfoBarSeverity.Success);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            InfoBarPresenter.Show(CableInfoBar, "操作已取消",
                "没有批准管理员权限，驱动没有更改。", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            InfoBarPresenter.Show(CableInfoBar, "VB-CABLE 操作失败", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _virtualCableBusy = false;
            InstallVirtualCableButton.IsEnabled = true;
            UninstallVirtualCableButton.IsEnabled = true;
            RefreshVirtualCableButton.IsEnabled = true;
        }
    }

    private async Task OfferCableRestartAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "是否立即重启？",
            Content = "如果你刚才在 VB-CABLE 官方程序中选择了“Remove Driver”，重启 Windows 可完成驱动卸载。请先保存其他应用中的工作。",
            PrimaryButtonText = "立即重启",
            CloseButtonText = "稍后重启",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            InfoBarPresenter.Show(CableInfoBar, "无法启动重启", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void SpeakingModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (SpeakingModeBox.SelectedItem is ComboBoxItem { Tag: string mode })
            AppPreferencesService.Current.SetSpeakingMode(mode);
    }

    private void EtsProgramIntegration_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox checkBox) return;
        AppPreferencesService.Current.SetEtsProgramIntegration(checkBox.IsChecked == true);
    }

    private void UpdateApiKeyStatus() =>
        ApiKeyStatusText.Text = ApiKeyStore.HasKey ? "状态：已保存（本机加密）" : "状态：未配置";

    private void UpdateAliyunStatus() =>
        AliyunStatusText.Text = AliyunTtsCredentialStore.HasCredentials
            ? "状态：RAM 凭据和 TTS AppID 已保存在本机加密文件中。"
            : "状态：未配置；语音合成需要填写个人凭据。";

    private void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        string key = ApiKeyBox.Password.Trim();
        if (key.Length == 0)
        {
            InfoBarPresenter.Show(ApiKeyInfoBar, "尚未填写 API Key",
                "请先粘贴 DeepSeek API Key，再点击保存。", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            ApiKeyStore.Save(key);
            ApiKeyBox.Password = string.Empty;
            UpdateApiKeyStatus();
            InfoBarPresenter.Show(ApiKeyInfoBar, "密钥已保存",
                "已使用 Windows DPAPI 加密保存到本机，测试工具页可以直接测试口语题。",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            InfoBarPresenter.Show(ApiKeyInfoBar, "保存失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ClearApiKey_Click(object sender, RoutedEventArgs e)
    {
        ApiKeyStore.Clear();
        ApiKeyBox.Password = string.Empty;
        UpdateApiKeyStatus();
        InfoBarPresenter.Show(ApiKeyInfoBar, "密钥已清除", "本机保存的 DeepSeek API Key 已删除。",
            InfoBarSeverity.Informational);
    }

    private void SaveAliyun_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AliyunTtsCredentialStore.Save(new AliyunTtsCredentials(
                AliyunRamNameBox.Text.Trim(), AliyunRamKeyBox.Password.Trim(), AliyunAppIdBox.Password.Trim()));
            AliyunRamNameBox.Text = string.Empty;
            AliyunRamKeyBox.Password = string.Empty;
            AliyunAppIdBox.Password = string.Empty;
            UpdateAliyunStatus();
            InfoBarPresenter.Show(AliyunInfoBar, "阿里云凭据已保存",
                "凭据已使用 Windows DPAPI 加密保存在本机。",
                InfoBarSeverity.Success);
        }
        catch (Exception ex) { InfoBarPresenter.Show(AliyunInfoBar, "保存失败", ex.Message, InfoBarSeverity.Error); }
    }

    private void ClearAliyun_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AliyunTtsCredentialStore.Clear();
            AliyunRamNameBox.Text = string.Empty;
            AliyunRamKeyBox.Password = string.Empty;
            AliyunAppIdBox.Password = string.Empty;
            UpdateAliyunStatus();
            InfoBarPresenter.Show(AliyunInfoBar, "阿里云凭据已清除", "本机保存的阿里云 TTS 凭据已删除。",
                InfoBarSeverity.Informational);
        }
        catch (Exception ex) { InfoBarPresenter.Show(AliyunInfoBar, "清除失败", ex.Message, InfoBarSeverity.Error); }
    }

    private async void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_csvPickerActive) return;
        _csvPickerActive = true;
        try
        {
            MainWindow window = MainWindow.Current ?? throw new InvalidOperationException("窗口尚未准备好。");
            DiagnosticLogService.Info("settings.credentials.csv_picker.opened");
            string? csvPath = NativeCsvFilePicker.PickCsv(WindowNative.GetWindowHandle(window));
            if (csvPath is null)
            {
                DiagnosticLogService.Info("settings.credentials.csv_picker.cancelled");
                return;
            }
            string csvName = Path.GetFileName(csvPath);
            DiagnosticLogService.Info("settings.credentials.csv_picker.selected", $"file={csvName}");

            CredentialCsvPreview preview = CredentialCsvImporter.Parse(csvPath);
            if (!preview.HasAny)
            {
                string errors = string.Join(Environment.NewLine, preview.Errors.Take(5));
                CsvImportStatusText.Text = "未导入任何凭据。";
                InfoBarPresenter.Show(CsvImportInfoBar, "CSV 中没有有效凭据", errors,
                    InfoBarSeverity.Error);
                return;
            }

            var summary = new StackPanel { Spacing = 6 };
            summary.Children.Add(new TextBlock { Text = "将导入并覆盖同类型凭据：", TextWrapping = TextWrapping.Wrap });
            if (preview.HasDeepSeek)
                summary.Children.Add(new TextBlock { Text = "• DeepSeek API Key：1 项" });
            if (preview.HasAliyun)
                summary.Children.Add(new TextBlock { Text = "• 阿里云 TTS：AccessKey ID / Secret / AppKey 各 1 项" });
            summary.Children.Add(new TextBlock
            {
                Text = "导入后使用 Windows DPAPI 加密保存，不提供导出。",
                FontSize = 12,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap
            });
            if (preview.Errors.Count > 0)
            {
                summary.Children.Add(new TextBlock
                {
                    Text = "CSV 中有 " + preview.Errors.Count + " 行被跳过：" + Environment.NewLine +
                           string.Join(Environment.NewLine, preview.Errors.Take(3)),
                    FontSize = 12,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Windows.UI.Color.FromArgb(0xFF, 252, 211, 77)),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            var dialog = new ContentDialog
            {
                Title = "导入 CSV 凭据",
                Content = summary,
                PrimaryButtonText = "导入并加密",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

            CredentialImportResult result = CredentialCsvImporter.Apply(preview);
            ApiKeyBox.Password = string.Empty;
            UpdateApiKeyStatus();
            UpdateAliyunStatus();

            var imported = new List<string>();
            if (result.DeepSeekSaved) imported.Add("DeepSeek");
            if (result.AliyunSaved) imported.Add("阿里云 TTS");
            CsvImportStatusText.Text = imported.Count > 0
                ? $"已导入并加密：{string.Join("、", imported)}（来源 {csvName}）"
                : "没有保存任何凭据。";

            InfoBarSeverity severity = !result.SavedAny
                ? InfoBarSeverity.Error
                : result.Errors.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            string message = result.Errors.Count > 0
                ? string.Join(Environment.NewLine, result.Errors.Take(5))
                : "凭据已使用 Windows DPAPI 加密保存。程序不提供导出。";
            InfoBarPresenter.Show(CsvImportInfoBar,
                result.SavedAny ? "凭据导入完成" : "凭据导入失败", message, severity);
        }
        catch (Exception ex)
        {
            string detail = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            CsvImportStatusText.Text = $"导入失败：{detail}";
            DiagnosticLogService.Error("settings.credentials.csv_import.failed", ex);
            InfoBarPresenter.Show(CsvImportInfoBar, "CSV 导入失败", detail, InfoBarSeverity.Error);
        }
        finally
        {
            _csvPickerActive = false;
        }
    }

    private void AccuracySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (AccuracyText is not null) AccuracyText.Text = $"{Math.Round(e.NewValue * 100):0}%";
    }

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        _service.RefreshEts();
        EtsPathText.Text = _service.EtsExe ?? "未找到 ETS 安装目录。";
    }

    private async void ChooseEtsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder,
                CommitButtonText = "选择此文件夹" };
            picker.FileTypeFilter.Add("*");
            MainWindow window = MainWindow.Current ?? throw new InvalidOperationException("窗口尚未准备好。");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            EtsDirectoryValidation validation = _service.ValidateEtsDirectory(folder.Path);
            if (!validation.IsValid)
            {
                InfoBarPresenter.Show(SaveInfoBar, "目录校验失败", validation.Message, InfoBarSeverity.Error);
                return;
            }
            var dialog = new ContentDialog { Title = "应用 ETS 安装目录？",
                Content = validation.ExecutablePath, PrimaryButtonText = "应用", CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
            if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
            string warnings = _service.ApplyEtsDirectory(validation.DirectoryPath);
            EtsPathText.Text = _service.EtsExe ?? "未找到 ETS 安装目录。";
            InfoBarPresenter.Show(SaveInfoBar, warnings.Length == 0 ? "ETS 目录已更新" : "目录已更新，但有提醒",
                warnings.Length == 0 ? validation.ExecutablePath ?? validation.DirectoryPath : warnings,
                warnings.Length == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex) { InfoBarPresenter.Show(SaveInfoBar, "选择失败", ex.Message, InfoBarSeverity.Error); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!double.IsFinite(SkipBox.Value) || SkipBox.Value < 0 || SkipBox.Value > 60)
                throw new ArgumentOutOfRangeException(nameof(SkipBox), "跳题间隔必须在 0 到 60 秒之间。");
            var settings = new AssistantSettings(AccuracySlider.Value,
                Guid.NewGuid().ToString("N"), SkipBox.Value,
                AutostartCheck.IsChecked == true,
                AppPreferencesService.Current.VerboseLoggingEnabled);
            _service.SaveSettings(settings);
            string warning = _service.TryInstallAutostart(settings.StartAtLogin);
            if (settings.StartAtLogin) _service.EnsureListener();
            InfoBarPresenter.Show(SaveInfoBar, warning.Length == 0 ? "已保存" : "设置已保存，登录启动项未更新",
                warning.Length == 0 ? "配置和 Windows 登录启动项已更新。" : warning,
                warning.Length == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            InfoBarPresenter.Show(SaveInfoBar, "保存失败", ex.Message, InfoBarSeverity.Error);
        }
    }
}

/// <summary>
/// Uses the desktop common file dialog because this app runs elevated. The WinRT
/// FileOpenPicker broker cannot reliably activate with an elevated owner window.
/// </summary>
internal static class NativeCsvFilePicker
{
    private const int OfnNoChangeDir = 0x00000008;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnExplorer = 0x00080000;
    private const int OfnHideReadOnly = 0x00000004;
    private const int FileNameBufferSize = 32768;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public nint Owner;
        public nint Instance;
        public nint Filter;
        public nint CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public nint File;
        public int MaxFile;
        public nint FileTitle;
        public int MaxFileTitle;
        public nint InitialDirectory;
        public nint Title;
        public int Flags;
        public ushort FileOffset;
        public ushort FileExtension;
        public nint DefaultExtension;
        public nint CustomData;
        public nint Hook;
        public nint TemplateName;
        public nint Reserved;
        public int ReservedValue;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetOpenFileNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OpenFileName fileName);

    [DllImport("comdlg32.dll", EntryPoint = "CommDlgExtendedError")]
    private static extern uint CommDlgExtendedError();

    public static string? PickCsv(nint owner)
    {
        if (owner == 0) throw new InvalidOperationException("无法获取 ETS Tool 窗口句柄，不能显示文件选择器。");

        nint filter = Marshal.StringToCoTaskMemUni("CSV 文件 (*.csv)\0*.csv\0所有文件 (*.*)\0*.*\0\0");
        nint title = Marshal.StringToCoTaskMemUni("选择 CSV 文件");
        nint extension = Marshal.StringToCoTaskMemUni("csv");
        char[] fileBuffer = new char[FileNameBufferSize];
        GCHandle fileBufferHandle = GCHandle.Alloc(fileBuffer, GCHandleType.Pinned);
        try
        {
            var dialog = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),
                Owner = owner,
                Filter = filter,
                FilterIndex = 1,
                File = fileBufferHandle.AddrOfPinnedObject(),
                MaxFile = FileNameBufferSize,
                Title = title,
                DefaultExtension = extension,
                Flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir | OfnHideReadOnly
            };

            if (GetOpenFileName(ref dialog))
            {
                int terminator = Array.IndexOf(fileBuffer, '\0');
                return new string(fileBuffer, 0, terminator < 0 ? fileBuffer.Length : terminator);
            }

            uint error = CommDlgExtendedError();
            if (error == 0) return null; // User cancelled.
            throw new Win32Exception(unchecked((int)error),
                $"Windows 无法打开 CSV 文件选择器（错误 0x{error:X8}）。");
        }
        finally
        {
            if (fileBufferHandle.IsAllocated) fileBufferHandle.Free();
            Marshal.FreeCoTaskMem(filter);
            Marshal.FreeCoTaskMem(title);
            Marshal.FreeCoTaskMem(extension);
        }
    }
}
