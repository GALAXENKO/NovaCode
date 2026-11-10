using System;
using System.Security.Cryptography;

namespace NovaCode.Core.Tls
{
    /// <summary>
    /// AES-GCM (NIST SP 800-38D) поверх RijndaelManaged в режиме ECB.
    /// На Windows XP нет ни CNG, ни AesGcm, поэтому CTR и GHASH считаем сами.
    /// </summary>
    public static class AesGcm
    {
        public static void Encrypt(byte[] key, byte[] nonce, byte[] aad,
                                   byte[] plain, int plainOffset, int count,
                                   byte[] cipher, int cipherOffset,
                                   byte[] tag, int tagOffset)
        {
            byte[] h;
            byte[] j0 = Prepare(key, nonce, out h);

            Gctr(key, Increment(j0), plain, plainOffset, count, cipher, cipherOffset);
            ComputeTag(key, h, j0, aad, cipher, cipherOffset, count, tag, tagOffset);
        }

        public static bool Decrypt(byte[] key, byte[] nonce, byte[] aad,
                                   byte[] cipher, int cipherOffset, int count,
                                   byte[] tag, int tagOffset,
                                   byte[] plain, int plainOffset)
        {
            byte[] h;
            byte[] j0 = Prepare(key, nonce, out h);

            var expected = new byte[16];
            ComputeTag(key, h, j0, aad, cipher, cipherOffset, count, expected, 0);
            if (!Secret.Equal(expected, 0, tag, tagOffset, 16)) return false;

            Gctr(key, Increment(j0), cipher, cipherOffset, count, plain, plainOffset);
            return true;
        }

        // ------------------------------------------------------------ служебное

        private static RijndaelManaged NewAes(byte[] key)
        {
            var aes = new RijndaelManaged();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.BlockSize = 128;
            aes.Key = key;
            return aes;
        }

        /// <summary>H = AES(K, 0^128) и J0 = nonce || 0^31 || 1.</summary>
        private static byte[] Prepare(byte[] key, byte[] nonce, out byte[] h)
        {
            var zeros = new byte[16];
            h = new byte[16];

            using (RijndaelManaged aes = NewAes(key))
            using (ICryptoTransform enc = aes.CreateEncryptor())
                enc.TransformBlock(zeros, 0, 16, h, 0);

            var j0 = new byte[16];
            Buffer.BlockCopy(nonce, 0, j0, 0, 12);
            j0[15] = 1;
            return j0;
        }

        private static byte[] Increment(byte[] counter)
        {
            var next = (byte[])counter.Clone();
            for (int i = 15; i >= 12; i--)
            {
                next[i]++;
                if (next[i] != 0) break;
            }
            return next;
        }

        /// <summary>GCTR: поток счётчика XOR данные.</summary>
        private static void Gctr(byte[] key, byte[] icb,
                                 byte[] input, int inputOffset, int count,
                                 byte[] output, int outputOffset)
        {
            if (count <= 0) return;

            var counter = (byte[])icb.Clone();
            var stream = new byte[16];

            using (RijndaelManaged aes = NewAes(key))
            using (ICryptoTransform enc = aes.CreateEncryptor())
            {
                int done = 0;
                while (done < count)
                {
                    enc.TransformBlock(counter, 0, 16, stream, 0);

                    int take = Math.Min(16, count - done);
                    for (int i = 0; i < take; i++)
                        output[outputOffset + done + i] = (byte)(input[inputOffset + done + i] ^ stream[i]);

                    done += take;
                    counter = Increment(counter);
                }
            }
        }

        private static void ComputeTag(byte[] key, byte[] h, byte[] j0, byte[] aad,
                                       byte[] cipher, int cipherOffset, int count,
                                       byte[] tag, int tagOffset)
        {
            var ghash = new GHash(h);
            if (aad != null && aad.Length > 0) ghash.UpdatePadded(aad, 0, aad.Length);
            if (count > 0) ghash.UpdatePadded(cipher, cipherOffset, count);

            var lengths = new byte[16];
            PutU64(lengths, 0, (ulong)(aad == null ? 0 : aad.Length) * 8);
            PutU64(lengths, 8, (ulong)count * 8);
            ghash.UpdateBlock(lengths, 0);

            byte[] s = ghash.Finish();

            var mask = new byte[16];
            using (RijndaelManaged aes = NewAes(key))
            using (ICryptoTransform enc = aes.CreateEncryptor())
                enc.TransformBlock(j0, 0, 16, mask, 0);

            for (int i = 0; i < 16; i++) tag[tagOffset + i] = (byte)(s[i] ^ mask[i]);
        }

        internal static void PutU64(byte[] buffer, int offset, ulong value)
        {
            for (int i = 7; i >= 0; i--)
            {
                buffer[offset + i] = (byte)(value & 0xff);
                value >>= 8;
            }
        }

        internal static ulong GetU64(byte[] buffer, int offset)
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++) value = (value << 8) | buffer[offset + i];
            return value;
        }
    }

    /// <summary>GHASH над GF(2^128) — 128-битное умножение по модулю x^128+x^7+x^2+x+1.</summary>
    internal sealed class GHash
    {
        private readonly ulong _hHigh;
        private readonly ulong _hLow;
        private ulong _yHigh;
        private ulong _yLow;

        public GHash(byte[] h)
        {
            _hHigh = AesGcm.GetU64(h, 0);
            _hLow = AesGcm.GetU64(h, 8);
        }

        public void UpdateBlock(byte[] data, int offset)
        {
            _yHigh ^= AesGcm.GetU64(data, offset);
            _yLow ^= AesGcm.GetU64(data, offset + 8);

            ulong zHigh = 0, zLow = 0;
            ulong vHigh = _hHigh, vLow = _hLow;

            for (int i = 0; i < 128; i++)
            {
                ulong bit = i < 64 ? (_yHigh >> (63 - i)) & 1UL : (_yLow >> (63 - (i - 64))) & 1UL;
                if (bit != 0)
                {
                    zHigh ^= vHigh;
                    zLow ^= vLow;
                }

                ulong carry = vLow & 1UL;
                vLow = (vLow >> 1) | (vHigh << 63);
                vHigh >>= 1;
                if (carry != 0) vHigh ^= 0xE100000000000000UL;
            }

            _yHigh = zHigh;
            _yLow = zLow;
        }

        /// <summary>Дополняет нулями до 16 байт и подаёт блоками.</summary>
        public void UpdatePadded(byte[] data, int offset, int count)
        {
            int done = 0;
            while (count - done >= 16)
            {
                UpdateBlock(data, offset + done);
                done += 16;
            }

            if (done < count)
            {
                var tail = new byte[16];
                Buffer.BlockCopy(data, offset + done, tail, 0, count - done);
                UpdateBlock(tail, 0);
            }
        }

        public byte[] Finish()
        {
            var result = new byte[16];
            AesGcm.PutU64(result, 0, _yHigh);
            AesGcm.PutU64(result, 8, _yLow);
            return result;
        }
    }
}
