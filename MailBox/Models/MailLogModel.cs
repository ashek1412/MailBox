namespace MailBox.Models;

public class MailLogModel
{
    public int     Id           { get; set; }
    public int?    AccountId    { get; set; }
    public string? AccountEmail { get; set; }
    public string? AccountName  { get; set; }
    public string  Type         { get; set; } = "";   // connection | send | receive
    public string  Status       { get; set; } = "";   // success | error
    public string  Message      { get; set; } = "";
    public string? Context      { get; set; }         // JSON
    public string? CreatedAt    { get; set; }

    public bool IsError => Status == "error";

    public string FormattedTime
    {
        get
        {
            if (string.IsNullOrEmpty(CreatedAt)) return "";
            if (DateTime.TryParse(CreatedAt, out var dt))
                return dt.ToString("MMM d, HH:mm:ss");
            return CreatedAt;
        }
    }
}
