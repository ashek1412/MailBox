namespace MailBox.Models;

public class FolderInfo
{
    public string FullName   { get; set; } = "";
    public string Name       { get; set; } = "";
    public string Icon       { get; set; } = "📁";
    public int    UnreadCount{ get; set; }

    // Well-known folder icons
    public static string IconFor(string fullName) => fullName.ToUpper() switch
    {
        "INBOX"                => "📥",
        "SENT" or "[GMAIL]/SENT MAIL" or "SENT MESSAGES" => "📤",
        "DRAFTS" or "[GMAIL]/DRAFTS"                     => "📝",
        "TRASH" or "[GMAIL]/TRASH" or "DELETED ITEMS" or "DELETED MESSAGES" => "🗑",
        "SPAM"  or "[GMAIL]/SPAM"  or "JUNK" or "JUNK EMAIL"                => "⚠",
        "[GMAIL]/STARRED" or "STARRED"                   => "⭐",
        "[GMAIL]/ALL MAIL"                               => "📦",
        "ARCHIVE"                                        => "🗄",
        _ => "📁",
    };
}
