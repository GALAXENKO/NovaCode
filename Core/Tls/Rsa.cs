using System;
using System.Numerics;

namespace NovaCode.Core.Tls
{
    /// <summary>Открытый ключ RSA, вынутый из сертификата.</summary>
    public sealed class RsaKey
    {
        public BigInteger Modulus;
        public BigInteger Exponent;
        public int ModulusBytes;

        public int ModulusBits
        {
            get
            {
                byte[] raw = Modulus.ToByteArray();
                int last = raw.Length - 1;
                while (last > 0 && raw[last] == 0) last--;
                int bits = last * 8;
                byte top = raw[last];
                while (top != 0) { bits++; top >>= 1; }
                return bits;
            }
        }
    }

    /// <summary>
    /// Проверка подписи RSA без CryptoAPI: на XP без hotfix'а SHA-2
    /// RSACryptoServiceProvider не умеет SHA-256, а нам это нужно постоянно.
    /// </summary>
    public static class Rsa
    {
        // DigestInfo (RFC 8017, 9.2) — префиксы для PKCS#1 v1.5.
        private static readonly byte[] PrefixSha1 =
            { 0x30, 0x21, 0x30, 0x09, 0x06, 0x05, 0x2b, 0x0e, 0x03, 0x02, 0x1a, 0x05, 0x00, 0x04, 0x14 };

        private static readonly byte[] PrefixSha256 =
        {
            0x30, 0x31, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01,
            0x65, 0x03, 0x04, 0x02, 0x01, 0x05, 0x00, 0x04, 0x20
        };

        private static readonly byte[] PrefixSha384 =
        {
            0x30, 0x41, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01,
            0x65, 0x03, 0x04, 0x02, 0x02, 0x05, 0x00, 0x04, 0x30
        };

        public static bool VerifyPkcs1(RsaKey key, HashAlg alg, byte[] hash, byte[] signature)
        {
            if (signature == null || signature.Length != key.ModulusBytes) return false;

            byte[] encoded = EncodedMessage(key, signature, key.ModulusBytes);
            if (encoded == null) return false;

            if (encoded[0] != 0x00 || encoded[1] != 0x01) return false;

            int i = 2;
            while (i < encoded.Length && encoded[i] == 0xff) i++;
            if (i < 10) return false;                       // минимум 8 байт 0xFF
            if (i >= encoded.Length || encoded[i] != 0x00) return false;
            i++;

            byte[] prefix = alg.Id == 1 ? PrefixSha1 : (alg.Id == 2 ? PrefixSha256 : PrefixSha384);

            int expected = prefix.Length + hash.Length;
            if (encoded.Length - i != expected) return false;
            if (!Secret.Equal(encoded, i, prefix, 0, prefix.Length)) return false;
            return Secret.Equal(encoded, i + prefix.Length, hash, 0, hash.Length);
        }

        /// <summary>RSASSA-PSS (RFC 8017, 8.1.2). Обязателен для CertificateVerify в TLS 1.3.</summary>
        public static bool VerifyPss(RsaKey key, HashAlg alg, byte[] hash, byte[] signature, int saltLength)
        {
            int emBits = key.ModulusBits - 1;
            int emLength = (emBits + 7) / 8;
            if (signature == null || signature.Length != key.ModulusBytes) return false;

            byte[] encoded = EncodedMessage(key, signature, emLength);
            if (encoded == null) return false;

            int hashLength = alg.Length;
            if (emLength < hashLength + saltLength + 2) return false;
            if (encoded[emLength - 1] != 0xbc) return false;

            int dbLength = emLength - hashLength - 1;
            var maskedDb = new byte[dbLength];
            var h = new byte[hashLength];
            Buffer.BlockCopy(encoded, 0, maskedDb, 0, dbLength);
            Buffer.BlockCopy(encoded, dbLength, h, 0, hashLength);

            int unusedBits = 8 * emLength - emBits;
            if (unusedBits > 0 && (maskedDb[0] & (0xff << (8 - unusedBits))) != 0) return false;

            byte[] dbMask = Mgf1(alg, h, dbLength);
            var db = new byte[dbLength];
            for (int i = 0; i < dbLength; i++) db[i] = (byte)(maskedDb[i] ^ dbMask[i]);

            if (unusedBits > 0) db[0] &= (byte)(0xff >> unusedBits);

            int psLength = dbLength - saltLength - 1;
            for (int i = 0; i < psLength; i++) if (db[i] != 0) return false;
            if (db[psLength] != 0x01) return false;

            var salt = new byte[saltLength];
            Buffer.BlockCopy(db, psLength + 1, salt, 0, saltLength);

            var message = new byte[8 + hashLength + saltLength];
            Buffer.BlockCopy(hash, 0, message, 8, hashLength);
            Buffer.BlockCopy(salt, 0, message, 8 + hashLength, saltLength);

            byte[] expected = alg.Hash(message);
            return Secret.Equal(expected, h);
        }

        private static byte[] EncodedMessage(RsaKey key, byte[] signature, int outputLength)
        {
            var s = new BigInteger(Positive(signature));
            if (s.Sign < 0 || s >= key.Modulus) return null;

            BigInteger m = BigInteger.ModPow(s, key.Exponent, key.Modulus);
            byte[] raw = m.ToByteArray();        // little-endian, со знаковым нулём

            var result = new byte[outputLength];
            for (int i = 0; i < raw.Length && i < outputLength; i++)
                result[outputLength - 1 - i] = raw[i];

            for (int i = outputLength; i < raw.Length; i++)
                if (raw[i] != 0) return null;    // не влезло — подпись неверна

            return result;
        }

        private static byte[] Mgf1(HashAlg alg, byte[] seed, int length)
        {
            var result = new byte[length];
            int filled = 0;
            uint counter = 0;

            while (filled < length)
            {
                var block = new byte[seed.Length + 4];
                Buffer.BlockCopy(seed, 0, block, 0, seed.Length);
                block[seed.Length] = (byte)(counter >> 24);
                block[seed.Length + 1] = (byte)(counter >> 16);
                block[seed.Length + 2] = (byte)(counter >> 8);
                block[seed.Length + 3] = (byte)counter;

                byte[] digest = alg.Hash(block);
                int take = Math.Min(digest.Length, length - filled);
                Buffer.BlockCopy(digest, 0, result, filled, take);

                filled += take;
                counter++;
            }

            return result;
        }

        /// <summary>BigInteger хочет little-endian и знаковый байт.</summary>
        internal static byte[] Positive(byte[] bigEndian)
        {
            var result = new byte[bigEndian.Length + 1];
            for (int i = 0; i < bigEndian.Length; i++) result[i] = bigEndian[bigEndian.Length - 1 - i];
            return result;
        }
    }
}
