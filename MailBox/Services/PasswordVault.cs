using System.Security.Cryptography;
using System.Text;

namespace MailBox.Services;

/// <summary>
/// Encrypts/decrypts passwords using Windows DPAPI.
/// The ciphertext is tied to the current Windows user account — far safer than a shared app key.
/// </summary>
public static class PasswordVault
{
    public static string Encrypt(string plaintext)
    {
        var bytes     = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public static string Decrypt(string base64Ciphertext)
    {
        try
        {
            var encrypted = Convert.FromBase64String(base64Ciphertext);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return "";
        }
    }
}
