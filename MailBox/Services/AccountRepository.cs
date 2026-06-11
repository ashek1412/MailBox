using Dapper;
using MailBox.Models;
using Microsoft.Data.Sqlite;

namespace MailBox.Services;

/// <summary>
/// Manages the accounts.db SQLite file — accounts and mail_logs tables.
/// </summary>
public class AccountRepository
{
    private SqliteConnection Open() => OpenAndBoot(AppPaths.AccountsDb);

    private static SqliteConnection OpenAndBoot(string path)
    {
        var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        conn.Execute("PRAGMA journal_mode=WAL");
        conn.Execute("PRAGMA synchronous=NORMAL");
        BootSchema(conn);
        return conn;
    }

    private static void BootSchema(SqliteConnection conn)
    {
        conn.Execute(@"
            CREATE TABLE IF NOT EXISTS accounts (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                name              TEXT    NOT NULL,
                email             TEXT    NOT NULL,
                imap_host         TEXT    NOT NULL DEFAULT '',
                imap_port         INTEGER NOT NULL DEFAULT 993,
                imap_encryption   TEXT    NOT NULL DEFAULT 'ssl',
                smtp_host         TEXT    NOT NULL DEFAULT '',
                smtp_port         INTEGER NOT NULL DEFAULT 587,
                smtp_encryption   TEXT    NOT NULL DEFAULT 'tls',
                username          TEXT    NOT NULL DEFAULT '',
                encrypted_password TEXT   NOT NULL DEFAULT '',
                color             TEXT    NOT NULL DEFAULT '#1a73e8',
                sync_state_json   TEXT,
                last_synced_at    TEXT,
                sync_error        TEXT,
                initial_sync_done INTEGER NOT NULL DEFAULT 0
            )");

        conn.Execute(@"
            CREATE TABLE IF NOT EXISTS mail_logs (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                account_id    INTEGER,
                account_email TEXT,
                account_name  TEXT,
                type          TEXT NOT NULL,
                status        TEXT NOT NULL,
                message       TEXT NOT NULL,
                context       TEXT,
                created_at    TEXT NOT NULL
            )");

        conn.Execute("CREATE INDEX IF NOT EXISTS idx_logs_created ON mail_logs(created_at DESC)");
    }

    // ── Accounts ──────────────────────────────────────────────────────────────

    public List<AccountModel> GetAll()
    {
        using var conn = Open();
        return conn.Query<AccountModel>(@"
            SELECT id, name, email,
                   imap_host ImapHost, imap_port ImapPort, imap_encryption ImapEncryption,
                   smtp_host SmtpHost, smtp_port SmtpPort, smtp_encryption SmtpEncryption,
                   username, encrypted_password EncryptedPassword,
                   color, sync_state_json SyncStateJson,
                   last_synced_at LastSyncedAt, sync_error SyncError,
                   initial_sync_done InitialSyncDone
            FROM accounts ORDER BY id").ToList();
    }

    public AccountModel? GetById(int id)
    {
        using var conn = Open();
        return conn.QueryFirstOrDefault<AccountModel>(@"
            SELECT id, name, email,
                   imap_host ImapHost, imap_port ImapPort, imap_encryption ImapEncryption,
                   smtp_host SmtpHost, smtp_port SmtpPort, smtp_encryption SmtpEncryption,
                   username, encrypted_password EncryptedPassword,
                   color, sync_state_json SyncStateJson,
                   last_synced_at LastSyncedAt, sync_error SyncError,
                   initial_sync_done InitialSyncDone
            FROM accounts WHERE id = @id", new { id });
    }

    public int Insert(AccountModel a)
    {
        using var conn = Open();
        return conn.ExecuteScalar<int>(@"
            INSERT INTO accounts (name, email, imap_host, imap_port, imap_encryption,
                smtp_host, smtp_port, smtp_encryption, username, encrypted_password, color)
            VALUES (@Name, @Email, @ImapHost, @ImapPort, @ImapEncryption,
                @SmtpHost, @SmtpPort, @SmtpEncryption, @Username, @EncryptedPassword, @Color);
            SELECT last_insert_rowid()", a);
    }

    public void Update(AccountModel a)
    {
        using var conn = Open();
        conn.Execute(@"
            UPDATE accounts SET
                name              = @Name,
                email             = @Email,
                imap_host         = @ImapHost,
                imap_port         = @ImapPort,
                imap_encryption   = @ImapEncryption,
                smtp_host         = @SmtpHost,
                smtp_port         = @SmtpPort,
                smtp_encryption   = @SmtpEncryption,
                username          = @Username,
                encrypted_password= @EncryptedPassword,
                color             = @Color
            WHERE id = @Id", a);
    }

    public void UpdateSyncState(int id, string? syncStateJson, string? lastSyncedAt, string? syncError, int initialSyncDone)
    {
        using var conn = Open();
        conn.Execute(@"
            UPDATE accounts SET
                sync_state_json   = @syncStateJson,
                last_synced_at    = @lastSyncedAt,
                sync_error        = @syncError,
                initial_sync_done = @initialSyncDone
            WHERE id = @id",
            new { id, syncStateJson, lastSyncedAt, syncError, initialSyncDone });
    }

    public void Delete(int id)
    {
        using var conn = Open();
        conn.Execute("DELETE FROM accounts WHERE id = @id", new { id });
    }

    // ── Mail logs ─────────────────────────────────────────────────────────────

    public void InsertLog(MailLogModel log)
    {
        using var conn = Open();
        conn.Execute(@"
            INSERT INTO mail_logs (account_id, account_email, account_name, type, status, message, context, created_at)
            VALUES (@AccountId, @AccountEmail, @AccountName, @Type, @Status, @Message, @Context, @CreatedAt)",
            new
            {
                log.AccountId, log.AccountEmail, log.AccountName,
                log.Type, log.Status, log.Message, log.Context,
                CreatedAt = DateTime.UtcNow.ToString("o")
            });
    }

    public (List<MailLogModel> Logs, int Total) GetLogs(string typeFilter, string statusFilter, int page, int perPage)
    {
        using var conn = Open();
        var where = "WHERE 1=1";
        if (typeFilter   != "all") where += " AND type   = @typeFilter";
        if (statusFilter != "all") where += " AND status = @statusFilter";

        var total = conn.ExecuteScalar<int>($"SELECT COUNT(*) FROM mail_logs {where}",
            new { typeFilter, statusFilter });

        var logs = conn.Query<MailLogModel>($@"
            SELECT id, account_id AccountId, account_email AccountEmail, account_name AccountName,
                   type, status, message, context, created_at CreatedAt
            FROM mail_logs {where}
            ORDER BY id DESC
            LIMIT @perPage OFFSET @offset",
            new { typeFilter, statusFilter, perPage, offset = (page - 1) * perPage }).ToList();

        return (logs, total);
    }

    public void ClearLogs(string typeFilter, string statusFilter)
    {
        using var conn = Open();
        var where = "WHERE 1=1";
        if (typeFilter   != "all") where += " AND type   = @typeFilter";
        if (statusFilter != "all") where += " AND status = @statusFilter";
        conn.Execute($"DELETE FROM mail_logs {where}", new { typeFilter, statusFilter });
    }
}
