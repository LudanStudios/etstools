using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ETSAssistant.WinUI3.Pages;

public sealed partial class AboutPage : Page
{
    private int _versionTaps;

    public AboutPage()
    {
        InitializeComponent();
        Assembly assembly = typeof(AboutPage).Assembly;
        Version? version = assembly.GetName().Version;
        VersionText.Text = version is null
            ? "1.2.4"
            : $"{version.Major}.{version.Minor}.{version.Build}";

        string location = assembly.Location;
        ReleaseText.Text = File.Exists(location)
            ? File.GetLastWriteTime(location).ToString("yyyy-MM-dd HH:mm")
            : "未知";
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e) =>
        OpenLink("https://github.com/LudanStudios/etstool");

    private void OpenHomepage_Click(object sender, RoutedEventArgs e) =>
        OpenLink("https://azursmelt.pages.dev");

    private async void VersionText_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        _versionTaps++;
        if (_versionTaps < 7) return;
        _versionTaps = 0;
        if (MainWindow.Current is { } window) await window.UnlockDeveloperToolsAsync();
    }

    private static void OpenLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{url}\"") { UseShellExecute = false });
        }
        catch (Exception) { }
    }
}









