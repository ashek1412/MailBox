using System.Collections.Generic;

namespace MailBox.Models;

public class AccountModel
{
    public int    Id               { get; set; }
    public string Name             { get; set; } = "";
    public string Email            { get; set; } = "";
    public string ImapHost         { get; set; } = "";
    public int    ImapPort         { get; set; } = 993;
    public string ImapEncryption   { get; set; } = "ssl";   // ssl | tls | none
    public string SmtpHost         { get; set; } = "";
    public int    SmtpPort         { get; set; } = 587;
    public string SmtpEncryption   { get; set; } = "tls";
    public string Username         { get; set; } = "";
    public string EncryptedPassword{ get; set; } = "";      // DPAPI-protected, base64
    public string Color            { get; set; } = "#1a73e8";
    public string? SyncStateJson   { get; set; }            // JSON: { "INBOX": 12345, ... }
    public string? LastSyncedAt    { get; set; }
    public string? SyncError       { get; set; }
    public int    InitialSyncDone  { get; set; } = 0;
    public int    SortOrder        { get; set; } = 0;
    public string? ProxyHost       { get; set; }
    public int    ProxyPort        { get; set; } = 0;

    public bool HasProxy => !string.IsNullOrWhiteSpace(ProxyHost) && ProxyPort > 0;

    public string DisplayName => string.IsNullOrEmpty(Name) ? Email : $"{Name} <{Email}>";

    /// <summary>Returns the max UID already downloaded for a folder (0 = never synced).</summary>
    public int LastSyncedUid(string folder)
    {
        if (string.IsNullOrEmpty(SyncStateJson)) return 0;
        try
        {
            var dict = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, int>>(SyncStateJson);
            return dict != null && dict.TryGetValue(folder, out var uid) ? uid : 0;
        }
        catch { return 0; }
    }

    /// <summary>Updates the max UID for a folder in the sync-state JSON blob.</summary>
    public void SetSyncedUid(string folder, int uid)
    {
        var dict = new Dictionary<string, int>();
        if (!string.IsNullOrEmpty(SyncStateJson))
        {
            try { dict = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, int>>(SyncStateJson) ?? dict; }
            catch { /* ignore */ }
        }
        if (uid > (dict.TryGetValue(folder, out var cur) ? cur : 0))
            dict[folder] = uid;
        SyncStateJson = System.Text.Json.JsonSerializer.Serialize(dict);
    }
}
