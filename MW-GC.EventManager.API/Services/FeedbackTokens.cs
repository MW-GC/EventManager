using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace MW_GC.EventManager.API.Services;

/// <summary>
/// The secrets behind the anonymous feedback link (#101). A token is 256 random bits from
/// <see cref="RandomNumberGenerator"/>, written as unpadded base64url (43 characters, URL-safe and
/// valid as a Table Storage key). It is not derived from the Event id, so it cannot be guessed from one.
/// </summary>
internal static class FeedbackTokens
{
    public const int RandomBytes = 32;
    public const int Length = 43;

    public static string Create() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RandomBytes));

    /// <summary>
    /// True for a string that could be a token: exactly <see cref="Length"/> base64url characters.
    /// Anything else is refused before any storage lookup.
    /// </summary>
    public static bool IsWellFormed(string? token) =>
        token is { Length: Length } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// The like row key for one visitor address and one Event: HMAC-SHA256 of the address keyed by the
    /// Event's token (a salted hash), as lowercase hex. The address itself is never stored.
    /// This is pseudonymisation, not anonymisation: the IPv4 space is small, so whoever holds both the
    /// token and a stored key can test every address. People behind one NAT share one key, so one like.
    /// </summary>
    public static string LikeKey(string token, string clientIp) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(clientIp)));
}
