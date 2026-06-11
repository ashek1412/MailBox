using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailBox.Models;
using MailBox.Services;

namespace MailBox.ViewModels;

public partial class AccountDialogViewModel : ObservableObject
{
    private readonly AccountRepository _accounts;
    private readonly int?              _editId;

    [ObservableProperty] private string _name           = "";
    [ObservableProperty] private string _email          = "";
    [ObservableProperty] private string _imapHost       = "";
    [ObservableProperty] private int    _imapPort       = 993;
    [ObservableProperty] private string _imapEncryption = "ssl";
    [ObservableProperty] private string _smtpHost       = "";
    [ObservableProperty] private int    _smtpPort       = 587;
    [ObservableProperty] private string _smtpEncryption = "tls";
    [ObservableProperty] private string _username       = "";
    [ObservableProperty] private string _password       = "";
    [ObservableProperty] private string _color          = "#1a73e8";
    [ObservableProperty] private string _testResult     = "";
    [ObservableProperty] private string _errorMessage   = "";
    [ObservableProperty] private bool   _isTesting      = false;

    public bool IsEdit => _editId.HasValue;
    public string Title => IsEdit ? "Edit Account" : "Add Email Account";

    // Colour swatches shown in the form
    public static readonly IReadOnlyList<string> ColorSwatches = new[]
    {
        "#1a73e8", "#e53935", "#43a047", "#fb8c00", "#8e24aa",
        "#00acc1", "#6d4c41", "#546e7a", "#f4511e", "#0b8043",
    };

    public event Action<bool>? RequestClose;

    public AccountDialogViewModel(AccountModel? existing, AccountRepository accounts)
    {
        _accounts = accounts;
        _editId   = existing?.Id;

        if (existing != null)
        {
            Name           = existing.Name;
            Email          = existing.Email;
            ImapHost       = existing.ImapHost;
            ImapPort       = existing.ImapPort;
            ImapEncryption = existing.ImapEncryption;
            SmtpHost       = existing.SmtpHost;
            SmtpPort       = existing.SmtpPort;
            SmtpEncryption = existing.SmtpEncryption;
            Username       = existing.Username;
            Color          = existing.Color;
            // Password is not shown (re-enter to change)
        }
    }

    [RelayCommand]
    private void ApplyPreset(string provider)
    {
        (ImapHost, ImapPort, ImapEncryption, SmtpHost, SmtpPort, SmtpEncryption) = provider switch
        {
            "gmail"   => ("imap.gmail.com",       993, "ssl", "smtp.gmail.com",       587, "tls"),
            "outlook" => ("outlook.office365.com", 993, "ssl", "smtp.office365.com",  587, "tls"),
            "yahoo"   => ("imap.mail.yahoo.com",   993, "ssl", "smtp.mail.yahoo.com", 587, "tls"),
            "icloud"  => ("imap.mail.me.com",      993, "ssl", "smtp.mail.me.com",    587, "tls"),
            _ => (ImapHost, ImapPort, ImapEncryption, SmtpHost, SmtpPort, SmtpEncryption),
        };
    }

    [RelayCommand]
    private async Task TestConnection()
    {
        TestResult = "Testing…";
        IsTesting  = true;
        try
        {
            var temp = new AccountModel
            {
                Name              = Name,
                Email             = Email,
                ImapHost          = ImapHost,
                ImapPort          = ImapPort,
                ImapEncryption    = ImapEncryption,
                Username          = Username,
                EncryptedPassword = PasswordVault.Encrypt(Password),
            };
            using var client = await ImapSyncService.ConnectAsync(temp);
            TestResult = "✓ Connection successful!";
        }
        catch (Exception ex)
        {
            // Build a temp model so we can get provider-specific hints
            var temp = new AccountModel { ImapHost = ImapHost };
            TestResult = "✗ " + ImapSyncService.HintMessage(ex, temp);
        }
        finally { IsTesting = false; }
    }

    [RelayCommand]
    private void Save()
    {
        ErrorMessage = "";
        if (string.IsNullOrWhiteSpace(Name))    { ErrorMessage = "Display name is required."; return; }
        if (string.IsNullOrWhiteSpace(Email))   { ErrorMessage = "Email address is required."; return; }
        if (string.IsNullOrWhiteSpace(ImapHost)){ ErrorMessage = "IMAP host is required."; return; }
        if (string.IsNullOrWhiteSpace(SmtpHost)){ ErrorMessage = "SMTP host is required."; return; }
        if (string.IsNullOrWhiteSpace(Username)){ ErrorMessage = "Username is required."; return; }
        if (!IsEdit && string.IsNullOrWhiteSpace(Password)) { ErrorMessage = "Password is required."; return; }

        var model = new AccountModel
        {
            Name           = Name,
            Email          = Email,
            ImapHost       = ImapHost,
            ImapPort       = ImapPort,
            ImapEncryption = ImapEncryption,
            SmtpHost       = SmtpHost,
            SmtpPort       = SmtpPort,
            SmtpEncryption = SmtpEncryption,
            Username       = Username,
            Color          = Color,
        };

        if (!string.IsNullOrWhiteSpace(Password))
            model.EncryptedPassword = PasswordVault.Encrypt(Password);

        if (IsEdit)
        {
            model.Id = _editId!.Value;
            if (string.IsNullOrWhiteSpace(Password))
            {
                var existing = _accounts.GetById(_editId.Value);
                model.EncryptedPassword = existing?.EncryptedPassword ?? "";
            }
            _accounts.Update(model);
        }
        else
        {
            _accounts.Insert(model);
        }

        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void SetColor(string hex) => Color = hex;

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);
}
