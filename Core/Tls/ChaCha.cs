using System;

namespace NovaCode.Core.Tls
{
    /// <summary>
    /// ChaCha20-Poly1305 AEAD (RFC 8439). Полностью managed, без зависимостей
    /// от системы: на старых процессорах он ещё и быстрее AES-GCM.
    /// </summary>
    public static class ChaCha20Poly1305
    {
        public static void Encrypt(byte[] key, byte[] nonce, byte[] aad,
                                   byte[] plain, int plainOffset, int count,
                                   byte[] cipher, int cipherOffset,
                                   byte[] tag, int tagOffset)
        {
            var polyKey = new byte[64];
            ChaCha20Block(key, 0, nonce, polyKey);

            ChaCha20Xor(key, 1, nonce, plain, plainOffset, count, cipher, cipherOffset);

            byte[] mac = BuildMac(polyKey, aad, cipher, cipherOffset, count);
            Buffer.BlockCopy(mac, 0, tag, tagOffset, 16);
        }

        public static bool Decrypt(byte[] key, byte[] nonce, byte[] aad,
                                   byte[] cipher, int cipherOffset, int count,
                                   byte[] tag, int tagOffset,
                                   byte[] plain, int plainOffset)
        {
            var polyKey = new byte[64];
            ChaCha20Block(key, 0, nonce, polyKey);

            byte[] mac = BuildMac(polyKey, aad, cipher, cipherOffset, count);
            if (!Secret.Equal(mac, 0, tag, tagOffset, 16)) return false;

            ChaCha20Xor(key, 1, nonce, cipher, cipherOffset, count, plain, plainOffset);
            return true;
        }

        private static byte[] BuildMac(byte[] polyKey, byte[] aad, byte[] cipher, int cipherOffset, int count)
        {
            var mac = new byte[16];
            var poly = new Poly1305(polyKey);

            if (aad != null && aad.Length > 0)
            {
                poly.Update(aad, 0, aad.Length);
                poly.Pad16();
            }

            if (count > 0)
            {
                poly.Update(cipher, cipherOffset, count);
                poly.Pad16();
            }

            var lengths = new byte[16];
            PutU64Le(lengths, 0, (ulong)(aad == null ? 0 : aad.Length));
            PutU64Le(lengths, 8, (ulong)count);
            poly.Update(lengths, 0, 16);

            poly.Finish(mac);
            return mac;
        }

        /// <summary>Little-endian 64 бита: длины в AEAD-блоке Poly1305 именно такие.</summary>
        private static void PutU64Le(byte[] buffer, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                buffer[offset + i] = (byte)(value & 0xff);
                value >>= 8;
            }
        }

        // ------------------------------------------------------------ ChaCha20

        private static void ChaCha20Xor(byte[] key, uint counter, byte[] nonce,
                                        byte[] input, int inputOffset, int count,
                                        byte[] output, int outputOffset)
        {
            var block = new byte[64];
            int done = 0;

            while (done < count)
            {
                ChaCha20Block(key, counter, nonce, block);
                counter++;

                int take = Math.Min(64, count - done);
                for (int i = 0; i < take; i++)
                    output[outputOffset + done + i] = (byte)(input[inputOffset + done + i] ^ block[i]);

                done += take;
            }
        }

        internal static void ChaCha20Block(byte[] key, uint counter, byte[] nonce, byte[] output)
        {
            var state = new uint[16];
            state[0] = 0x61707865;
            state[1] = 0x3320646e;
            state[2] = 0x79622d32;
            state[3] = 0x6b206574;

            for (int i = 0; i < 8; i++) state[4 + i] = GetU32(key, i * 4);
            state[12] = counter;
            state[13] = GetU32(nonce, 0);
            state[14] = GetU32(nonce, 4);
            state[15] = GetU32(nonce, 8);

            uint[] x = (uint[])state.Clone();

            for (int round = 0; round < 10; round++)
            {
                Quarter(ref x[0], ref x[4], ref x[8], ref x[12]);
                Quarter(ref x[1], ref x[5], ref x[9], ref x[13]);
                Quarter(ref x[2], ref x[6], ref x[10], ref x[14]);
                Quarter(ref x[3], ref x[7], ref x[11], ref x[15]);

                Quarter(ref x[0], ref x[5], ref x[10], ref x[15]);
                Quarter(ref x[1], ref x[6], ref x[11], ref x[12]);
                Quarter(ref x[2], ref x[7], ref x[8], ref x[13]);
                Quarter(ref x[3], ref x[4], ref x[9], ref x[14]);
            }

            for (int i = 0; i < 16; i++)
                PutU32(output, i * 4, x[i] + state[i]);
        }

