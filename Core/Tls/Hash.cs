using System;
using System.Security.Cryptography;
using System.Text;

namespace NovaCode.Core.Tls
{
    /// <summary>
    /// Алгоритм хеширования с параметрами, нужными TLS.
    /// Используются managed-реализации (SHA256Managed), а не CryptoAPI:
    /// на Windows XP без hotfix'а SHA-2 они не зависят от системы.
    /// </summary>
    public sealed class HashAlg
    {
        public static readonly HashAlg Sha1 = new HashAlg(1, 20, 64);
        public static readonly HashAlg Sha256 = new HashAlg(2, 32, 64);
        public static readonly HashAlg Sha384 = new HashAlg(3, 48, 128);
        public static readonly HashAlg Sha512 = new HashAlg(4, 64, 128);

        public readonly byte Id;
        public readonly int Length;
        public readonly int BlockSize;

        private HashAlg(byte id, int length, int blockSize)
        {
            Id = id;
            Length = length;
            BlockSize = blockSize;
        }

        public string Name
        {
            get
            {
                if (Id == 1) return "SHA1";
                if (Id == 2) return "SHA256";
                if (Id == 3) return "SHA384";
                return "SHA512";
            }
        }

        public HashAlgorithm Create()
        {
            if (Id == 1) return new SHA1Managed();
            if (Id == 2) return new SHA256Managed();
            if (Id == 3) return new SHA384Managed();
            return new SHA512Managed();
        }

        public byte[] Hash(byte[] data)
        {
            using (HashAlgorithm h = Create()) return h.ComputeHash(data);
        }

        public byte[] Hash(byte[] data, int offset, int count)
        {
            using (HashAlgorithm h = Create()) return h.ComputeHash(data, offset, count);
        }
    }

    /// <summary>HMAC своими руками: нужен для PRF, HKDF и Finished.</summary>
    public sealed class Hmac
    {
        private readonly HashAlg _alg;
        private readonly byte[] _inner;
        private readonly byte[] _outer;

        public Hmac(HashAlg alg, byte[] key)
        {
            _alg = alg;
            byte[] k = key.Length > alg.BlockSize ? alg.Hash(key) : (byte[])key.Clone();

            var pad = new byte[alg.BlockSize];
            for (int i = 0; i < k.Length; i++) pad[i] = k[i];

            _inner = new byte[alg.BlockSize];
            _outer = new byte[alg.BlockSize];
            for (int i = 0; i < pad.Length; i++)
            {
                _inner[i] = (byte)(pad[i] ^ 0x36);
                _outer[i] = (byte)(pad[i] ^ 0x5c);
            }
        }

        public byte[] Compute(byte[] data)
        {
            return Compute(data, 0, data.Length);
        }

        public byte[] Compute(byte[] data, int offset, int count)
        {
            byte[] first;
            using (HashAlgorithm h = _alg.Create())
            {
                h.TransformBlock(_inner, 0, _inner.Length, null, 0);
                h.TransformFinalBlock(data, offset, count);
                first = h.Hash;
            }

            using (HashAlgorithm h = _alg.Create())
            {
                h.TransformBlock(_outer, 0, _outer.Length, null, 0);
                h.TransformFinalBlock(first, 0, first.Length);
                return h.Hash;
            }
        }
    }

    /// <summary>HKDF (RFC 5869) в варианте TLS 1.3: Extract + Expand-Label.</summary>
    public static class Hkdf
    {
        public static byte[] Extract(HashAlg alg, byte[] salt, byte[] ikm)
        {
            if (salt == null) salt = new byte[alg.Length];
            return new Hmac(alg, salt).Compute(ikm);
        }

        public static byte[] Expand(HashAlg alg, byte[] prk, byte[] info, int length)
        {
            var result = new byte[length];
            var hmac = new Hmac(alg, prk);

            byte[] previous = new byte[0];
            int filled = 0;
            byte counter = 1;

            while (filled < length)
            {
                var block = new byte[previous.Length + info.Length + 1];
                Buffer.BlockCopy(previous, 0, block, 0, previous.Length);
                Buffer.BlockCopy(info, 0, block, previous.Length, info.Length);
                block[block.Length - 1] = counter;

                previous = hmac.Compute(block);

                int take = Math.Min(previous.Length, length - filled);
                Buffer.BlockCopy(previous, 0, result, filled, take);
                filled += take;
                counter++;
            }

            return result;
        }

        /// <summary>HKDF-Expand-Label из TLS 1.3 (RFC 8446, 7.1).</summary>
        public static byte[] ExpandLabel(HashAlg alg, byte[] secret, string label, byte[] context, int length)
        {
            byte[] full = System.Text.Encoding.ASCII.GetBytes("tls13 " + label);
            if (context == null) context = new byte[0];

            var info = new byte[2 + 1 + full.Length + 1 + context.Length];
            int p = 0;
            info[p++] = (byte)(length >> 8);
            info[p++] = (byte)(length & 0xff);
            info[p++] = (byte)full.Length;
            Buffer.BlockCopy(full, 0, info, p, full.Length);
            p += full.Length;
            info[p++] = (byte)context.Length;
            Buffer.BlockCopy(context, 0, info, p, context.Length);

            return Expand(alg, secret, info, length);
        }

        /// <summary>Derive-Secret из TLS 1.3: Expand-Label(secret, label, Transcript-Hash, Hash.length).</summary>
        public static byte[] DeriveSecret(HashAlg alg, byte[] secret, string label, byte[] transcriptHash)
        {
            return ExpandLabel(alg, secret, label, transcriptHash, alg.Length);
        }
    }

    /// <summary>Сравнение байтов без раннего выхода — для тегов и Finished.</summary>
    public static class Secret
    {
        public static bool Equal(byte[] a, int aOff, byte[] b, int bOff, int length)
        {
            int diff = 0;
            for (int i = 0; i < length; i++) diff |= a[aOff + i] ^ b[bOff + i];
            return diff == 0;
        }

        public static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            return Equal(a, 0, b, 0, a.Length);
        }

        public static byte[] Concat(byte[] first, byte[] second)
        {
            var result = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, result, 0, first.Length);
            Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
            return result;
        }

        public static byte[] Concat(byte[] a, byte[] b, byte[] c)
        {
            return Concat(Concat(a, b), c);
        }
    }

    /// <summary>Hex-строки: отпечатки сертификатов и отладочные дампы.</summary>
    public static class Hex
    {
        public static string Encode(byte[] data)
        {
            if (data == null) return "";
            var sb = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++) sb.Append(data[i].ToString("x2"));
            return sb.ToString();
        }

        public static byte[] Decode(string text)
        {
            if (text == null) return new byte[0];
            var clean = new StringBuilder(text.Length);
            foreach (char c in text)
                if (Uri.IsHexDigit(c)) clean.Append(c);

            var result = new byte[clean.Length / 2];
            for (int i = 0; i < result.Length; i++)
                result[i] = Convert.ToByte(clean.ToString(i * 2, 2), 16);

            return result;
        }
    }
}
