using System.Security.Cryptography;

namespace SoulRemote.Services.Security;

public static class SecureRandom
{
    private const string AlphaNum = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
    private const string Digits = "0123456789";

    public static string Token(int length = 24) => FromAlphabet(AlphaNum, length);

    public static string NumericCode(int length = 6) => FromAlphabet(Digits, length);

    private static string FromAlphabet(string alphabet, int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }
}
