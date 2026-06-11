using MailBox.Models;
using MailBox.Services;
using MailBox.ViewModels;
using System.Windows;

namespace MailBox.Views;

public partial class AccountDialog : Window
{
    public AccountDialog(AccountModel? existing, AccountRepository accounts)
    {
        InitializeComponent();
        var vm = new AccountDialogViewModel(existing, accounts);
        vm.RequestClose += result => { DialogResult = result; Close(); };
        DataContext = vm;
    }

    // WPF PasswordBox doesn't support data binding — sync manually
    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AccountDialogViewModel vm)
            vm.Password = PasswordBox.Password;
    }
}