        private static void Quarter(ref uint a, ref uint b, ref uint c, ref uint d)
        {
            a += b; d ^= a; d = Rotate(d, 16);
            c += d; b ^= c; b = Rotate(b, 12);
            a += b; d ^= a; d = Rotate(d, 8);
            c += d; b ^= c; b = Rotate(b, 7);
        }

        private static uint Rotate(uint value, int bits)
        {
            return (value << bits) | (value >> (32 - bits));
        }

        internal static uint GetU32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset] | (buffer[offset + 1] << 8) |
                          (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));
        }

        internal static void PutU32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }
    }

    /// <summary>Poly1305 на 5 ветвях по 26 бит (poly1305-donna, 32-битный вариант).</summary>
    internal sealed class Poly1305
    {
        private readonly uint _r0, _r1, _r2, _r3, _r4;
        private readonly uint _s1, _s2, _s3, _s4;
        private readonly uint _p0, _p1, _p2, _p3;

        private uint _h0, _h1, _h2, _h3, _h4;
        private readonly byte[] _buffer = new byte[16];
        private int _buffered;

        public Poly1305(byte[] key)
        {
            _r0 = ChaCha20Poly1305.GetU32(key, 0) & 0x3ffffff;
            _r1 = (ChaCha20Poly1305.GetU32(key, 3) >> 2) & 0x3ffff03;
            _r2 = (ChaCha20Poly1305.GetU32(key, 6) >> 4) & 0x3ffc0ff;
            _r3 = (ChaCha20Poly1305.GetU32(key, 9) >> 6) & 0x3f03fff;
            _r4 = (ChaCha20Poly1305.GetU32(key, 12) >> 8) & 0x00fffff;

            _s1 = _r1 * 5;
            _s2 = _r2 * 5;
            _s3 = _r3 * 5;
            _s4 = _r4 * 5;

            _p0 = ChaCha20Poly1305.GetU32(key, 16);
            _p1 = ChaCha20Poly1305.GetU32(key, 20);
            _p2 = ChaCha20Poly1305.GetU32(key, 24);
            _p3 = ChaCha20Poly1305.GetU32(key, 28);
        }

        public void Update(byte[] data, int offset, int count)
        {
            int done = 0;

            if (_buffered > 0)
            {
                int need = 16 - _buffered;
                int take = Math.Min(need, count);
                Buffer.BlockCopy(data, offset, _buffer, _buffered, take);
                _buffered += take;
                done += take;

                if (_buffered < 16) return;

                Block(_buffer, 0, 1u << 24);
                _buffered = 0;
            }

            while (count - done >= 16)
            {
                Block(data, offset + done, 1u << 24);
                done += 16;
            }

            if (done < count)
            {
                int rest = count - done;
                Buffer.BlockCopy(data, offset + done, _buffer, 0, rest);
                _buffered = rest;
            }
        }

        public void Pad16()
        {
            if (_buffered == 0) return;
            for (int i = _buffered; i < 16; i++) _buffer[i] = 0;
            Block(_buffer, 0, 1u << 24);
            _buffered = 0;
        }

        private void Block(byte[] data, int offset, uint highBit)
        {
            _h0 += ChaCha20Poly1305.GetU32(data, offset) & 0x3ffffff;
            _h1 += (ChaCha20Poly1305.GetU32(data, offset + 3) >> 2) & 0x3ffffff;
            _h2 += (ChaCha20Poly1305.GetU32(data, offset + 6) >> 4) & 0x3ffffff;
            _h3 += (ChaCha20Poly1305.GetU32(data, offset + 9) >> 6) & 0x3ffffff;
            _h4 += (ChaCha20Poly1305.GetU32(data, offset + 12) >> 8) | highBit;

            ulong d0 = (ulong)_h0 * _r0 + (ulong)_h1 * _s4 + (ulong)_h2 * _s3 + (ulong)_h3 * _s2 + (ulong)_h4 * _s1;
            ulong d1 = (ulong)_h0 * _r1 + (ulong)_h1 * _r0 + (ulong)_h2 * _s4 + (ulong)_h3 * _s3 + (ulong)_h4 * _s2;
            ulong d2 = (ulong)_h0 * _r2 + (ulong)_h1 * _r1 + (ulong)_h2 * _r0 + (ulong)_h3 * _s4 + (ulong)_h4 * _s3;
            ulong d3 = (ulong)_h0 * _r3 + (ulong)_h1 * _r2 + (ulong)_h2 * _r1 + (ulong)_h3 * _r0 + (ulong)_h4 * _s4;
            ulong d4 = (ulong)_h0 * _r4 + (ulong)_h1 * _r3 + (ulong)_h2 * _r2 + (ulong)_h3 * _r1 + (ulong)_h4 * _r0;

            ulong carry = d0 >> 26; _h0 = (uint)(d0 & 0x3ffffff);
            d1 += carry; carry = d1 >> 26; _h1 = (uint)(d1 & 0x3ffffff);
            d2 += carry; carry = d2 >> 26; _h2 = (uint)(d2 & 0x3ffffff);
            d3 += carry; carry = d3 >> 26; _h3 = (uint)(d3 & 0x3ffffff);
            d4 += carry; carry = d4 >> 26; _h4 = (uint)(d4 & 0x3ffffff);
            _h0 += (uint)(carry * 5);
            carry = _h0 >> 26; _h0 &= 0x3ffffff;
            _h1 += (uint)carry;
        }

        public void Finish(byte[] tag)
        {
            if (_buffered > 0)
            {
                _buffer[_buffered] = 1;
                for (int i = _buffered + 1; i < 16; i++) _buffer[i] = 0;
                Block(_buffer, 0, 0);
                _buffered = 0;
            }

            uint carry = _h1 >> 26; _h1 &= 0x3ffffff;
            _h2 += carry; carry = _h2 >> 26; _h2 &= 0x3ffffff;
            _h3 += carry; carry = _h3 >> 26; _h3 &= 0x3ffffff;
            _h4 += carry; carry = _h4 >> 26; _h4 &= 0x3ffffff;
            _h0 += carry * 5; carry = _h0 >> 26; _h0 &= 0x3ffffff;
            _h1 += carry;

            uint g0 = _h0 + 5; carry = g0 >> 26; g0 &= 0x3ffffff;
            uint g1 = _h1 + carry; carry = g1 >> 26; g1 &= 0x3ffffff;
            uint g2 = _h2 + carry; carry = g2 >> 26; g2 &= 0x3ffffff;
            uint g3 = _h3 + carry; carry = g3 >> 26; g3 &= 0x3ffffff;
            uint g4 = _h4 + carry - (1u << 26);

            uint mask = (g4 >> 31) - 1u;
            g0 &= mask; g1 &= mask; g2 &= mask; g3 &= mask; g4 &= mask;
            mask = ~mask;
            _h0 = (_h0 & mask) | g0;
            _h1 = (_h1 & mask) | g1;
            _h2 = (_h2 & mask) | g2;
            _h3 = (_h3 & mask) | g3;
            _h4 = (_h4 & mask) | g4;

            ulong f0 = ((_h0) | ((ulong)_h1 << 26)) & 0xffffffffUL;
            ulong f1 = ((_h1 >> 6) | ((ulong)_h2 << 20)) & 0xffffffffUL;
            ulong f2 = ((_h2 >> 12) | ((ulong)_h3 << 14)) & 0xffffffffUL;
            ulong f3 = ((_h3 >> 18) | ((ulong)_h4 << 8)) & 0xffffffffUL;

            f0 += _p0; f1 += (f0 >> 32) + _p1; f2 += (f1 >> 32) + _p2; f3 += (f2 >> 32) + _p3;

            ChaCha20Poly1305.PutU32(tag, 0, (uint)f0);
            ChaCha20Poly1305.PutU32(tag, 4, (uint)f1);
            ChaCha20Poly1305.PutU32(tag, 8, (uint)f2);
            ChaCha20Poly1305.PutU32(tag, 12, (uint)f3);
        }
    }
}
