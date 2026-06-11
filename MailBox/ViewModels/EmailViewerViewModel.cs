using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailBox.Models;
using MailBox.Services;
using System.Collections.ObjectModel;

namespace MailBox.ViewModels;

public partial class EmailViewerViewModel : ObservableObject
{
    private readonly AccountModel      _account;
    private readonly AccountRepository _accounts;
    private readonly ImapSyncService   _imap;
    private readonly SmtpSendService   _smtp;
    private readonly MainViewModel     _main;

    public  EmailModel  Email     { get; }
    private EmailModel? _fullEmail;   // loaded with body_html/body_text for reply/forward

    [ObservableProperty] private ObservableCollection<AttachmentModel> _attachments = new();
    [ObservableProperty] private string  _sanitizedHtml = "";
    [ObservableProperty] private bool    _showHtml      = true;

    public EmailViewerViewModel(
        AccountModel account, EmailModel email,
        AccountRepository accounts, ImapSyncService imap,
        SmtpSendService smtp, MainViewModel main)
    {
        _account  = account;
        _accounts = accounts;
        _imap     = imap;
        _smtp     = smtp;
        _main     = main;
        Email     = email;

        LoadFull();
    }

    private void LoadFull()
    {
        var repo  = new MailDataRepository(_account.Email);
        var full  = repo.GetByUid(Email.Folder, Email.Uid);
        _fullEmail = full;
        if (full == null) return;

        // Sanitize HTML before rendering in WebView2
        var rawHtml = full.BodyHtml ?? "";
        if (!string.IsNullOrEmpty(rawHtml))
        {
            var sanitizer = new Ganss.Xss.HtmlSanitizer();
            sanitizer.AllowedAttributes.Add("style");
            sanitizer.AllowedAttributes.Add("class");
            sanitizer.AllowedSchemes.Add("data");
            SanitizedHtml = sanitizer.Sanitize(rawHtml);
            ShowHtml = true;
        }
        else
        {
            SanitizedHtml = $"<pre style='font-family:sans-serif;white-space:pre-wrap'>{System.Net.WebUtility.HtmlEncode(full.BodyText ?? "")}</pre>";
            ShowHtml = true;
        }

        // Mark as read in local DB, then refresh sidebar counters
        if (!Email.IsReadBool)
        {
            repo.MarkAs(Email.Folder, Email.Uid, true);
            Email.IsRead = 1;
            _main.Sidebar.RefreshUnreadCountFor(_account);
        }

        // Load attachments
        Attachments.Clear();
        foreach (var att in repo.GetAttachments(full.Id))
            Attachments.Add(att);
    }

    [RelayCommand]
    private void Reply()
    {
        var all     = _accounts.GetAll();
        var compose = new ComposeViewModel(_account, all, _accounts, _smtp, ComposeMode.Reply,
            _fullEmail ?? Email, afterSendCallback: _main.SyncAll, main: _main);
        new Views.ComposeWindow(compose).Show();
    }

    [RelayCommand]
    private void Forward()
    {
        var all     = _accounts.GetAll();
        var compose = new ComposeViewModel(_account, all, _accounts, _smtp, ComposeMode.Forward,
            _fullEmail ?? Email, afterSendCallback: _main.SyncAll, main: _main);
        new Views.ComposeWindow(compose).Show();
    }

    [RelayCommand]
    private void ToggleFlag()
    {
        var repo    = new MailDataRepository(_account.Email);
        var flagged = !Email.IsFlaggedBool;
        repo.SetFlagged(Email.Folder, Email.Uid, flagged);
        Email.IsFlagged = flagged ? 1 : 0;
        OnPropertyChanged(nameof(Email));
    }

    [RelayCommand]
    private void Delete()
    {
        if (System.Windows.MessageBox.Show(
                "Delete this email?", "Delete",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
            return;

        var repo = new MailDataRepository(_account.Email);
        var full = repo.GetByUid(Email.Folder, Email.Uid);
        if (full == null) { _main.RightPanel = null; return; }

        if (Email.Folder == "Trash")
        {
            // Already in local Trash — permanently delete
            foreach (var att in repo.GetAttachments(full.Id))
            {
                var path = att.AbsolutePath(AppPaths.Root);
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }
            repo.DeleteEmail(full.Id);
        }
        else
        {
            // Move to local Trash folder
            repo.MoveToFolder(full.Id, "Trash");
        }

        // Refresh Trash badge
        _main.Sidebar.AccountItems
            .FirstOrDefault(a => a.Account.Id == _account.Id)?
            .RefreshTrashCount();

        _main.RightPanel = null;
        _main.EmailList.Reload();
        _main.Sidebar.RefreshUnreadCountFor(_account);
    }

    [RelayCommand]
    private async Task OpenAttachment(AttachmentModel att)
    {
        var path = att.AbsolutePath(AppPaths.Root);
        if (!File.Exists(path))
        {
            path = await EnsureAttachmentDownloadedAsync(att);
            if (path == null) return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task SaveAttachment(AttachmentModel att)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName   = att.Filename,
            DefaultExt = System.IO.Path.GetExtension(att.Filename),
        };
        if (dlg.ShowDialog() != true) return;

        var src = att.AbsolutePath(AppPaths.Root);
        if (!File.Exists(src))
        {
            src = await EnsureAttachmentDownloadedAsync(att);
            if (src == null) return;
        }
        File.Copy(src, dlg.FileName, overwrite: true);
    }

    // Downloads all attachments for the current email if not yet on disk.
    // Returns the absolute path for the requested attachment, or null on failure.
    private async Task<string?> EnsureAttachmentDownloadedAsync(AttachmentModel att)
    {
        if (_fullEmail == null)
        {
            System.Windows.MessageBox.Show("Cannot download attachment — email not loaded.", "Attachment");
            return null;
        }
        try
        {
            var repo = new MailDataRepository(_account.Email);
            await _imap.DownloadAttachmentsAsync(_account, _fullEmail, repo);
            LoadFull(); // refresh Attachments collection with updated DiskPaths
        }
        catch
        {
            System.Windows.MessageBox.Show("Failed to download attachment from server.", "Attachment");
            return null;
        }

        var updated = Attachments.FirstOrDefault(a =>
            a.Filename.Equals(att.Filename, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(a.DiskPath));
        if (updated == null)
        {
            System.Windows.MessageBox.Show("Attachment could not be downloaded.", "Attachment");
            return null;
        }
        return updated.AbsolutePath(AppPaths.Root);
    }
}
