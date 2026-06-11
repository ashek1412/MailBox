using MailBox.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace MailBox.Views;

public partial class LogsView : UserControl
{
    public LogsView() { InitializeComponent(); }

    private void TypeFilter_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is LogsViewModel vm && sender is RadioButton rb)
            vm.TypeFilter = rb.Tag?.ToString() ?? "all";
    }

    private void StatusFilter_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is LogsViewModel vm && sender is RadioButton rb)
            vm.StatusFilter = rb.Tag?.ToString() ?? "all";
    }
}
