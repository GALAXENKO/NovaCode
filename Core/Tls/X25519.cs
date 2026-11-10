using System;
using System.Numerics;

namespace NovaCode.Core.Tls
{
    /// <summary>
    /// X25519 (RFC 7748) на BigInteger. Нужен для ECDHE: на Windows XP нет ни CNG,
    /// ни ECC в CryptoAPI, поэтому скалярное умножение делаем сами.
    /// </summary>
    public static class X25519
    {
        private static readonly BigInteger Prime = (BigInteger.One << 255) - 19;
        private static readonly BigInteger A24 = 121665;

        public static byte[] PublicKey(byte[] scalar)
        {
            var basePoint = new byte[32];
            basePoint[0] = 9;
            return SharedSecret(scalar, basePoint);
        }

        public static byte[] SharedSecret(byte[] scalar, byte[] peerPublic)
        {
            if (scalar == null || scalar.Length != 32) throw new ArgumentException("scalar");
            if (peerPublic == null || peerPublic.Length != 32) throw new ArgumentException("peer");

            byte[] k = (byte[])scalar.Clone();
            k[0] &= 248;
            k[31] &= 127;
            k[31] |= 64;

            BigInteger x1 = Decode(peerPublic, 0);
            BigInteger x2 = BigInteger.One, z2 = BigInteger.Zero;
            BigInteger x3 = x1, z3 = BigInteger.One;

            int swap = 0;

            for (int t = 254; t >= 0; t--)
            {
                int bit = (k[t >> 3] >> (t & 7)) & 1;
                swap ^= bit;

                if (swap != 0)
                {
                    BigInteger tmp = x2; x2 = x3; x3 = tmp;
                    tmp = z2; z2 = z3; z3 = tmp;
                }
                swap = bit;

                BigInteger a = Mod(x2 + z2);
                BigInteger aa = Mod(a * a);
                BigInteger b = Mod(x2 - z2);
                BigInteger bb = Mod(b * b);
                BigInteger e = Mod(aa - bb);
                BigInteger c = Mod(x3 + z3);
                BigInteger d = Mod(x3 - z3);
                BigInteger da = Mod(d * a);
                BigInteger cb = Mod(c * b);

                x3 = Mod(Square(da + cb));
                z3 = Mod(x1 * Square(da - cb));
                x2 = Mod(aa * bb);
                z2 = Mod(e * Mod(aa + Mod(A24 * e)));
            }

            if (swap != 0)
            {
                BigInteger tmp = x2; x2 = x3; x3 = tmp;
                tmp = z2; z2 = z3; z3 = tmp;
            }

            BigInteger result = Mod(x2 * BigInteger.ModPow(z2, Prime - 2, Prime));
            return Encode(result);
        }

        private static BigInteger Mod(BigInteger value)
        {
            BigInteger r = value % Prime;
            if (r.Sign < 0) r += Prime;
            return r;
        }

        private static BigInteger Square(BigInteger value)
        {
            BigInteger v = Mod(value);
            return Mod(v * v);
        }

        private static BigInteger Decode(byte[] data, int offset)
        {
            var buffer = new byte[33];
            Buffer.BlockCopy(data, offset, buffer, 0, 32);
            buffer[31] &= 0x7f;
            buffer[32] = 0;
            return new BigInteger(buffer);
        }

        private static byte[] Encode(BigInteger value)
        {
            byte[] raw = Mod(value).ToByteArray();
            var result = new byte[32];
            Buffer.BlockCopy(raw, 0, result, 0, Math.Min(32, raw.Length));
            return result;
        }
    }
}
