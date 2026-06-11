using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailBox.Models;
using MailBox.Services;
using System.Collections.ObjectModel;

namespace MailBox.ViewModels;

public partial class SidebarViewModel : ObservableObject
{
    private readonly AccountRepository _accounts;
    private readonly ImapSyncService   _imap;
    private readonly MainViewModel     _main;

    [ObservableProperty] private ObservableCollection<AccountItemViewModel> _accountItems = new();
    [ObservableProperty] private AccountItemViewModel? _selectedAccount;

    public SidebarViewModel(AccountRepository accounts, ImapSyncService imap, MainViewModel main)
    {
        _accounts = accounts;
        _imap     = imap;
        _main     = main;
        LoadAccounts();
    }

    public void LoadAccounts()
    {
        AccountItems.Clear();
        foreach (var a in _accounts.GetAll())
        {
            try { AccountItems.Add(new AccountItemViewModel(a, _imap, _accounts, _main)); }
            catch { /* skip broken account entry, don't crash the whole list */ }
        }
    }

    [RelayCommand]
    private void AddAccount()
    {
        var dlg = new Views.AccountDialog(null, _accounts);
        if (dlg.ShowDialog() == true) LoadAccounts();
    }

    [RelayCommand]
    private void OpenLogs() => _main.OpenLogsCommand.Execute(null);

    internal async Task RefreshCurrentAsync()
    {
        foreach (var item in AccountItems)
            await item.RefreshFoldersAsync();
    }

    internal void RefreshUnreadCountFor(AccountModel account)
    {
        var item = AccountItems.FirstOrDefault(i => i.Account.Id == account.Id);
        if (item == null) return;
        item.Account.LastSyncedAt = account.LastSyncedAt;
        item.RefreshUnread();
        item.UpdateLastSyncText();
    }
}

// ─── Per-account item in the sidebar ────────────────────────────────────────

public partial class AccountItemViewModel : ObservableObject
{
    private readonly ImapSyncService   _imap;
    private readonly AccountRepository _accounts;
    private readonly MainViewModel     _main;

    public AccountModel Account { get; }

    [ObservableProperty] private ObservableCollection<FolderItemViewModel> _folders = new();
    [ObservableProperty] private bool   _isExpanded      = false;
    [ObservableProperty] private bool   _isSyncing       = false;
    [ObservableProperty] private string _lastSyncText    = "Never synced";
    [ObservableProperty] private int    _totalUnread     = 0;
    [ObservableProperty] private string _avatarLetter    = "?";
    [ObservableProperty] private string _color           = "#1a73e8";
    [ObservableProperty] private string _connectionError = "";

    public AccountItemViewModel(AccountModel account, ImapSyncService imap,
        AccountRepository accounts, MainViewModel main)
    {
        Account   = account;
        _imap     = imap;
        _accounts = accounts;
        _main     = main;
        AvatarLetter = account.Name.Length > 0 ? account.Name[0].ToString().ToUpper() : "?";
        Color        = account.Color;
        UpdateLastSyncText();
        RefreshUnread();

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        timer.Tick += (_, _) => UpdateLastSyncText();
        timer.Start();
    }

    [RelayCommand]
    private async Task ToggleExpand()
    {
        IsExpanded = !IsExpanded;
        if (IsExpanded && Folders.Count == 0)
            await LoadFoldersAsync();
    }

