using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace PlexBridge.Security;

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedPayload);
}

/// <summary>Encrypts Plex tokens with this install's data-protection keys.</summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    public const string Purpose = "PlexBridge.PlexServer.Token.v1";
    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        return _protector.Protect(plaintext);
    }

    public string Unprotect(string protectedPayload)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedPayload);
        return _protector.Unprotect(protectedPayload);
    }
}

public static class SecretRedactor
{
    public static string ForDisplay(string? message, string? secret, int maxLength = 240)
    {
        var text = ForLog(message, secret);
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length > maxLength)
            text = text[..maxLength].TrimEnd() + "…";
        return text;
    }

    public static string ForLog(string? text, params string?[] secrets)
    {
        var value = text ?? "";
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret) && secret.Length >= 4)
                value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }

        return value.Replace("X-Plex-Token", "token", StringComparison.OrdinalIgnoreCase);
    }
}

public static class PlexTokens
{
    public static bool IsPlausible(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        var value = token.Trim();
        if (value.Length is < 8 or > 256)
            return false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
                return false;
            if (character is '/' or '\\' or '?' or '#' or '"' or '\'' or '&' or '=' or '@')
                return false;
        }

        return true;
    }
}

public sealed class PlexBridgeException : Exception
{
    public PlexBridgeException(string message) : base(message) { }
    public PlexBridgeException(string message, Exception inner) : base(message, inner) { }
}

public static class Crypto
{
    public static bool IsProtectionFailure(Exception exception) => exception is CryptographicException;
}
