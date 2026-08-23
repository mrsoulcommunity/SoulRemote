namespace SoulRemote.Abstractions;

/// <summary>
/// Encrypts the values that must not sit in plain text on disk — the Cloudflare
/// token, the bot token and the proxy secret.
///
/// On Windows this is DPAPI, scoped to the signed-in user. Both methods report
/// success separately from the value, because "" and "it failed" are different
/// answers and the settings layer has to tell them apart: a token that merely could
/// not be decrypted this once must not be written back as an empty string, which
/// would destroy it permanently.
/// </summary>
public interface ISecretProtector
{
    bool TryProtect(string? plainText, out string cipherText);
    bool TryUnprotect(string? cipherText, out string plainText);
}

/// <summary>Passes secrets through untouched. Test-only.</summary>
public sealed class NullSecretProtector : ISecretProtector
{
    public static readonly NullSecretProtector Instance = new();

    public bool TryProtect(string? plainText, out string cipherText)
    {
        cipherText = plainText ?? string.Empty;
        return true;
    }

    public bool TryUnprotect(string? cipherText, out string plainText)
    {
        plainText = cipherText ?? string.Empty;
        return true;
    }
}
