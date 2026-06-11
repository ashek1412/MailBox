using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailBox.Models;
using MailBox.Services;
using System.Collections.ObjectModel;

namespace MailBox.ViewModels;

public partial class EmailListViewModel : ObservableObject
{
    private readonly AccountRepository _accounts;
    private AccountModel? _currentAccount;
    private string        _currentFolder = "INBOX";

    [ObservableProperty] private ObservableCollection<EmailRowViewModel> _emails = new();
    [ObservableProperty] private int    _totalEmails   = 0;
    [ObservableProperty] private int    _currentPage   = 1;
    [ObservableProperty] private int    _perPage       = 50;
    [ObservableProperty] private string _search        = "";
    [ObservableProperty] private string _folderTitle   = "";
    [ObservableProperty] private bool   _isLoading     = false;
    [ObservableProperty] private bool   _showUnreadOnly = false;

    public int TotalPages => (int)Math.Ceiling(TotalEmails / (double)PerPage);
    public bool CanPrev   => CurrentPage > 1;
    public bool CanNext   => CurrentPage < TotalPages;

    // Raised when user selects a row — MainViewModel wires this up
    public event Action<AccountModel, EmailModel>? EmailSelected;

    public EmailListViewModel(AccountRepository accounts)
    {
        _accounts = accounts;
    }

    public void LoadEmails(AccountModel account, string folder)
    {
        _currentAccount = account;
        _currentFolder  = folder;
        CurrentPage     = 1;
        Search          = "";
        ShowUnreadOnly  = false;   // reset filter on folder change
        FolderTitle     = System.IO.Path.GetFileName(folder).Length > 0
                            ? System.IO.Path.GetFileName(folder) : folder;
        Reload();
    }

    // Auto-reload when filter toggled
    partial void OnShowUnreadOnlyChanged(bool value)
    {
        CurrentPage = 1;
        Reload();
    }

    public void Reload()
    {
        if (_currentAccount == null) return;
        IsLoading = true;
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

    /// Resets the list to empty (used after restore when accounts change).
    public void Clear()
    {
        _currentAccount = null;
        _currentFolder  = "INBOX";
        Emails.Clear();
        TotalEmails = 0;
        FolderTitle = "";
        CurrentPage = 1;
    }

    [RelayCommand]
    private void ToggleUnreadFilter() => ShowUnreadOnly = !ShowUnreadOnly;

    [RelayCommand]
    private void DoSearch() { CurrentPage = 1; Reload(); }

    [RelayCommand]
    private void ClearSearch() { Search = ""; CurrentPage = 1; Reload(); }

    [RelayCommand]
    private void PrevPage() { if (CanPrev) { CurrentPage--; Reload(); } }

    [RelayCommand]
    private void NextPage() { if (CanNext) { CurrentPage++; Reload(); } }

    [RelayCommand]
    private void Refresh() => Reload();

    internal void OnRowSelected(AccountModel account, EmailModel email) =>
        EmailSelected?.Invoke(account, email);
}

public partial class EmailRowViewModel : ObservableObject
{
    public EmailModel   Email   { get; }
    public AccountModel Account { get; }
    private readonly EmailListViewModel _list;

    [ObservableProperty] private bool _isSelected = false;

    public EmailRowViewModel(EmailModel email, AccountModel account, EmailListViewModel list)
    {
        Email   = email;
        Account = account;
        _list   = list;
    }

    [RelayCommand]
    private void Select()
    {
        IsSelected = true;
        _list.OnRowSelected(Account, Email);
    }
}
