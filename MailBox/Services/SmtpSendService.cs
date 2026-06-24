using MailBox.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Proxy;
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
            Timeout = 60_000,   // 60 s — VPN connections need more headroom than direct
            CheckCertificateRevocation = false,
            ServerCertificateValidationCallback = (s, c, h, e) => true,
        };
        if (account.HasProxy)
            client.ProxyClient = new Socks5Client(account.ProxyHost!, account.ProxyPort);
        var ssl = account.SmtpEncryption.ToLower() switch
        {
            "ssl"  => SecureSocketOptions.SslOnConnect,
            "tls"  => SecureSocketOptions.StartTls,
            _      => SecureSocketOptions.None,
        };

        try
        {
            await client.ConnectAsync(account.SmtpHost.Trim(), account.SmtpPort, ssl, ct);
            var password = PasswordVault.Decrypt(account.EncryptedPassword);
            await client.AuthenticateAsync(account.Username.Trim(), password, ct);
            await client.SendAsync(msg, ct);
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex)
        {
            var hint = SmtpHintMessage(ex, account);
            _accounts.InsertLog(new MailLogModel
            {
                AccountId    = account.Id,
                AccountEmail = account.Email,
                AccountName  = account.Name,
                Type         = "send",
                Status       = "error",
                Message      = $"SMTP failed to {req.To}: {hint}",
            });
            throw new Exception(hint, ex);
        }

        // Best-effort append to server Sent folder — runs with its own short timeout
        // so a blocked IMAP port doesn't freeze the UI after a successful send.
        using var appendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        appendCts.CancelAfter(TimeSpan.FromSeconds(20));
        await AppendToSentAsync(account, msg, appendCts.Token);

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

    private static string SmtpHintMessage(Exception ex, AccountModel account)
    {
        var msg = ex.Message;

        if (msg.Contains("AUTHENTICATIONFAILED", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Invalid credentials",  StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("535",                  StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Username and Password", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("LOGIN failed",          StringComparison.OrdinalIgnoreCase))
        {
            if (account.SmtpHost.Contains("gmail", StringComparison.OrdinalIgnoreCase))
                return "Gmail authentication failed. Use an App Password — not your regular Gmail password. " +
                       "Go to myaccount.google.com → Security → App Passwords.";
            if (account.SmtpHost.Contains("outlook", StringComparison.OrdinalIgnoreCase) ||
                account.SmtpHost.Contains("office365", StringComparison.OrdinalIgnoreCase))
                return "Outlook authentication failed. If MFA is enabled use an App Password, " +
                       "and ensure SMTP AUTH is enabled in Outlook settings.";
            return $"SMTP authentication failed — wrong username or password. {msg}";
        }

        if (msg.Contains("Connection refused",   StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("No connection could",  StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("actively refused",     StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("timed out",            StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("TimedOut",             StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Operation was cancelled", StringComparison.OrdinalIgnoreCase))
            return msg.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("TimedOut",  StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("Operation was cancelled", StringComparison.OrdinalIgnoreCase)
                ? $"Connection timed out to {account.SmtpHost}:{account.SmtpPort}. " +
                  "The connection is being blocked — check your firewall or network restrictions. " +
                  "If on a restricted network, configure a SOCKS5 proxy in account settings."
                : $"Cannot reach {account.SmtpHost}:{account.SmtpPort}. " +
                  "Check the hostname and port (587 for STARTTLS, 465 for SSL). " +
                  "Also verify SMTP AUTH is enabled on the mail server.";

        if (msg.Contains("SSL",         StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("certificate", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("handshake",   StringComparison.OrdinalIgnoreCase))
        {
            var inner  = ex.InnerException?.Message ?? "";
            var inner2 = ex.InnerException?.InnerException?.Message ?? "";
            var detail = string.Join(" → ", new[] { msg, inner, inner2 }.Where(s => !string.IsNullOrEmpty(s)));
            return $"SSL/TLS error connecting to {account.SmtpHost}. " +
                   $"Try switching encryption or port. Details: {detail}";
        }

        return msg;
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
