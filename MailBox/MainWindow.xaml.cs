using MailBox.ViewModels;
using System.Windows;

namespace MailBox;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        // Wire email selection from list to viewer
        vm.EmailList.EmailSelected += (account, email) =>
            Application.Current.Dispatcher.InvokeAsync(() => vm.OnEmailSelected(account, email));
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void Window_StateChanged(object sender, EventArgs e) { }

    public void ShowAndActivate()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
}
