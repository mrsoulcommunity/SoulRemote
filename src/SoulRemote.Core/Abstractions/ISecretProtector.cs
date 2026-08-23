namespace SoulRemote.Abstractions;

/// <summary>
/// Encrypts the values that must not sit in plain text on disk — the Cloudflare
/// token, the bot token and the proxy secret.
///
/// On Windows this is DPAPI, scoped to the signed-in user. The interface exists so
/// the settings layer can be exercised without one; <see cref="NullSecretProtector"/>
/// is a deliberate no-op for tests, never for a shipped build.
/// </summary>
public interface ISecretProtector
{
    string Protect(string? plainText);
    string Unprotect(string? cipherText);
}

/// <summary>Passes secrets through untouched. Test-only.</summary>
public sealed class NullSecretProtector : ISecretProtector
{
    public static readonly NullSecretProtector Instance = new();

    public string Protect(string? plainText) => plainText ?? string.Empty;

    public string Unprotect(string? cipherText) => cipherText ?? string.Empty;
}
