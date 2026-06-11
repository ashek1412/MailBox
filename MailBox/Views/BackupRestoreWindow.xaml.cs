using MailBox.Services;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace MailBox.Views;

public partial class BackupRestoreWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private readonly bool   _isBackup;
    private readonly string _zipPath;
    private bool _finished = false;

    /// <summary>True when the operation completed without error (not cancelled).</summary>
    public bool Success { get; private set; } = false;

    public BackupRestoreWindow(string zipPath, bool isBackup)
    {
        InitializeComponent();
        _zipPath  = zipPath;
        _isBackup = isBackup;

        TitleText.Text  = isBackup ? "💾  Backing up data…" : "📥  Restoring data…";
        StatusText.Text = isBackup ? "Preparing backup…"   : "Preparing restore…";

        Loaded  += async (_, _) => await RunAsync();
        Closing += OnClosing;
    }

    // ── Operation ─────────────────────────────────────────────────────────────

    private async Task RunAsync()
    {
        var svc = new BackupService();

        svc.Progress += (msg, pct) =>
            Dispatcher.InvokeAsync(() =>
            {
                StatusText.Text  = msg;
                ProgressBar.Value = pct * 100;
            });

        try
        {
            if (_isBackup)
                await svc.BackupAsync(_zipPath, _cts.Token);
            else
                await svc.RestoreAsync(_zipPath, _cts.Token);

            OnSuccess();
        }
        catch (OperationCanceledException)
        {
            OnCancelled();
        }
        catch (Exception ex)
        {
            OnError(ex.Message);
        }
    }

    // ── State transitions ─────────────────────────────────────────────────────

    private void OnSuccess()
    {
        Success   = true;
        _finished = true;
        Dispatcher.InvokeAsync(() =>
        {
            IndeterminateBar.Visibility = Visibility.Collapsed;
            ProgressBar.Value           = 100;
            ProgressBar.Foreground      = new SolidColorBrush(Color.FromRgb(22, 163, 74)); // green

            TitleText.Text = _isBackup ? "✅  Backup complete" : "✅  Restore complete";
            StatusText.Text = _isBackup
                ? $"Saved to:\n{_zipPath}"
                : "All data has been restored.\nThe app will reload your accounts.";

            ActionBtn.Content = "Close";
            if (_isBackup) OpenFolderBtn.Visibility = Visibility.Visible;
        });
    }

    private void OnCancelled()
    {
        _finished = true;
        Dispatcher.InvokeAsync(() =>
        {
            IndeterminateBar.Visibility = Visibility.Collapsed;
            TitleText.Text  = "Cancelled";
            StatusText.Text = "The operation was cancelled.";
            ActionBtn.Content = "Close";
        });
    }

    private void OnError(string message)
    {
        _finished = true;
        Dispatcher.InvokeAsync(() =>
        {
            IndeterminateBar.Visibility  = Visibility.Collapsed;
            ProgressBar.Foreground       = new SolidColorBrush(Color.FromRgb(220, 38, 38)); // red

            TitleText.Text  = _isBackup ? "❌  Backup failed" : "❌  Restore failed";
            StatusText.Text = message;
            ActionBtn.Content = "Close";
        });
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private void ActionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_finished) _cts.Cancel();
        Close();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(_zipPath);
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            Process.Start("explorer.exe", folder);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Cancel the operation if the user closes the window via the X button
        if (!_finished) _cts.Cancel();
    }
}
