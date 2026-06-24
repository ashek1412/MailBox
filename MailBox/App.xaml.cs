using MailBox.Services;
using MailBox.ViewModels;
using MailBox.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Web.WebView2.Core;
using System.Windows;

namespace MailBox;

public partial class App : Application
{
    private IHost? _host;
    private static System.Threading.Mutex? _instanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance — a second process would fight over the WebView2 user-data
        // folder and SQLite files, leaving email bodies and the compose editor blank.
        _instanceMutex = new System.Threading.Mutex(true, @"Local\MailBox.Desktop.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "MailBox is already running.\n\nCheck the system tray (bottom-right corner) and click the MailBox icon to open it.",
                "MailBox", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Check for WebView2 runtime — required for email rendering and compose editor
        try { CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch
        {
            var result = MessageBox.Show(
                "MailBox requires the Microsoft WebView2 Runtime to display emails.\n\n" +
                "It is not installed on this machine.\n\n" +
                "Click OK to open the download page, or Cancel to continue without email rendering.",
                "WebView2 Runtime Required",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.OK)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://go.microsoft.com/fwlink/p/?LinkId=2124703") { UseShellExecute = true });
        }

        // Ensure %APPDATA%\MailBox\ directories exist
        AppPaths.EnsureAll();

        // Register AUMID so WinRT toast notifications appear in Action Center
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"SOFTWARE\Classes\AppUserModelId\MailBox.Desktop");
            key.SetValue("DisplayName", "MailBox");
        }
        catch { }

        // Build DI host
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<AccountRepository>();
                services.AddSingleton<ImapSyncService>();
                services.AddSingleton<SmtpSendService>();
                services.AddSingleton<BackgroundSyncService>();
                services.AddHostedService(p => p.GetRequiredService<BackgroundSyncService>());
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        var vm = _host.Services.GetRequiredService<MainViewModel>();
        if (Resources["TrayIcon"] is H.NotifyIcon.TaskbarIcon tray)
        {
            tray.DataContext = vm;
            tray.ForceCreate(enablesEfficiencyMode: false);
        }

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
