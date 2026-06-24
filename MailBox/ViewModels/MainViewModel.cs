using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailBox.Models;
using MailBox.Services;

namespace MailBox.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AccountRepository  _accounts;
    private readonly ImapSyncService    _imap;
    private readonly BackgroundSyncService _bgSync;
    private readonly SmtpSendService    _smtp;

    [ObservableProperty] private SidebarViewModel  _sidebar;
    [ObservableProperty] private EmailListViewModel _emailList;
    [ObservableProperty] private object?            _rightPanel;   // EmailViewerViewModel or LogsViewModel

    [ObservableProperty] private string  _statusMessage = "";
    [ObservableProperty] private bool    _isBusy        = false;


    public MainViewModel(
        AccountRepository accounts,
        ImapSyncService   imap,
        BackgroundSyncService bgSync,
        SmtpSendService   smtp)
    {
        _accounts = accounts;
        _imap     = imap;
        _bgSync   = bgSync;
        _smtp     = smtp;

        EmailList  = new EmailListViewModel(accounts);
        Sidebar    = new SidebarViewModel(accounts, imap, this);
        RightPanel = null;

        _bgSync.AccountProgress += (email, msg) =>
            System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => StatusMessage = $"⟳ {email} — {msg}");
        _bgSync.NewMailArrived  += OnNewMailArrived;
        EmailList.EmailsDeleted += account =>
        {
            Sidebar.AccountItems.FirstOrDefault(a => a.Account.Id == account.Id)
                   ?.RefreshTrashCount();
            Sidebar.RefreshUnreadCountFor(account);
        };

    }

    // Called by SidebarViewModel when a folder is selected
    internal void OnFolderSelected(AccountModel account, string folder)
    {
        EmailList.LoadEmails(account, folder);
        // If showing logs, switch back to email viewer area
        if (RightPanel is LogsViewModel) RightPanel = null;
    }

    // Called by EmailListViewModel when an email row is clicked
    internal void OnEmailSelected(AccountModel account, EmailModel email)
    {
        var viewer = new EmailViewerViewModel(account, email, _accounts, _imap, _smtp, this);
        RightPanel = viewer;
    }

    [RelayCommand]
    private void OpenLogs()
    {
        RightPanel = new LogsViewModel(_accounts);
    }

    [RelayCommand]
    internal async Task SyncAll()
    {
        IsBusy = true;
        StatusMessage = "Syncing all accounts…";
        try   { await _bgSync.SyncAllAsync(); }
        catch { }
        finally { IsBusy = false; StatusMessage = ""; }

        await Sidebar.RefreshCurrentAsync();
        if (EmailList.HasSelection)
            EmailList.Reload();
        else if (Sidebar.AccountItems.Count > 0)
            EmailList.LoadEmails(Sidebar.AccountItems[0].Account, "INBOX");
    }

    internal async Task SyncAllSilent()
    {
        try { await _bgSync.SyncAllAsync(); } catch { }
        await Sidebar.RefreshCurrentAsync();
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (EmailList.HasSelection) EmailList.Reload();
        });
    }

    private void OnNewMailArrived(AccountModel account, int count)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Sidebar.RefreshUnreadCountFor(account);
            if (EmailList.HasSelection)
                EmailList.Reload();
            else
                EmailList.LoadEmails(account, "INBOX");
            StatusMessage = $"📬 {count} new email(s) in {account.Name}";
        });

        Task.Run(() => ShowNewMailNotifications(account, count));
    }

    private static void ShowNewMailNotifications(AccountModel account, int count)
    {
        Services.NotificationService.PlaySound();

        try
        {
            if (count > 3)
            {
                Services.NotificationService.ShowToast(
                    $"📬 {count} new emails",
                    $"Account: {account.Email}");
                return;
            }

            var repo = new Services.MailDataRepository(account.Email);
            var (emails, _) = repo.GetEmails("INBOX", "", 1, count);

            if (emails.Count == 0)
            {
                // Fallback — DB query returned nothing (e.g. mail in non-INBOX folder)
                Services.NotificationService.ShowToast(
                    $"📬 {count} new email(s)",
                    $"Account: {account.Email}");
                return;
            }

            foreach (var email in emails)
                Services.NotificationService.ShowToast(
                    $"📧 New mail — {account.Email}",
                    $"From: {email.DisplaySender}",
                    email.Subject ?? "(no subject)");
        }
        catch
        {
            Services.NotificationService.ShowToast(
                $"📬 {count} new email(s)",
                $"Account: {account.Email}");
        }
    }

    [RelayCommand]
    private void ShowWindow()
    {
        if (System.Windows.Application.Current.MainWindow is MainWindow mw)
            mw.ShowAndActivate();
    }

    [RelayCommand]
    private void ComposeNew()
    {
        var all = _accounts.GetAll();
        if (all.Count == 0) return;
        var initial = Sidebar.AccountItems.FirstOrDefault()?.Account ?? all[0];
        var compose = new ComposeViewModel(initial, all, _accounts, _smtp,
            afterSendCallback: SyncAllSilent, main: this);
        new Views.ComposeWindow(compose).Show();
    }

    [RelayCommand]
    private void Backup()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Save MailBox Backup",
            Filter     = "MailBox Backup (*.zip)|*.zip",
            FileName   = $"MailBox_Backup_{DateTime.Now:yyyy-MM-dd_HH-mm}",
            DefaultExt = ".zip",
        };
        if (dlg.ShowDialog() != true) return;

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var win = new Views.BackupRestoreWindow(dlg.FileName, isBackup: true)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        win.ShowDialog();
    }

    [RelayCommand]
    private async Task Restore()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title      = "Open MailBox Backup",
            Filter     = "MailBox Backup (*.zip)|*.zip",
            DefaultExt = ".zip",
        };
        if (dlg.ShowDialog() != true) return;

        var confirm = System.Windows.MessageBox.Show(
            "Restoring a backup will REPLACE all current accounts, mail, and settings.\n\n" +
            "This cannot be undone. Continue?",
            "Restore Backup",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var restoreWin = new Views.BackupRestoreWindow(dlg.FileName, isBackup: false)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        restoreWin.ShowDialog();

        if (restoreWin.Success)
        {
            Sidebar.LoadAccounts();
            EmailList.Clear();
            RightPanel    = null;
            StatusMessage = "✅  Data restored — accounts reloaded.";
        }
    }

    [RelayCommand]
    private void ExportContacts()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Export Sender Addresses",
            Filter     = "CSV file (*.csv)|*.csv",
            FileName   = $"MailBox_Contacts_{DateTime.Now:yyyy-MM-dd}",
            DefaultExt = ".csv",
        };
        if (dlg.ShowDialog() != true) return;

        var accounts = _accounts.GetAll();
        // email → (name, accountEmail) — dedupe by from_email across all accounts
        var seen = new Dictionary<string, (string Name, string Account)>(StringComparer.OrdinalIgnoreCase);
        foreach (var acct in accounts)
        {
            try
            {
                var rows = new MailDataRepository(acct.Email).GetAllFromAddresses();
                foreach (var (email, name) in rows)
                    if (!string.IsNullOrWhiteSpace(email) && !seen.ContainsKey(email))
                        seen[email] = (name ?? "", acct.Email);
            }
            catch { /* skip inaccessible account db */ }
        }

        try
        {
            using var sw = new System.IO.StreamWriter(dlg.FileName, false, System.Text.Encoding.UTF8);
            sw.WriteLine("Name,Email,FoundInAccount");
            foreach (var kvp in seen)
            {
                var name    = kvp.Value.Name.Replace("\"", "\"\"");
                var email   = kvp.Key.Replace("\"", "\"\"");
                var account = kvp.Value.Account.Replace("\"", "\"\"");
                sw.WriteLine($"\"{name}\",\"{email}\",\"{account}\"");
            }
            StatusMessage = $"✅  Exported {seen.Count} contacts to CSV.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Exit() => System.Windows.Application.Current.Shutdown();

    internal SmtpSendService SmtpService => _smtp;
    internal AccountRepository AccountRepo => _accounts;
}
