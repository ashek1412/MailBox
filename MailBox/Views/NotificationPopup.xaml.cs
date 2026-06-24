using System.Windows;
using System.Windows.Media.Animation;

namespace MailBox.Views;

public partial class NotificationPopup : Window
{
    private static readonly List<NotificationPopup> _active = [];
    private static readonly object _lock = new();

    public NotificationPopup(string line1, string? line2 = null, string? line3 = null)
    {
        InitializeComponent();
        Line1.Text = line1;
        if (line2 != null) { Line2.Text = line2; Line2.Visibility = Visibility.Visible; }
        if (line3 != null) { Line3.Text = line3; Line3.Visibility = Visibility.Visible; }
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var screen = SystemParameters.WorkArea;

        lock (_lock)
        {
            // Stack above any already-visible popups
            var stackOffset = _active.Sum(w => w.ActualHeight + 6) + ActualHeight + 16;
            Left = screen.Right - ActualWidth - 16;
            Top  = screen.Bottom - stackOffset;
            _active.Add(this);
        }

        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));

        var timer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => { timer.Stop(); FadeOut(); };
        timer.Start();
    }

    private void FadeOut()
    {
        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280));
        anim.Completed += (_, _) =>
        {
            lock (_lock) { _active.Remove(this); }
            Close();
        };
        BeginAnimation(OpacityProperty, anim);
    }

    private void OnClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => FadeOut();
}
