using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace MailBox.Services;

public static class NotificationService
{
    private const string Aumid = "MailBox.Desktop";
    private static string? _wavPath;

    public static void ShowToast(string line1, string? line2 = null, string? line3 = null)
    {
        // Windows Action Center toast
        TryShowWinRtToast(line1, line2, line3);

        // Custom WPF popup (bottom-right corner)
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try { new Views.NotificationPopup(line1, line2, line3).Show(); }
            catch { }
        });
    }

    private static void TryShowWinRtToast(string line1, string? line2, string? line3)
    {
        try
        {
            var lines = new[] { line1, line2, line3 }
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => $"<text>{SecurityElement.Escape(s)}</text>");

            var xml = new XmlDocument();
            xml.LoadXml($"""
                <toast>
                  <visual>
                    <binding template="ToastGeneric">
                      {string.Join("", lines)}
                    </binding>
                  </visual>
                  <audio silent="true"/>
                </toast>
                """);

            ToastNotificationManager.CreateToastNotifier(Aumid)
                .Show(new ToastNotification(xml));
        }
        catch { }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern bool PlaySound(string? sound, IntPtr hmod, uint flags);
    private const uint SND_ASYNC    = 0x00000001;
    private const uint SND_FILENAME = 0x00020000;

    private static string? EnsureWavExtracted()
    {
        if (_wavPath != null && File.Exists(_wavPath))
            return _wavPath;

        try
        {
            var dest = Path.Combine(AppPaths.Root, "notification.wav");
            if (!File.Exists(dest))
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var resourceName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("notification.wav", StringComparison.OrdinalIgnoreCase));
                if (resourceName == null) return null;
                using var src = asm.GetManifestResourceStream(resourceName)!;
                using var dst = File.Create(dest);
                src.CopyTo(dst);
            }
            _wavPath = dest;
            return _wavPath;
        }
        catch { return null; }
    }

    public static void PlaySound()
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var path = EnsureWavExtracted();
            if (path != null)
                PlaySound(path, IntPtr.Zero, SND_FILENAME | SND_ASYNC);
        });
    }
}
