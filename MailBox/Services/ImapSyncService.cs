using System.IO;
using MailBox.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Proxy;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace MailBox.Services;

public record SyncResult(int Folders, int Downloaded, int Skipped, int Errors, string? ErrorMessage = null);

public class ImapSyncService
{
    private readonly AccountRepository _accounts;

    public ImapSyncService(AccountRepository accounts)
    {
        _accounts = accounts;
    }

    public event Action<string>? Progress;

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<SyncResult> SyncAsync(AccountModel account, CancellationToken ct = default, Action<string>? progress = null)
    {
        void Report(string msg) { if (progress != null) progress(msg); else Progress?.Invoke(msg); }
        // First sync for this account — delete any stale database left over from a
        // previous add/delete cycle so every message is re-downloaded from scratch.
        if (account.InitialSyncDone == 0)
        {
            var dbPath = AppPaths.MailDataFile(account.Email);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }

        var repo    = new MailDataRepository(account.Email);
        var stats   = new int[4];   // [folders, downloaded, skipped, errors]
        string? firstError = null;

        ImapClient? client = null;
        try
        {
            client = await ConnectAsync(account, ct);
        }
        catch (Exception ex)
        {
            var errMsg = HintMessage(ex, account);
            _accounts.InsertLog(new MailLogModel
            {
                AccountId    = account.Id,
                AccountEmail = account.Email,
                AccountName  = account.Name,
                Type         = "connection",
                Status       = "error",
                Message      = $"IMAP connection failed: {errMsg}",
            });
            _accounts.UpdateSyncState(account.Id, account.SyncStateJson,
                account.LastSyncedAt, errMsg, account.InitialSyncDone);
            Report($"Connection failed: {errMsg}");
            return new SyncResult(0, 0, 0, 1, errMsg);
        }

        using (client)
        try
        {
            var ns = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : null;
            IList<IMailFolder> fetched;
            try   { fetched = await client.GetFoldersAsync(ns, cancellationToken: ct); }
            catch { fetched = await client.GetFoldersAsync(null, cancellationToken: ct); }

            var folders = fetched.ToList();
            if (client.Inbox != null &&
                !folders.Any(f => f.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)))
            {
                folders.Insert(0, client.Inbox);
            }

            foreach (var folder in folders)
            {
                ct.ThrowIfCancellationRequested();

                var upper = folder.FullName.ToUpper();
                if (upper.Contains("TRASH")    || upper.Contains("DELETED") ||
                    upper.Contains("DRAFT")    || upper.Contains("ARCHIVE") ||
                    upper.Contains("ALL MAIL") || upper.Contains("JUNK")    ||
                    upper.Contains("SPAM"))
                    continue;

                try
                {
                    Report($"Syncing {folder.FullName}…");
                    var (dl, sk) = await SyncFolderAsync(account, repo, client, folder, ct);
                    stats[0]++;
                    stats[1] += dl;
                    stats[2] += sk;
                }
                catch (Exception ex)
                {
                    stats[3]++;
                    firstError ??= ex.Message;
                    _accounts.InsertLog(new MailLogModel
                    {
                        AccountId    = account.Id,
                        AccountEmail = account.Email,
                        AccountName  = account.Name,
                        Type         = "receive",
                        Status       = "error",
                        Message      = $"Folder [{folder.FullName}]: {ex.Message}",
                    });
                }
            }

            try { await client.DisconnectAsync(true, ct); } catch { }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            firstError = ex.Message;
            stats[3]++;
            _accounts.InsertLog(new MailLogModel
            {
                AccountId    = account.Id,
                AccountEmail = account.Email,
                AccountName  = account.Name,
                Type         = "receive",
                Status       = "error",
                Message      = $"Sync error: {ex.Message}",
            });
        }

        _accounts.UpdateSyncState(
            account.Id,
            account.SyncStateJson,
            DateTime.UtcNow.ToString("o"),
            stats[3] > 0 ? $"{stats[3]} folder(s) had errors" : null,
            1);

