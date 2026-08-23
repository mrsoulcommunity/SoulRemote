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

    public string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        catch
        {
            // If protection fails, fall back to storing nothing rather than plaintext.
            return string.Empty;
        }
    }

    public string Unprotect(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return string.Empty;
        try
        {
            var encrypted = Convert.FromBase64String(cipherText);
            var bytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }
}
