using System.Security.Cryptography;
using System.Text;

namespace CommunityHub.Core.Auth;

/// <summary>
/// Generates and verifies the 6-digit login PINs (CONTEXT.md section 5).
/// The plaintext PIN is returned to the caller exactly once (to be emailed)
/// and is never stored - only a salted hash goes to the database.
/// </summary>
public sealed class PinService
{
    private const int PinDigits = 6;

    /// <summary>
    /// Generate a cryptographically-random 6-digit PIN. Uses a rejection loop
    /// so every value 000000-999999 is equally likely (no modulo bias).
    /// </summary>
    public string GeneratePin()
    {
        const int max = 1_000_000; // 6 digits
        // Largest multiple of `max` that fits in uint - values above it are
        // rejected so the result is uniform.
        uint limit = uint.MaxValue - (uint.MaxValue % max);
        uint value;
        // stackalloc OUTSIDE the rejection loop (CA2014): each loop-body
        // stackalloc grows the frame and rejection can iterate, so the old
        // in-loop form was a theoretical stack overflow.
        Span<byte> bytes = stackalloc byte[4];
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = BitConverter.ToUInt32(bytes);
        }
        while (value >= limit);

        return (value % max).ToString("D" + PinDigits);
    }

    /// <summary>
    /// Hash a PIN for storage. Returns a string holding the salt and the
    /// digest together (salt:hash, base64). PBKDF2-SHA256 makes brute-forcing
    /// a 6-digit space costly even though the space is small.
    /// </summary>
    public string HashPin(string pin)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password: Encoding.UTF8.GetBytes(pin),
            salt: salt,
            iterations: 100_000,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: 32);

        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Verify a candidate PIN against a stored salt:hash. Uses a
    /// constant-time comparison so a wrong PIN cannot be found by timing.
    /// </summary>
    public bool VerifyPin(string candidatePin, string storedHash)
    {
        var parts = storedHash.Split(':', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[0]);
            expected = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
            password: Encoding.UTF8.GetBytes(candidatePin),
            salt: salt,
            iterations: 100_000,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: 32);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
