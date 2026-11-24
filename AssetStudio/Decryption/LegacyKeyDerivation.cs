#nullable enable
using System;
using System.Security.Cryptography;

namespace AssetStudio
{
    public static class LegacyKeyDerivation
    {
        public static byte[] Pbkdf1Sha1(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int iterations, int cb)
        {
            if (iterations < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(iterations));
            }

            if (cb < 1 || cb > SHA1.HashSizeInBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(cb));
            }

            var input = new byte[password.Length + salt.Length];
            password.CopyTo(input);
            salt.CopyTo(input.AsSpan(password.Length));

            byte[] hash = SHA1.HashData(input);
            CryptographicOperations.ZeroMemory(input);
            for (int i = 1; i < iterations; i++)
            {
                byte[] next = SHA1.HashData(hash);
                CryptographicOperations.ZeroMemory(hash);
                hash = next;
            }

            if (cb == hash.Length)
            {
                return hash;
            }

            var result = hash.AsSpan(0, cb).ToArray();
            CryptographicOperations.ZeroMemory(hash);
            return result;
        }
    }
}
