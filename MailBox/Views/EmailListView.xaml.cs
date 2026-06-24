using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MailBox.ViewModels;

namespace MailBox.Views;

public partial class EmailListView : UserControl
{
    public EmailListView() { InitializeComponent(); }

    // Delete key → delete selected emails
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && DataContext is EmailListViewModel vm && vm.HasAnySelected)
        {
            vm.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Prevent checkbox click from also firing the row's SelectCommand
    private void CheckBox_Click(object sender, RoutedEventArgs e) =>
        e.Handled = true;
}