    [RelayCommand]
    private async Task Sync()
    {
        IsSyncing       = true;
        ConnectionError = "";
        try
        {
            var svc = new ImapSyncService(_accounts);
            svc.Progress += msg => System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => _main.StatusMessage = msg);

            var result = await svc.SyncAsync(Account);

            if (!string.IsNullOrEmpty(result.ErrorMessage) && result.Folders == 0)
            {
                // Connection failed or nothing synced — show reason in sidebar
                ConnectionError = result.ErrorMessage;
            }
            else
            {
                ConnectionError = "";
                // Reload account row to get updated LastSyncedAt + sync state
                var updated = _accounts.GetById(Account.Id);
                if (updated != null)
                {
                    Account.LastSyncedAt    = updated.LastSyncedAt;
                    Account.SyncStateJson   = updated.SyncStateJson;
                    Account.InitialSyncDone = updated.InitialSyncDone;
                }
                UpdateLastSyncText();
                RefreshUnread();
                await LoadFoldersAsync();
            }
        }
        catch (Exception ex)
        {
            // Safety net — should not normally be reached
            ConnectionError = ex.Message;
        }
        finally
        {
            IsSyncing = false;
            _main.StatusMessage = "";
        }
    }

    [RelayCommand]
    private void Edit()
    {
        var dlg = new Views.AccountDialog(Account, _accounts);
        dlg.ShowDialog();
    }

    [RelayCommand]
    private void Delete()
    {
        if (System.Windows.MessageBox.Show(
                $"Remove {Account.Name} and all its local mail?",
                "Remove Account",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;

        // Clear UI state that references this account before deleting any data
        _main.EmailList.Clear();
        _main.RightPanel = null;

        // Delete attachment files directory
        var mailDir = Path.Combine(AppPaths.MailDir, Account.Id.ToString());
        if (Directory.Exists(mailDir))
            try { Directory.Delete(mailDir, recursive: true); } catch { }

        // Delete the per-account sqlite file
        var dbPath = AppPaths.MailDataFile(Account.Email);
        if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }

        _accounts.Delete(Account.Id);

        // Remove from sidebar
        if (_main.Sidebar.AccountItems.Contains(this))
            _main.Sidebar.AccountItems.Remove(this);
    }

    internal async Task LoadFoldersAsync()
    {
        MailKit.Net.Imap.ImapClient client;
        try
        {
            client = await ImapSyncService.ConnectAsync(Account);
            ConnectionError = "";
        }
        catch (Exception ex)
        {
            ConnectionError = ImapSyncService.HintMessage(ex, Account);
            return;
        }

        using (client)
        {
            var ns      = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : null;
            var fetched = await client.GetFoldersAsync(ns);

            // INBOX is special on many servers — not always returned by GetFoldersAsync
            var folderList = fetched.ToList();
            if (client.Inbox != null &&
                !folderList.Any(f => f.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)))
            {
                folderList.Insert(0, client.Inbox);
            }

            await client.DisconnectAsync(true);

            Folders.Clear();
            foreach (var f in folderList)
            {
                var upper = f.FullName.ToUpper();
                // Skip Archive, All Mail, server Drafts, and server Trash/Deleted
                // (both managed locally and injected as fixed folders below)
                if (upper.Contains("ARCHIVE") || upper.Contains("ALL MAIL") ||
                    upper.Contains("DRAFT")   || upper.Contains("TRASH") ||
                    upper.Contains("DELETED"))
                    continue;

                var repo   = new MailDataRepository(Account.Email);
                var unread = repo.GetUnreadCount(f.FullName);
                Folders.Add(new FolderItemViewModel(
                    new FolderInfo
                    {
                        FullName    = f.FullName,
                        Name        = f.Name,
                        Icon        = FolderInfo.IconFor(f.FullName),
                        UnreadCount = unread,
                    },
                    Account, _main));
            }

            var localRepo = new MailDataRepository(Account.Email);

            // Always inject local Drafts folder (after Sent)
            var draftsVm = new FolderItemViewModel(
                new FolderInfo { FullName = "Drafts", Name = "Drafts", Icon = "📝",
                    UnreadCount = localRepo.GetFolderCount("Drafts") },
                Account, _main);
            var sentIdx = Folders.ToList().FindIndex(fv => fv.Folder.FullName.ToUpper().Contains("SENT"));
            if (sentIdx >= 0 && sentIdx + 1 < Folders.Count)
                Folders.Insert(sentIdx + 1, draftsVm);
            else
                Folders.Add(draftsVm);

            // Always inject local Trash folder (at end)
            Folders.Add(new FolderItemViewModel(
                new FolderInfo { FullName = "Trash", Name = "Trash", Icon = "🗑",
                    UnreadCount = localRepo.GetFolderCount("Trash") },
                Account, _main));
        }
    }

    internal async Task RefreshFoldersAsync()
    {
        if (IsExpanded) await LoadFoldersAsync();
    }

    internal void RefreshTrashCount()
    {
        var trashVm = Folders.FirstOrDefault(f => f.Folder.FullName == "Trash");
        if (trashVm == null) return;
        try
        {
            var repo = new MailDataRepository(Account.Email);
            trashVm.Folder.UnreadCount = repo.GetFolderCount("Trash");
            trashVm.RefreshCount();
        }
        catch { }
    }

    internal void RefreshDraftsCount()
    {
        var draftVm = Folders.FirstOrDefault(f => f.Folder.FullName == "Drafts");
        if (draftVm == null) return;
        try
        {
            var repo = new MailDataRepository(Account.Email);
            draftVm.Folder.UnreadCount = repo.GetFolderCount("Drafts");
            draftVm.RefreshCount();
        }
        catch { }
    }

    internal void RefreshUnread()
    {
        try
        {
            var repo    = new MailDataRepository(Account.Email);
            TotalUnread = repo.GetTotalUnreadCount();   // all folders, not just INBOX
        }
        catch { TotalUnread = 0; }

        // Also refresh each visible folder's unread badge
        foreach (var folder in Folders)
            folder.RefreshCount();
    }

    internal void UpdateLastSyncText()
    {
        if (string.IsNullOrEmpty(Account.LastSyncedAt))
        { LastSyncText = "Never synced"; return; }
        if (DateTimeOffset.TryParse(Account.LastSyncedAt, out var dto))
        {
            var diff = DateTimeOffset.UtcNow - dto;
            LastSyncText = diff.TotalMinutes < 1  ? "Synced just now"
                         : diff.TotalHours   < 1  ? $"Synced {(int)diff.TotalMinutes}m ago"
                         : diff.TotalDays    < 1  ? $"Synced {(int)diff.TotalHours}h ago"
                         : $"Synced {dto.LocalDateTime:MMM d}";
        }
    }
}

public partial class FolderItemViewModel : ObservableObject
{
    public FolderInfo   Folder  { get; }
    public AccountModel Account { get; }
    private readonly MainViewModel _main;

    [ObservableProperty] private bool _isSelected  = false;
    [ObservableProperty] private int  _unreadCount = 0;

    public FolderItemViewModel(FolderInfo folder, AccountModel account, MainViewModel main)
    {
        Folder       = folder;
        Account      = account;
        _main        = main;
        _unreadCount = folder.UnreadCount;  // seed from initial load
    }

    /// Refreshes the count badge from the local SQLite DB.
    internal void RefreshCount()
    {
        try
        {
            var repo = new MailDataRepository(Account.Email);
            UnreadCount = (Folder.FullName == "Drafts" || Folder.FullName == "Trash")
                ? repo.GetFolderCount(Folder.FullName)
                : repo.GetUnreadCount(Folder.FullName);
        }
        catch { }
    }

    [RelayCommand]
    private void Select()
    {
        IsSelected = true;
        _main.OnFolderSelected(Account, Folder.FullName);
    }
}
