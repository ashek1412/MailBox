using System.ComponentModel;

namespace MailBox.Models;

public class EmailModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public int     Id            { get; set; }
    public string  Folder        { get; set; } = "";
    public int     Uid           { get; set; }
    public string? MessageId     { get; set; }
    public string  Subject       { get; set; } = "(No Subject)";
    public string? FromName      { get; set; }
    public string? FromEmail     { get; set; }
    public string? ToAddresses   { get; set; }
    public string? CcAddresses   { get; set; }
    public string? SentAt        { get; set; }

    private int _isRead;
    public int IsRead
    {
        get => _isRead;
        set { _isRead = value; Notify(nameof(IsRead)); Notify(nameof(IsReadBool)); }
    }

    private int _isFlagged;
    public int IsFlagged
    {
        get => _isFlagged;
        set { _isFlagged = value; Notify(nameof(IsFlagged)); Notify(nameof(IsFlaggedBool)); }
    }

    public int     HasAttachment { get; set; }
    public string? BodyHtml      { get; set; }
    public string? BodyText      { get; set; }
    public string? SyncedAt      { get; set; }

    public bool IsReadBool        => IsRead    != 0;
    public bool IsFlaggedBool     => IsFlagged != 0;
    public bool HasAttachmentBool => HasAttachment != 0;

    public string DisplaySender =>
        !string.IsNullOrWhiteSpace(FromName) ? FromName : (FromEmail ?? "Unknown");

    public string AvatarLetter =>
        (DisplaySender.Length > 0 ? DisplaySender[0].ToString() : "?").ToUpper();

    public string FormattedDate
    {
        get
        {
            if (string.IsNullOrEmpty(SentAt)) return "";
            if (DateTime.TryParse(SentAt, out var dt))
            {
                var now = DateTime.Now;
                if (dt.Date == now.Date)                  return dt.ToString("h:mm tt");
                if (dt.Date == now.Date.AddDays(-1))      return "Yesterday";
                if ((now - dt).TotalDays < 7)             return dt.ToString("ddd");
                return dt.ToString("MMM d");
            }
            return SentAt;
        }
    }
}
