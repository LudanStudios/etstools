using ETSAssistant.WinUI3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;

namespace ETSAssistant.WinUI3.Pages;

public sealed partial class PreferencesPage : Page
{
    private readonly AppPreferencesService _preferences = AppPreferencesService.Current;
    private bool _loading;

    public PreferencesPage()
    {
        InitializeComponent();
        _loading = true;
        ThemeBox.SelectedIndex = _preferences.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0
        };
        LanguageBox.SelectedIndex = _preferences.Language == "en-US" ? 1 : 0;
        AutoUpdateToggle.IsOn = _preferences.AutoUpdateEnabled;
        _loading = false;
        LocalizationService.Current.Apply(this);
        RefreshUpdateSettings();
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _preferences.SetTheme(ThemeBox.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System"
        });
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _preferences.SetLanguage(LanguageBox.SelectedIndex == 1 ? "en-US" : "zh-CN");
    }

    private void AutoUpdateToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading && !_preferences.InternalTestNoUpdatesEnabled)
            _preferences.SetAutoUpdate(AutoUpdateToggle.IsOn);
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_preferences.InternalTestNoUpdatesEnabled) return;
        CheckUpdatesButtonEnabled(false);
        UpdateStatusText.Text = "正在检查 GitHub 代码版本和 Release 版本…";
        try
        {
            UpdateCheckResult result = await new GitHubUpdateService().CheckAsync();
            _preferences.MarkUpdateCheck(result.CheckedAt);
            UpdateStatusText.Text = $"{result.Message} 检查时间：{result.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
            if (result.UpdateAvailable)
            {
                var dialog = new ContentDialog
                {
                    Title = "发现可用更新",
                    Content = result.Message + "\n\n现在打开 GitHub 查看提交或下载页面？",
                    PrimaryButtonText = "打开 GitHub",
                    CloseButtonText = "稍后",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };
                if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog) == ContentDialogResult.Primary)
                    Process.Start(new ProcessStartInfo(result.Url) { UseShellExecute = true });
                InfoBarPresenter.Show(UpdateInfoBar, "发现更新", result.Message, InfoBarSeverity.Informational);
            }
            else InfoBarPresenter.Show(UpdateInfoBar, "检查完成", result.Message, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "检查失败：" + ex.Message;
            InfoBarPresenter.Show(UpdateInfoBar, "无法检查更新", ex.Message, InfoBarSeverity.Warning);
        }
        finally { CheckUpdatesButtonEnabled(true); }
    }

    private void CheckUpdatesButtonEnabled(bool enabled)
    {
        CheckUpdatesButton.IsEnabled = enabled;
    }

    private void RefreshUpdateSettings()
    {
        bool internalTestMode = _preferences.InternalTestNoUpdatesEnabled;
        UpdateControlsPanel.Visibility = internalTestMode ? Visibility.Collapsed : Visibility.Visible;
        InternalTestNoUpdatesText.Visibility = internalTestMode ? Visibility.Visible : Visibility.Collapsed;
        if (!internalTestMode) return;

        Version? version = typeof(PreferencesPage).Assembly.GetName().Version;
        string versionText = version is null ? "1.2.4" : $"{version.Major}.{version.Minor}.{version.Build}";
        string format = LocalizationService.Current.Translate("当前版本：{0} 内部测试版本，无法配置更新。");
        InternalTestNoUpdatesText.Text = string.Format(format, versionText);
    }
}
