using MailBox.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace MailBox.Services;

public class SmtpSendService
{
    private readonly AccountRepository _accounts;

    public SmtpSendService(AccountRepository accounts)
    {
        _accounts = accounts;
    }

    private static async Task AppendToSentAsync(AccountModel account, MimeMessage msg, CancellationToken ct)
    {
        try
        {
            using var imap = await ImapSyncService.ConnectAsync(account, ct);

            // Try special Sent folder first, fall back to name search
            IMailFolder? sent = null;
            try { sent = imap.GetFolder(SpecialFolder.Sent); } catch { }

            if (sent == null)
            {
                var ns = imap.PersonalNamespaces.Count > 0 ? imap.PersonalNamespaces[0] : null;
                if (ns != null)
                {
                    var folders = await imap.GetFoldersAsync(ns, cancellationToken: ct);
                    sent = folders.FirstOrDefault(f =>
                        f.Name.Equals("Sent", StringComparison.OrdinalIgnoreCase) ||
                        f.Name.Equals("Sent Mail", StringComparison.OrdinalIgnoreCase) ||
                        f.Name.Equals("Sent Items", StringComparison.OrdinalIgnoreCase));
                }
            }

            if (sent == null) return;

            await sent.OpenAsync(FolderAccess.ReadWrite, ct);
            await sent.AppendAsync(msg, MessageFlags.Seen, ct);
            await sent.CloseAsync(false, ct);
            await imap.DisconnectAsync(true, ct);
        }
        catch { /* best-effort: don't fail the send if IMAP append fails */ }
    }

    // Parse a raw address string (comma or semicolon separated, display names OK)
    private static void AddAddresses(InternetAddressList dest, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        try { foreach (var a in InternetAddressList.Parse(raw.Replace(';', ','))) dest.Add(a); }
        catch { /* skip malformed field */ }
    }

    public async Task SendAsync(AccountModel account, ComposeRequest req, CancellationToken ct = default)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(account.Name?.Trim(), account.Email.Trim()));

        AddAddresses(msg.To,  req.To);
        AddAddresses(msg.Cc,  req.Cc);
        AddAddresses(msg.Bcc, req.Bcc);

        msg.Subject = req.Subject;

        if (!string.IsNullOrEmpty(req.InReplyTo))
            msg.InReplyTo = req.InReplyTo;

        var builder = new BodyBuilder();
        if (req.IsHtml)
            builder.HtmlBody = req.Body;
        else
            builder.TextBody = req.Body;

        if (req.AttachmentPaths != null)
            foreach (var path in req.AttachmentPaths.Where(System.IO.File.Exists))
                builder.Attachments.Add(path);

        msg.Body = builder.ToMessageBody();

        using var client = new SmtpClient
        {
            CheckCertificateRevocation = false,
            ServerCertificateValidationCallback = (s, c, h, e) => true,
        };
        var ssl = account.SmtpEncryption.ToLower() switch
        {
            "ssl"  => SecureSocketOptions.SslOnConnect,
            "tls"  => SecureSocketOptions.StartTls,
            _      => SecureSocketOptions.None,
        };

        await client.ConnectAsync(account.SmtpHost.Trim(), account.SmtpPort, ssl, ct);
        var password = PasswordVault.Decrypt(account.EncryptedPassword);
        await client.AuthenticateAsync(account.Username.Trim(), password, ct);
        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);

        await AppendToSentAsync(account, msg, ct);

        _accounts.InsertLog(new MailLogModel
        {
            AccountId    = account.Id,
            AccountEmail = account.Email,
            AccountName  = account.Name,
            Type         = "send",
            Status       = "success",
            Message      = $"Sent to {req.To} — {req.Subject}",
        });
    }
}

public record ComposeRequest(
    string To,
    string Cc,
    string Bcc,
    string Subject,
    string Body,
    bool IsHtml = true,
    string? InReplyTo = null,
    List<string>? AttachmentPaths = null
);
