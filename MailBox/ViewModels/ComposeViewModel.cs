using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailBox.Models;
using MailBox.Services;
using System.Collections.ObjectModel;

namespace MailBox.ViewModels;

public enum ComposeMode { New, Reply, Forward }

public partial class ComposeViewModel : ObservableObject
{
    private readonly AccountRepository _accounts;
    private readonly SmtpSendService   _smtp;
    private readonly Func<Task>?       _afterSendCallback;
    private readonly MainViewModel?    _main;

    [ObservableProperty] private string _to      = "";
    [ObservableProperty] private string _cc      = "";
    [ObservableProperty] private string _bcc     = "";
    [ObservableProperty] private string _subject = "";
    [ObservableProperty] private string _body    = "";
    [ObservableProperty] private bool   _isSending     = false;
    [ObservableProperty] private bool   _isSavingDraft = false;
    [ObservableProperty] private string _errorMessage  = "";
    [ObservableProperty] private ObservableCollection<RecipientSuggestion> _suggestions  = new();
    [ObservableProperty] private ObservableCollection<AttachmentFile>      _attachments  = new();
    [ObservableProperty] private bool         _hasSuggestions = false;
    [ObservableProperty] private AccountModel _selectedAccount = null!;

    public IReadOnlyList<AccountModel> Accounts { get; }

    private string _inReplyTo = "";
    public  string WindowTitle { get; }

    public ComposeViewModel(
        AccountModel account,
        IReadOnlyList<AccountModel> allAccounts,
        AccountRepository accounts,
        SmtpSendService smtp,
        ComposeMode mode = ComposeMode.New,
        EmailModel? replyTo = null,
        Func<Task>? afterSendCallback = null,
        MainViewModel? main = null)
    {
        Accounts           = allAccounts.Count > 0 ? allAccounts : new[] { account };
        _selectedAccount   = account;
        _accounts          = accounts;
        _smtp              = smtp;
        _afterSendCallback = afterSendCallback;
        _main              = main;

        WindowTitle = mode switch
        {
            ComposeMode.Reply   => "Reply",
            ComposeMode.Forward => "Forward",
            _                   => "New Message",
        };

        if (mode == ComposeMode.Reply && replyTo != null)
        {
            To         = replyTo.FromEmail ?? "";
            Subject    = "Re: " + System.Text.RegularExpressions.Regex.Replace(replyTo.Subject, @"^(Re:\s*)+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            _inReplyTo = replyTo.MessageId ?? "";
            Body       = BuildQuote(replyTo);
        }
        else if (mode == ComposeMode.Forward && replyTo != null)
        {
            Subject = "Fwd: " + replyTo.Subject;
            Body    = BuildQuote(replyTo);
        }
    }

    [RelayCommand]
    private void AddAttachment()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Attach Files" };
        if (dlg.ShowDialog() != true) return;
        foreach (var path in dlg.FileNames)
            if (Attachments.All(a => a.Path != path))
                Attachments.Add(new AttachmentFile(path));
    }

    [RelayCommand]
    private void RemoveAttachment(AttachmentFile att) => Attachments.Remove(att);

    [RelayCommand]
    private async Task Send()
    {
        ErrorMessage = "";
        if (string.IsNullOrWhiteSpace(To))      { ErrorMessage = "To is required.";      return; }
        if (string.IsNullOrWhiteSpace(Subject)) { ErrorMessage = "Subject is required."; return; }
        if (string.IsNullOrWhiteSpace(Body))    { ErrorMessage = "Body is required.";    return; }

        IsSending = true;
        try
        {
            var req = new ComposeRequest(
                To:              To,
                Cc:              Cc,
                Bcc:             Bcc,
                Subject:         Subject,
                Body:            Body,
                IsHtml:          true,
                InReplyTo:       _inReplyTo,
                AttachmentPaths: Attachments.Select(a => a.Path).ToList()
            );
            await _smtp.SendAsync(SelectedAccount, req);
            SendSucceeded?.Invoke();
            if (_afterSendCallback != null) _ = _afterSendCallback();
        }
        catch (Exception ex)
        {
            ErrorMessage = "Send failed: " + ex.Message;
        }
        finally { IsSending = false; }
    }

    [RelayCommand]
    private void SearchRecipients(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
        { Suggestions.Clear(); HasSuggestions = false; return; }

        var parts   = query.Split(',');
        var current = parts.Last().Trim();
        if (current.Length < 2) { Suggestions.Clear(); HasSuggestions = false; return; }

        var repo    = new MailDataRepository(SelectedAccount.Email);
        var results = repo.SuggestRecipients(current, SelectedAccount.Email);

        Suggestions.Clear();
        foreach (var (email, name) in results)
            Suggestions.Add(new RecipientSuggestion(email, name));
        HasSuggestions = Suggestions.Count > 0;
    }

    [RelayCommand]
    private void PickSuggestion(RecipientSuggestion s)
    {
        var parts  = To.Split(',').Select(p => p.Trim()).ToList();
        var label  = string.IsNullOrEmpty(s.Name) ? s.Email : $"{s.Name} <{s.Email}>";
        if (parts.Count > 0) parts[^1] = label;
        else parts.Add(label);
        To = string.Join(", ", parts) + ", ";
        Suggestions.Clear();
        HasSuggestions = false;
    }

    [RelayCommand]
    private void SaveDraft()
    {
        if (string.IsNullOrWhiteSpace(Subject) && string.IsNullOrWhiteSpace(Body))
        { ErrorMessage = "Nothing to save."; return; }

        IsSavingDraft = true;
        ErrorMessage  = "";
        try
        {
            var repo = new MailDataRepository(SelectedAccount.Email);
            repo.SaveDraft(SelectedAccount.Email, SelectedAccount.Name ?? "",
                To, Cc, Subject, Body, "Drafts");

            // Refresh the Drafts folder badge in the sidebar
            _main?.Sidebar.AccountItems
                .FirstOrDefault(a => a.Account.Id == SelectedAccount.Id)?
                .RefreshDraftsCount();

            DraftSaved?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorMessage = "Save failed: " + ex.Message;
        }
        finally { IsSavingDraft = false; }
    }

    public event Action? SendSucceeded;
    public event Action? DraftSaved;

    private static string BuildQuote(EmailModel email)
    {
        var from = string.IsNullOrEmpty(email.FromName)
            ? email.FromEmail
            : $"{email.FromName} &lt;{email.FromEmail}&gt;";
        var body = !string.IsNullOrEmpty(email.BodyHtml)
            ? email.BodyHtml
            : $"<div style='white-space:pre-wrap'>{System.Net.WebUtility.HtmlEncode(email.BodyText ?? "")}</div>";

        return $"<p><br></p>" +
               $"<div style='border-left:3px solid #d0d5dd;padding-left:12px;color:#374151'>" +
               $"<p style='font-size:0.78rem;color:#6b7280;margin:0 0 8px'>On {email.FormattedDate}, <strong>{from}</strong> wrote:</p>" +
               body +
               "</div>";
    }
}

public record AttachmentFile(string Path)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

public record RecipientSuggestion(string Email, string Name)
{
    public string Label => string.IsNullOrEmpty(Name) ? Email : $"{Name} <{Email}>";
    public string AvatarLetter => (string.IsNullOrEmpty(Name) ? Email ?? "?" : Name)[0].ToString().ToUpper();
}
