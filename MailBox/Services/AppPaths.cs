using System.IO;
namespace MailBox.Services;

/// <summary>
/// Central place for all file-system paths used by the app.
/// Everything lives under %APPDATA%\MailBox\.
/// </summary>
public static class AppPaths
{
    public static readonly string Root        = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MailBox");
    public static readonly string AccountsDb  = Path.Combine(Root, "accounts.db");
    public static readonly string MailDataDir = Path.Combine(Root, "maildata");
    public static readonly string MailDir     = Path.Combine(Root, "mail");
    public static readonly string LogsDir     = Path.Combine(Root, "logs");
    public static readonly string WebView2Dir = Path.Combine(Root, "webview2");

    public static string MailDataFile(string email)
    {
        var safe = string.Concat(email.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(MailDataDir, $"{safe}.sqlite");
    }

    public static string AttachmentDir(int accountId, int emailId) =>
        Path.Combine(MailDir, accountId.ToString(), emailId.ToString());

    public static void EnsureAll()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(MailDataDir);
        Directory.CreateDirectory(MailDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(WebView2Dir);
    }
}