        _accounts.InsertLog(new MailLogModel
        {
            AccountId    = account.Id,
            AccountEmail = account.Email,
            AccountName  = account.Name,
            Type         = "receive",
            Status       = stats[3] == 0 ? "success" : "error",
            Message      = $"Sync complete — {stats[1]} new, {stats[2]} skipped, {stats[3]} errors",
        });

        Report("Sync complete.");
        return new SyncResult(stats[0], stats[1], stats[2], stats[3], firstError);
    }

    // ── Folder sync ───────────────────────────────────────────────────────────

    private async Task<(int Downloaded, int Skipped)> SyncFolderAsync(
        AccountModel account,
        MailDataRepository repo,
        ImapClient client,
        IMailFolder folder,
        CancellationToken ct)
    {
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);

        var lastUid = account.LastSyncedUid(folder.FullName);

        UniqueIdRange range = lastUid > 0
            ? new UniqueIdRange(new UniqueId((uint)lastUid + 1), UniqueId.MaxValue)
            : new UniqueIdRange(UniqueId.MinValue, UniqueId.MaxValue);

        IList<UniqueId> newUids;
        try   { newUids = await folder.SearchAsync(SearchQuery.Uids(range), ct); }
        catch
        {
            var all = await folder.SearchAsync(SearchQuery.All, ct);
            newUids = all.Where(u => (int)u.Id > lastUid).ToList();
        }

        // IMAP "N:*" ranges always match the highest-UID message even when its UID < N,
        // so servers return the newest mail on every sync — drop already-synced UIDs.
        if (lastUid > 0)
            newUids = newUids.Where(u => (int)u.Id > lastUid).ToList();

        int maxUid = lastUid;
        int downloaded = 0, skipped = 0;

        if (newUids.Count > 0)
        {
            // One round trip to fetch ALL metadata for every new UID
            var summaries = await folder.FetchAsync(
                newUids.ToList(),
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                MessageSummaryItems.BodyStructure | MessageSummaryItems.Size |
                MessageSummaryItems.Flags,
                ct);

            foreach (var summary in summaries)
            {
                ct.ThrowIfCancellationRequested();

                var uid = summary.UniqueId;
                if ((int)uid.Id > maxUid) maxUid = (int)uid.Id;

                if (repo.ExistsByUid(folder.FullName, (int)uid.Id)) { skipped++; continue; }

                try
                {
                    await StoreFromSummaryAsync(account, repo, folder, summary, ct);
                    downloaded++;
                }
                catch (Exception ex)
                {
                    _accounts.InsertLog(new MailLogModel
                    {
                        AccountId    = account.Id,
                        AccountEmail = account.Email,
                        AccountName  = account.Name,
                        Type         = "receive",
                        Status       = "error",
                        Message      = $"Could not download uid={uid.Id} in [{folder.FullName}]: {ex.Message}",
                    });
                }
            }
        }

        // Re-fetch body content for emails that were previously stored without it
        // (e.g. initial sync on a restricted network where body downloads timed out).
        // Reuses the already-open folder connection — no extra handshake cost.
        await RefetchBodylessAsync(repo, folder, ct);

        await folder.CloseAsync(false, ct);
        if (maxUid > lastUid) account.SetSyncedUid(folder.FullName, maxUid);
        return (downloaded, skipped);
    }

    private static async Task RefetchBodylessAsync(
        MailDataRepository repo, IMailFolder folder, CancellationToken ct)
    {
        var uids = repo.GetBodylessUids(folder.FullName, limit: 50)
                       .Select(u => new UniqueId((uint)u)).ToList();
        if (uids.Count == 0) return;

        try
        {
            var summaries = await folder.FetchAsync(uids,
                MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure, ct);

            foreach (var summary in summaries)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var (html, text) = await FetchBodyPartsAsync(folder, summary, ct);
                    if (!string.IsNullOrEmpty(html) || !string.IsNullOrEmpty(text))
                        repo.UpdateBodyByUid(folder.FullName, (int)summary.UniqueId.Id, html, text);
                }
                catch { }
            }
        }
        catch { }
    }

    // Stores one email from its IMAP summary — downloads body text only, not attachment files.
    private async Task StoreFromSummaryAsync(
        AccountModel account,
        MailDataRepository repo,
        IMailFolder folder,
        IMessageSummary summary,
        CancellationToken ct)
    {
        var env  = summary.Envelope;
        var from = env?.From?.Mailboxes.FirstOrDefault();
        var toList = env != null ? string.Join(", ", env.To.Mailboxes.Select(m => m.ToString())) : "";
        var ccList = env != null ? string.Join(", ", env.Cc.Mailboxes.Select(m => m.ToString())) : "";

        // Download text body only — attachment files are fetched on demand
        string? bodyHtml = null, bodyText = null, bodyError = null;

        if (summary.HtmlBody != null)
        {
            try
            {
                var part = await folder.GetBodyPartAsync(summary.UniqueId, summary.HtmlBody, ct) as TextPart;
                bodyHtml = part?.Text;
            }
            catch (Exception ex) { bodyError = ex.Message; }
        }

        if (string.IsNullOrEmpty(bodyHtml) && summary.TextBody != null)
        {
            try
            {
                var part = await folder.GetBodyPartAsync(summary.UniqueId, summary.TextBody, ct) as TextPart;
                bodyText = part?.Text;
            }
            catch (Exception ex) { bodyError ??= ex.Message; }
        }

        // Fallback: body parts missing from BODYSTRUCTURE or their fetch failed —
        // download the whole message and let MimeKit pick the body out of the MIME tree.
        if (string.IsNullOrEmpty(bodyHtml) && string.IsNullOrEmpty(bodyText))
        {
            try
            {
                var message = await folder.GetMessageAsync(summary.UniqueId, ct);
                bodyHtml  = message.HtmlBody;
                bodyText  = message.TextBody;
                bodyError = null;
            }
            catch (Exception ex) { bodyError ??= ex.Message; }
        }

        if (bodyError != null)
        {
            _accounts.InsertLog(new MailLogModel
            {
                AccountId    = account.Id,
                AccountEmail = account.Email,
                AccountName  = account.Name,
                Type         = "receive",
                Status       = "error",
                Message      = $"Body download failed for uid={summary.UniqueId.Id} in [{folder.FullName}] — stored without body: {bodyError}",
            });
        }

        var email = new EmailModel
        {
            Folder        = folder.FullName,
            Uid           = (int)summary.UniqueId.Id,
            MessageId     = env?.MessageId,
            Subject       = env?.Subject ?? "(No Subject)",
            FromName      = from?.Name,
            FromEmail     = from?.Address,
            ToAddresses   = toList,
            CcAddresses   = string.IsNullOrEmpty(ccList) ? null : ccList,
            SentAt        = (env?.Date ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("o"),
            IsRead        = summary.Flags?.HasFlag(MessageFlags.Seen) == true ? 1 : 0,
            IsFlagged     = summary.Flags?.HasFlag(MessageFlags.Flagged) == true ? 1 : 0,
            HasAttachment = summary.Attachments.Any() ? 1 : 0,
            BodyHtml      = bodyHtml,
            BodyText      = bodyText,
            SyncedAt      = DateTime.UtcNow.ToString("o"),
        };

        var emailId = repo.InsertEmail(email);
        if (emailId == 0) return;

        // Store attachment metadata — DiskPath left empty until user opens the attachment
        foreach (var att in summary.Attachments)
        {
            if (att is not BodyPartBasic basic) continue;
            var filename = SanitizeFilename(basic.FileName ?? basic.ContentDescription ?? "attachment");
            repo.InsertAttachment(new AttachmentModel
            {
                EmailId  = emailId,
                Filename = filename,
                MimeType = basic.ContentType.MimeType,
                Size     = basic.Octets,
                DiskPath = "",
            });
        }
    }

    // Fetches just the body text/HTML for one email — much faster than GetMessageAsync
    // because it skips attachment data entirely.
    public async Task<(string? BodyHtml, string? BodyText)> FetchBodyOnDemandAsync(
        AccountModel account, string folderName, int uid, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(account, ct);
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);

        var uniqueId = new UniqueId((uint)uid);
        var summaries = await folder.FetchAsync(
            new[] { uniqueId },
            MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure, ct);

        var summary = summaries.FirstOrDefault();
        (string? html, string? text) result = (null, null);
        if (summary != null)
            result = await FetchBodyPartsAsync(folder, summary, ct);

        await folder.CloseAsync(false, ct);
        await client.DisconnectAsync(true, ct);
        return result;
    }

    // Fetches HTML then text body parts from an already-open folder — skips attachments.
    private static async Task<(string? Html, string? Text)> FetchBodyPartsAsync(
        IMailFolder folder, IMessageSummary summary, CancellationToken ct)
    {
        string? html = null, text = null;

        if (summary.HtmlBody != null)
        {
            try
            {
                var part = await folder.GetBodyPartAsync(summary.UniqueId, summary.HtmlBody, ct) as TextPart;
                html = part?.Text;
            }
            catch { }
        }
        if (string.IsNullOrEmpty(html) && summary.TextBody != null)
        {
            try
            {
                var part = await folder.GetBodyPartAsync(summary.UniqueId, summary.TextBody, ct) as TextPart;
                text = part?.Text;
            }
            catch { }
        }
        // Fallback: download full message only if body parts unavailable
        if (string.IsNullOrEmpty(html) && string.IsNullOrEmpty(text))
        {
            try
            {
                var msg = await folder.GetMessageAsync(summary.UniqueId, ct);
                html = msg.HtmlBody;
                text = msg.TextBody;
            }
            catch { }
        }
        return (html, text);
    }

    // Downloads all attachment files for one email from IMAP and saves them to disk.
    public async Task DownloadAttachmentsAsync(
        AccountModel account, EmailModel email, MailDataRepository repo, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(account, ct);
        var folder = await client.GetFolderAsync(email.Folder, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);

        var message = await folder.GetMessageAsync(new UniqueId((uint)email.Uid), ct);
        await folder.CloseAsync(false, ct);

        var storedAtts = repo.GetAttachments(email.Id);

        foreach (var part in message.Attachments.OfType<MimePart>())
        {
            var filename = SanitizeFilename(part.FileName ?? "attachment");
            var stored   = storedAtts.FirstOrDefault(a =>
                a.Filename.Equals(filename, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty(a.DiskPath));
            if (stored == null) continue;

            var dir      = AppPaths.AttachmentDir(account.Id, email.Id);
            Directory.CreateDirectory(dir);
            var fullPath = Path.Combine(dir, filename);

            try
            {
                using var fs = File.OpenWrite(fullPath);
                part.Content.DecodeTo(fs);

                var diskPath = Path.Combine("mail", account.Id.ToString(), email.Id.ToString(), filename)
                                   .Replace('\\', '/');
                repo.UpdateAttachmentPath(stored.Id, diskPath);
            }
            catch { }
        }
    }

    // ── IMAP connection ───────────────────────────────────────────────────────

    public static async Task<ImapClient> ConnectAsync(AccountModel account, CancellationToken ct = default)
    {
        var client = new ImapClient
        {
            Timeout = 120_000,  // 120 s — VPN/China connections need extra headroom
            CheckCertificateRevocation = false,
            ServerCertificateValidationCallback = (s, c, h, e) => true,
        };
        if (account.HasProxy)
            client.ProxyClient = new Socks5Client(account.ProxyHost!, account.ProxyPort);
        try
        {
            var ssl = account.ImapEncryption.ToLower() switch
            {
                "ssl" => SecureSocketOptions.SslOnConnect,
                "tls" => SecureSocketOptions.StartTls,
                _     => SecureSocketOptions.None,
            };

            await client.ConnectAsync(account.ImapHost, account.ImapPort, ssl, ct);

            var password = PasswordVault.Decrypt(account.EncryptedPassword);
            await client.AuthenticateAsync(account.Username, password, ct);

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static async Task<string> GetDraftsFolderNameAsync(AccountModel account, CancellationToken ct = default)
    {
        try
        {
            using var client = await ConnectAsync(account, ct);
            IMailFolder? folder = null;
            try { folder = client.GetFolder(SpecialFolder.Drafts); } catch { }
            if (folder == null)
            {
                var ns = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : null;
                if (ns != null)
                {
                    var list = await client.GetFoldersAsync(ns, cancellationToken: ct);
                    folder = list.FirstOrDefault(f => f.FullName.ToUpper().Contains("DRAFT"));
                }
            }
            await client.DisconnectAsync(true, ct);
            return folder?.FullName ?? "Drafts";
        }
        catch { return "Drafts"; }
    }

    public static string HintMessage(Exception ex, AccountModel account)
    {
        var msg = ex.Message;

        if (msg.Contains("AUTHENTICATIONFAILED", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Invalid credentials",  StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("535",                  StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("LOGIN failed",         StringComparison.OrdinalIgnoreCase))
        {
            if (account.ImapHost.Contains("gmail", StringComparison.OrdinalIgnoreCase))
                return "Gmail authentication failed. You must use an App Password, not your regular Gmail password. " +
                       "Go to myaccount.google.com → Security → App Passwords and create one for 'Mail'.";

            if (account.ImapHost.Contains("outlook", StringComparison.OrdinalIgnoreCase) ||
                account.ImapHost.Contains("office365", StringComparison.OrdinalIgnoreCase))
                return "Outlook/Microsoft authentication failed. " +
                       "If MFA is enabled use an App Password. " +
                       "Also ensure IMAP is turned on in Outlook settings.";

            return $"Authentication failed — wrong username or password.\n{msg}";
        }

        if (msg.Contains("Connection refused",  StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("No connection could", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("actively refused",    StringComparison.OrdinalIgnoreCase))
            return $"Connection refused at {account.ImapHost}:{account.ImapPort}. " +
                   "Check the hostname and port (993 for SSL, 143 for STARTTLS). " +
                   "Also verify IMAP is enabled on the mail server.";

        if (msg.Contains("timed out",    StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("TimedOut",     StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("did not receive a greeting", StringComparison.OrdinalIgnoreCase))
            return $"Connection timed out to {account.ImapHost}:{account.ImapPort}. " +
                   "The connection is being blocked — check your firewall or network restrictions. " +
                   "If on a restricted network, configure a SOCKS5 proxy in account settings.";

        if (msg.Contains("SSL",         StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("certificate", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("handshake",   StringComparison.OrdinalIgnoreCase))
        {
            var inner = ex.InnerException?.Message ?? "";
            var inner2 = ex.InnerException?.InnerException?.Message ?? "";
            var detail = string.Join(" → ", new[] { msg, inner, inner2 }.Where(s => !string.IsNullOrEmpty(s)));
            return $"SSL/TLS error. Make sure encryption is set to 'ssl' and port is 993. Details: {detail}";
        }

        if (msg.Contains("IMAP access is disabled", StringComparison.OrdinalIgnoreCase))
            return "IMAP is disabled on this account. For Gmail, go to Settings → See all settings → Forwarding and POP/IMAP → Enable IMAP.";

        return msg;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string SanitizeFilename(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe    = string.Concat(raw.Select(c => invalid.Contains(c) ? '_' : c));
        return string.IsNullOrWhiteSpace(safe) ? "attachment" : safe.TrimStart('.');
    }
}
