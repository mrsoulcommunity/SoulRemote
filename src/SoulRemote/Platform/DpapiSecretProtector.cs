using System.Security.Cryptography;
using System.Text;
using SoulRemote.Abstractions;

namespace SoulRemote.Platform;

/// <summary>
/// Thin wrapper over the Windows Data Protection API (DPAPI). Secrets are
/// encrypted for the current user account so the stored settings file cannot be
/// read on another machine or by another user.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    public static readonly DpapiSecretProtector Instance = new();

    // Extra entropy binds ciphertext to this application.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SoulRemote::v1::dpapi");

    public bool TryProtect(string? plainText, out string cipherText)
    {
        cipherText = string.Empty;
        if (string.IsNullOrEmpty(plainText))
            return true;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            cipherText = Convert.ToBase64String(
                ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
            return true;
        }
        catch
        {
            // Reported rather than swallowed: writing "" here would replace a working
            // token with nothing, and the user would have no way to know why.
            return false;
        }
    }

    public bool TryUnprotect(string? cipherText, out string plainText)
    {
        plainText = string.Empty;
        if (string.IsNullOrEmpty(cipherText))
            return true;
        try
        {
            var encrypted = Convert.FromBase64String(cipherText);
            plainText = Encoding.UTF8.GetString(
                ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser));
            return true;
        }
        catch
        {
            // A settings file copied from another user or machine lands here, as does a
            // profile DPAPI cannot reach yet. Either way the stored value is kept.
            return false;
        }
    }
}
