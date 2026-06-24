using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailBox.Models;
using MailBox.Services;
using System.Collections.ObjectModel;
using System.IO;

namespace MailBox.ViewModels;

public partial class EmailListViewModel : ObservableObject
{
    private readonly AccountRepository _accounts;
    private AccountModel? _currentAccount;
    private string        _currentFolder = "INBOX";

    [ObservableProperty] private ObservableCollection<EmailRowViewModel> _emails = new();
    [ObservableProperty] private int    _totalEmails    = 0;
    [ObservableProperty] private int    _currentPage    = 1;
    [ObservableProperty] private int    _perPage        = 50;
    [ObservableProperty] private string _search         = "";
    [ObservableProperty] private string _folderTitle    = "";
    [ObservableProperty] private string _accountEmail   = "";
    [ObservableProperty] private bool   _isLoading      = false;
    [ObservableProperty] private bool   _showUnreadOnly  = false;
    [ObservableProperty] private int    _selectedCount   = 0;
    [ObservableProperty] private bool   _showSelectMode  = false;

    public int  TotalPages     => (int)Math.Ceiling(TotalEmails / (double)PerPage);
    public bool CanPrev        => CurrentPage > 1;
    public bool CanNext        => CurrentPage < TotalPages;
    public bool HasSelection   => _currentAccount != null;
    public bool HasAnySelected => SelectedCount > 0;

    public event Action<AccountModel, EmailModel>? EmailSelected;
    public event Action<AccountModel>?             EmailsDeleted;

    public EmailListViewModel(AccountRepository accounts) => _accounts = accounts;

    partial void OnSelectedCountChanged(int value) =>
        OnPropertyChanged(nameof(HasAnySelected));

    partial void OnShowSelectModeChanged(bool value)
    {
        if (!value) { foreach (var r in Emails) r.IsChecked = false; SelectedCount = 0; }
    }

    public void LoadEmails(AccountModel account, string folder)
    {
        _currentAccount = account;
        _currentFolder  = folder;
        CurrentPage     = 1;
        Search          = "";
        ShowUnreadOnly  = false;
        ShowSelectMode  = false;
        SelectedCount   = 0;
        FolderTitle     = System.IO.Path.GetFileName(folder).Length > 0
                            ? System.IO.Path.GetFileName(folder) : folder;
        AccountEmail    = account.Email;
        Reload();
    }

    partial void OnShowUnreadOnlyChanged(bool value) { CurrentPage = 1; Reload(); }

    public void Reload()
    {
        if (_currentAccount == null) return;
        IsLoading     = true;
        SelectedCount = 0;
        try
        {
            var repo = new MailDataRepository(_currentAccount.Email);
            var (list, total) = repo.GetEmails(_currentFolder, Search, CurrentPage, PerPage, ShowUnreadOnly);
            TotalEmails = total;
            Emails.Clear();
            foreach (var e in list)
                Emails.Add(new EmailRowViewModel(e, _currentAccount, this));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(TotalPages));
            OnPropertyChanged(nameof(CanPrev));
            OnPropertyChanged(nameof(CanNext));
        }
    }

    public void Clear()
    {
        _currentAccount = null;
        _currentFolder  = "INBOX";
        Emails.Clear();
        TotalEmails    = 0;
        FolderTitle    = "";
        CurrentPage    = 1;
        SelectedCount  = 0;
        ShowSelectMode = false;
    }

    internal void OnRowChecked(bool isChecked)
    {
        SelectedCount = Math.Max(0, SelectedCount + (isChecked ? 1 : -1));
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (_currentAccount == null || SelectedCount == 0) return;
        var selected = Emails.Where(r => r.IsChecked).ToList();
        if (selected.Count == 0) return;

        bool inTrash = _currentFolder.Equals("Trash", StringComparison.OrdinalIgnoreCase);
        var msg = inTrash
            ? $"Permanently delete {selected.Count} email(s)? This cannot be undone."
            : $"Move {selected.Count} email(s) to Trash?";
        if (System.Windows.MessageBox.Show(msg, "Confirm Delete",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;

        var repo = new MailDataRepository(_currentAccount.Email);

        foreach (var row in selected)
        {
            if (inTrash)
            {
                foreach (var att in repo.GetAttachments(row.Email.Id))
                {
                    var p = att.AbsolutePath(AppPaths.Root);
                    if (File.Exists(p)) try { File.Delete(p); } catch { }
                }
                repo.DeleteEmail(row.Email.Id);
            }
            else
            {
                repo.MoveToFolder(row.Email.Id, "Trash");
            }
        }

        var account = _currentAccount;
        Reload();
        EmailsDeleted?.Invoke(account);
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in Emails) row.IsChecked = false;
        SelectedCount = 0;
    }

    [RelayCommand] private void ToggleSelectMode() => ShowSelectMode = !ShowSelectMode;

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var row in Emails) row.IsChecked = true;
        SelectedCount = Emails.Count;
    }

    [RelayCommand] private void ToggleUnreadFilter() => ShowUnreadOnly = !ShowUnreadOnly;
    [RelayCommand] private void DoSearch()   { CurrentPage = 1; Reload(); }
    [RelayCommand] private void ClearSearch(){ Search = ""; CurrentPage = 1; Reload(); }
    [RelayCommand] private void PrevPage()   { if (CanPrev) { CurrentPage--; Reload(); } }
    [RelayCommand] private void NextPage()   { if (CanNext) { CurrentPage++; Reload(); } }
    [RelayCommand] private void Refresh()    => Reload();

    internal void OnRowSelected(AccountModel account, EmailModel email) =>
        EmailSelected?.Invoke(account, email);
}

public partial class EmailRowViewModel : ObservableObject
{
    public EmailModel   Email   { get; }
    public AccountModel Account { get; }
    private readonly EmailListViewModel _list;

    [ObservableProperty] private bool _isSelected = false;
    [ObservableProperty] private bool _isChecked  = false;

    public EmailRowViewModel(EmailModel email, AccountModel account, EmailListViewModel list)
    {
        Email   = email;
        Account = account;
        _list   = list;
    }

    partial void OnIsCheckedChanged(bool value) => _list.OnRowChecked(value);

    [RelayCommand]
    private void Select()
    {
        IsSelected = true;
        _list.OnRowSelected(Account, Email);
    }
}
