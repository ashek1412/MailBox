namespace MailBox.Models;

public class AttachmentModel
{
    public int    Id       { get; set; }
    public int    EmailId  { get; set; }
    public string Filename { get; set; } = "";
    public string MimeType { get; set; } = "application/octet-stream";
    public long   Size     { get; set; }
    public string DiskPath { get; set; } = "";

    public string FormattedSize => Size switch
    {
        < 1024             => $"{Size} B",
        < 1024 * 1024      => $"{Size / 1024.0:0.#} KB",
        _                  => $"{Size / (1024.0 * 1024):0.#} MB",
    };

    /// <summary>Absolute path on disk (built from AppData storage root).</summary>
    public string AbsolutePath(string storageRoot) =>
        System.IO.Path.Combine(storageRoot, DiskPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
}
