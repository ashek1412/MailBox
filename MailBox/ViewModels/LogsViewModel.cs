using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailBox.Models;
using MailBox.Services;
using System.Collections.ObjectModel;

namespace MailBox.ViewModels;

public partial class LogsViewModel : ObservableObject
{
    private readonly AccountRepository _accounts;

    [ObservableProperty] private ObservableCollection<MailLogModel> _logs = new();
    [ObservableProperty] private int    _total       = 0;
    [ObservableProperty] private int    _page        = 1;
    [ObservableProperty] private int    _perPage     = 50;
    [ObservableProperty] private string _typeFilter   = "all";
    [ObservableProperty] private string _statusFilter = "all";

    public int TotalPages => (int)Math.Ceiling(Total / (double)PerPage);

    public LogsViewModel(AccountRepository accounts)
    {
        _accounts = accounts;
        Load();
    }

    private void Load()
    {
        var (list, total) = _accounts.GetLogs(TypeFilter, StatusFilter, Page, PerPage);
        Total = total;
        Logs.Clear();
        foreach (var l in list) Logs.Add(l);
        OnPropertyChanged(nameof(TotalPages));
    }

    partial void OnTypeFilterChanged(string value)   { Page = 1; Load(); }
    partial void OnStatusFilterChanged(string value) { Page = 1; Load(); }

    [RelayCommand]
    private void PrevPage() { if (Page > 1)            { Page--; Load(); } }

    [RelayCommand]
    private void NextPage() { if (Page < TotalPages)   { Page++; Load(); } }

    [RelayCommand]
    private void ClearLogs()
    {
        if (System.Windows.MessageBox.Show(
                "Clear the currently filtered logs?", "Clear Logs",
                System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes) return;
        _accounts.ClearLogs(TypeFilter, StatusFilter);
        Page = 1;
        Load();
    }
}
