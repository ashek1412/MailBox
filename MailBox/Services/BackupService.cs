using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace MailBox.Services;

/// <summary>
/// Zips the entire %APPDATA%\MailBox\ data directory into a single .zip file,
/// and extracts a previously saved zip back to restore all data.
/// </summary>
public class BackupService
{
    /// Fired with (statusMessage, progress 0.0–1.0) during both operations.
    public event Action<string, double>? Progress;

    // ── Backup ────────────────────────────────────────────────────────────────

    public async Task BackupAsync(string zipPath, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

            // Manifest
            Report("Writing manifest…", 0);
            WriteManifest(zip);

            // accounts.db
            if (File.Exists(AppPaths.AccountsDb))
            {
                Report("Backing up account settings…", 0.03);
                AddFile(zip, AppPaths.AccountsDb, "accounts.db");
            }

            // maildata/*.sqlite  (one per account)
            var sqlites = Directory.Exists(AppPaths.MailDataDir)
                ? Directory.GetFiles(AppPaths.MailDataDir, "*.sqlite")
                : Array.Empty<string>();

            for (int i = 0; i < sqlites.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                Report($"Backing up mail store {i + 1}/{sqlites.Length}: {Path.GetFileName(sqlites[i])}…",
                       0.08 + 0.27 * i / Math.Max(1, sqlites.Length));
                AddFile(zip, sqlites[i], "maildata/" + Path.GetFileName(sqlites[i]));
            }

            // mail/**/* attachment files
            var attachments = Directory.Exists(AppPaths.MailDir)
                ? Directory.GetFiles(AppPaths.MailDir, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();

            for (int i = 0; i < attachments.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                // Report every 10 files to avoid flooding the UI
                if (i % 10 == 0 || i == attachments.Length - 1)
                    Report($"Backing up attachments… {i + 1}/{attachments.Length}",
                           0.35 + 0.63 * i / Math.Max(1, attachments.Length));
                var rel = Path.GetRelativePath(AppPaths.Root, attachments[i]).Replace('\\', '/');
                AddFile(zip, attachments[i], rel);
            }

            Report("Finalising…", 0.99);
        }, ct);

        Report("Backup complete ✓", 1.0);
    }

    // ── Restore ───────────────────────────────────────────────────────────────

    public async Task RestoreAsync(string zipPath, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            using var zip = ZipFile.OpenRead(zipPath);

            // Validate it's a MailBox backup
            if (zip.GetEntry("manifest.json") == null)
                throw new InvalidOperationException(
                    "This does not appear to be a valid MailBox backup.\n" +
                    "(manifest.json not found inside the zip file)");

            var entries = zip.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name) && e.Name != "manifest.json")
                // Extract accounts.db last so per-account sqlite files are in place first
                .OrderBy(e => e.Name.Equals("accounts.db", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ToList();

            for (int i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries[i];

                if (i % 5 == 0 || i == entries.Count - 1)
                    Report($"Restoring {i + 1}/{entries.Count}: {entry.Name}…",
                           (double)(i + 1) / entries.Count);

                var dest = Path.Combine(AppPaths.Root,
                    entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                // Retry up to 3 times in case file is briefly locked
                Exception? lastEx = null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        entry.ExtractToFile(dest, overwrite: true);
                        lastEx = null;
                        break;
                    }
                    catch (IOException ex)
                    {
                        lastEx = ex;
                        System.Threading.Thread.Sleep(200);
                    }
                }
                if (lastEx != null)
                    throw new IOException($"Could not restore '{entry.Name}': {lastEx.Message}", lastEx);
            }
        }, ct);

        Report("Restore complete ✓", 1.0);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void WriteManifest(ZipArchive zip)
    {
        var json = JsonSerializer.Serialize(new
        {
            version = "1.0",
            app     = "MailBox",
            created = DateTime.UtcNow.ToString("O"),
        }, new JsonSerializerOptions { WriteIndented = true });

        using var w = new StreamWriter(zip.CreateEntry("manifest.json").Open());
        w.Write(json);
    }

    /// Uses FileShare.ReadWrite so open SQLite WAL files don't cause access-denied.
    private static void AddFile(ZipArchive zip, string filePath, string entryName)
    {
        using var src = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var dst = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
        src.CopyTo(dst);
    }

    private void Report(string msg, double pct) => Progress?.Invoke(msg, pct);
}
