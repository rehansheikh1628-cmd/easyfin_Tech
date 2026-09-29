using System;
using System.Security.Cryptography;
using System.Text;

namespace Accufex.Server.Services;

public interface IPasswordHasher
{
    string HashPassword(string password);
    bool VerifyHashedPassword(string hashedPassword, string providedPassword, out bool rehashNeeded);
}

public class PasswordHasher : IPasswordHasher
{
    private const int SaltSize = 16; // 128 bit
    private const int KeySize = 32;  // 256 bit
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;
    private const string Prefix = "$pbkdf2$";

    public string HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            Algorithm,
            KeySize);

        return $"{Prefix}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool VerifyHashedPassword(string hashedPassword, string providedPassword, out bool rehashNeeded)
    {
        rehashNeeded = false;
        if (string.IsNullOrWhiteSpace(hashedPassword) || string.IsNullOrWhiteSpace(providedPassword))
        {
            return false;
        }

        // Check if this is our salted PBKDF2 hash
        if (hashedPassword.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var parts = hashedPassword.Split('$');
            // Format: "" , "pbkdf2", "<iterations>", "<salt>", "<hash>"
            if (parts.Length == 5 && int.TryParse(parts[2], out int iterations))
            {
                byte[] salt;
                byte[] expectedHash;
                try
                {
                    salt = Convert.FromBase64String(parts[3]);
                    expectedHash = Convert.FromBase64String(parts[4]);
                }
                catch (FormatException)
                {
                    return false;
                }

                byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(providedPassword),
                    salt,
                    iterations,
                    Algorithm,
                    expectedHash.Length);

                bool matches = CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
                if (matches && iterations != Iterations)
                {
                    rehashNeeded = true;
                }

                return matches;
            }

            return false;
        }

        // Legacy / Seed Plaintext Password handling:
        // Support transparent login and upgrade for existing seed users (e.g. zxc@zxc.com, asd@asd.com)
        bool plaintextMatches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(hashedPassword),
            Encoding.UTF8.GetBytes(providedPassword));

        if (plaintextMatches)
        {
            // Signal that caller should update user's password to salted PBKDF2
            rehashNeeded = true;
            return true;
        }

        return false;
    }
}
