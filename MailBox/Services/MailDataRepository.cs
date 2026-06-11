using Dapper;
using MailBox.Models;
using Microsoft.Data.Sqlite;

namespace MailBox.Services;

/// <summary>
/// Manages a per-account SQLite data file — identical schema to the Laravel MailDataService.
/// File: %APPDATA%\MailBox\maildata\{email}.sqlite
/// </summary>
public class MailDataRepository
{
    private readonly string _path;

    public MailDataRepository(string accountEmail)
    {
        _path = AppPaths.MailDataFile(accountEmail);
        using var conn = Open();  // boots schema on first access
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_path};Pooling=False");
        conn.Open();
        conn.Execute("PRAGMA journal_mode=WAL");
        conn.Execute("PRAGMA synchronous=NORMAL");
        conn.Execute("PRAGMA foreign_keys=ON");
        BootSchema(conn);
        return conn;
    }

    private static void BootSchema(SqliteConnection conn)
    {
        conn.Execute(@"
            CREATE TABLE IF NOT EXISTS emails (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                folder         TEXT    NOT NULL,
                uid            INTEGER NOT NULL,
                message_id     TEXT,
                subject        TEXT,
                from_name      TEXT,
                from_email     TEXT,
                to_addresses   TEXT,
                cc_addresses   TEXT,
                sent_at        TEXT,
                is_read        INTEGER NOT NULL DEFAULT 0,
                is_flagged     INTEGER NOT NULL DEFAULT 0,
                has_attachment INTEGER NOT NULL DEFAULT 0,
                body_html      TEXT,
                body_text      TEXT,
                synced_at      TEXT    NOT NULL,
                UNIQUE (folder, uid)
            )");

        conn.Execute(@"
            CREATE TABLE IF NOT EXISTS attachments (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                email_id  INTEGER NOT NULL,
                filename  TEXT    NOT NULL,
                mime_type TEXT    NOT NULL DEFAULT 'application/octet-stream',
                size      INTEGER NOT NULL DEFAULT 0,
                disk_path TEXT    NOT NULL,
                FOREIGN KEY (email_id) REFERENCES emails(id) ON DELETE CASCADE
            )");

        conn.Execute("CREATE INDEX IF NOT EXISTS idx_folder_sent ON emails(folder, sent_at)");
        conn.Execute("CREATE INDEX IF NOT EXISTS idx_folder_uid  ON emails(folder, uid)");
        conn.Execute("CREATE INDEX IF NOT EXISTS idx_att_email   ON attachments(email_id)");
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    public (List<EmailModel> Emails, int Total) GetEmails(
        string folder, string search, int page, int perPage, bool unreadOnly = false)
    {
        using var conn = Open();
        var where = "WHERE folder = @folder";
        if (!string.IsNullOrWhiteSpace(search))
            where += " AND (subject LIKE @term OR from_email LIKE @term OR from_name LIKE @term)";
        if (unreadOnly)
            where += " AND is_read = 0";

        var term  = $"%{search}%";
        var param = new { folder, term };

        var total = conn.ExecuteScalar<int>($"SELECT COUNT(*) FROM emails {where}", param);

        var emails = conn.Query<EmailModel>($@"
            SELECT id, folder, uid, message_id MessageId, subject,
                   from_name FromName, from_email FromEmail,
                   to_addresses ToAddresses, cc_addresses CcAddresses,
                   sent_at SentAt, is_read IsRead, is_flagged IsFlagged,
                   has_attachment HasAttachment, synced_at SyncedAt
            FROM emails {where}
            ORDER BY sent_at DESC, id DESC
            LIMIT @perPage OFFSET @offset",
            new { folder, term, perPage, offset = (page - 1) * perPage }).ToList();

        return (emails, total);
    }

    public EmailModel? GetByUid(string folder, int uid)
    {
        using var conn = Open();
        return conn.QueryFirstOrDefault<EmailModel>(@"
            SELECT id, folder, uid, message_id MessageId, subject,
                   from_name FromName, from_email FromEmail,
                   to_addresses ToAddresses, cc_addresses CcAddresses,
                   sent_at SentAt, is_read IsRead, is_flagged IsFlagged,
                   has_attachment HasAttachment, body_html BodyHtml, body_text BodyText,
                   synced_at SyncedAt
            FROM emails WHERE folder = @folder AND uid = @uid",
            new { folder, uid });
    }

    public bool ExistsByUid(string folder, int uid)
    {
        using var conn = Open();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM emails WHERE folder = @folder AND uid = @uid",
            new { folder, uid }) > 0;
    }

    public int InsertEmail(EmailModel e)
    {
        using var conn = Open();
        return conn.ExecuteScalar<int>(@"
            INSERT OR IGNORE INTO emails
                (folder, uid, message_id, subject, from_name, from_email,
                 to_addresses, cc_addresses, sent_at, is_read, is_flagged,
                 has_attachment, body_html, body_text, synced_at)
            VALUES
                (@Folder, @Uid, @MessageId, @Subject, @FromName, @FromEmail,
                 @ToAddresses, @CcAddresses, @SentAt, @IsRead, @IsFlagged,
                 @HasAttachment, @BodyHtml, @BodyText, @SyncedAt);
            SELECT last_insert_rowid()", e);
    }

    public List<AttachmentModel> GetAttachments(int emailId)
    {
        using var conn = Open();
        return conn.Query<AttachmentModel>(@"
            SELECT id, email_id EmailId, filename Filename,
                   mime_type MimeType, size Size, disk_path DiskPath
            FROM attachments WHERE email_id = @emailId",
            new { emailId }).ToList();
    }

    public void InsertAttachment(AttachmentModel a)
    {
        using var conn = Open();
        conn.Execute(@"
            INSERT INTO attachments (email_id, filename, mime_type, size, disk_path)
            VALUES (@EmailId, @Filename, @MimeType, @Size, @DiskPath)", a);
    }

    public void UpdateAttachmentPath(int id, string diskPath)
    {
        using var conn = Open();
        conn.Execute("UPDATE attachments SET disk_path=@diskPath WHERE id=@id", new { id, diskPath });
    }

    public void MarkAs(string folder, int uid, bool isRead, bool? isFlagged = null)
    {
        using var conn = Open();
        if (isFlagged.HasValue)
            conn.Execute("UPDATE emails SET is_read=@r, is_flagged=@f WHERE folder=@folder AND uid=@uid",
                new { r = isRead ? 1 : 0, f = isFlagged.Value ? 1 : 0, folder, uid });
        else
            conn.Execute("UPDATE emails SET is_read=@r WHERE folder=@folder AND uid=@uid",
                new { r = isRead ? 1 : 0, folder, uid });
    }

    public void SetFlagged(string folder, int uid, bool flagged)
    {
        using var conn = Open();
        conn.Execute("UPDATE emails SET is_flagged=@f WHERE folder=@folder AND uid=@uid",
            new { f = flagged ? 1 : 0, folder, uid });
    }

    public void DeleteEmail(int emailId)
    {
        using var conn = Open();
        conn.Execute("DELETE FROM attachments WHERE email_id = @emailId", new { emailId });
        conn.Execute("DELETE FROM emails WHERE id = @emailId", new { emailId });
    }

    public void MoveToFolder(int emailId, string newFolder)
    {
        using var conn = Open();
        conn.Execute("UPDATE emails SET folder=@folder WHERE id=@emailId",
            new { folder = newFolder, emailId });
    }

    public int SaveDraft(string fromEmail, string fromName, string toAddresses,
        string ccAddresses, string subject, string bodyHtml, string folder)
    {
        using var conn = Open();
        // Use a unique negative uid so it never clashes with IMAP uids
        var minUid = conn.QueryFirstOrDefault<int?>(
            "SELECT MIN(uid) FROM emails WHERE uid < 0") ?? 0;
        var uid = minUid - 1;
        var now = DateTime.UtcNow.ToString("o");
        return conn.QueryFirst<int>(@"
            INSERT INTO emails
                (folder, uid, subject, from_name, from_email,
                 to_addresses, cc_addresses, sent_at, is_read, is_flagged,
                 has_attachment, body_html, body_text, synced_at)
            VALUES
                (@folder, @uid, @subject, @fromName, @fromEmail,
                 @toAddresses, @ccAddresses, @now, 1, 0,
                 0, @bodyHtml, '', @now);
            SELECT last_insert_rowid();",
            new { folder, uid, subject, fromName, fromEmail, toAddresses, ccAddresses, bodyHtml, now });
    }

    public int GetUnreadCount(string folder = "INBOX")
    {
        using var conn = Open();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM emails WHERE folder=@folder AND is_read=0",
            new { folder });
    }

    public int GetFolderCount(string folder)
    {
        using var conn = Open();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM emails WHERE folder=@folder", new { folder });
    }

    /// Total unread across ALL folders (for the account-level badge).
    public int GetTotalUnreadCount()
    {
        using var conn = Open();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM emails WHERE is_read=0");
    }

    /// Returns distinct sender addresses across all folders, most-frequent first.
    public List<(string Email, string Name)> GetAllFromAddresses()
    {
        using var conn = Open();
        return conn.Query<(string, string)>(@"
            SELECT from_email, from_name
            FROM emails
            WHERE from_email IS NOT NULL AND from_email != ''
            GROUP BY from_email
            ORDER BY COUNT(*) DESC")
            .ToList();
    }

    public List<(string Email, string Name)> SuggestRecipients(string query, string ownEmail, int limit = 8)
    {
        using var conn = Open();
        var term = $"%{query}%";
        return conn.Query<(string, string)>(@"
            SELECT from_email, from_name
            FROM emails
            WHERE (from_email LIKE @term OR from_name LIKE @term)
              AND from_email != '' AND from_email != @ownEmail
            GROUP BY from_email, from_name
            ORDER BY COUNT(*) DESC
            LIMIT @limit",
            new { term, ownEmail, limit }).ToList();
    }
}
