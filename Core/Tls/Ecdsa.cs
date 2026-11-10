using System;
using System.Numerics;

namespace NovaCode.Core.Tls
{
    /// <summary>Именованная кривая NIST, достаточная для проверки подписей.</summary>
    public sealed class EcCurve
    {
        public string Name = "";
        public string Oid = "";
        public BigInteger P;
        public BigInteger A;
        public BigInteger B;
        public BigInteger N;
        public BigInteger Gx;
        public BigInteger Gy;
        public int FieldBytes;

        /// <summary>Сколько байт хеша берётся в ECDSA (старшие биты).</summary>
        public int HashBytes { get { return FieldBytes; } }
    }

    /// <summary>Открытый ключ ECDSA — точка на кривой.</summary>
    public sealed class EcKey
    {
        public EcCurve Curve;
        public BigInteger X;
        public BigInteger Y;
    }

    /// <summary>
    /// Проверка подписи ECDSA на managed-коде. В net40 нет управляемой реализации,
    /// а на XP CryptoAPI не умеет современные кривые — поэтому арифметика своя,
    /// в координатах Якоби, с формулами для a = -3.
    /// </summary>
    public static class Ecdsa
    {
        public static readonly EcCurve P256 = new EcCurve
        {
            Name = "P-256",
            Oid = "1.2.840.10045.3.1.7",
            FieldBytes = 32,
            P = HexToNumber("FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF"),
            A = HexToNumber("FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFC"),
            B = HexToNumber("5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B"),
            N = HexToNumber("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"),
            Gx = HexToNumber("6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296"),
            Gy = HexToNumber("4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5")
        };

        public static readonly EcCurve P384 = new EcCurve
        {
            Name = "P-384",
            Oid = "1.3.132.0.34",
            FieldBytes = 48,
            P = HexToNumber("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFFFF0000000000000000FFFFFFFF"),
            A = HexToNumber("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFFFF0000000000000000FFFFFFFC"),
            B = HexToNumber("B3312FA7E23EE7E4988E056BE3F82D19181D9C6EFE8141120314088F5013875AC656398D8A2ED19D2A85C8EDD3EC2AEF"),
            N = HexToNumber("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFC7634D81F4372DDF581A0DB248B0A77AECEC196ACCC52973"),
            Gx = HexToNumber("AA87CA22BE8B05378EB1C71EF320AD746E1D3B628BA79B9859F741E082542A385502F25DBF55296C3A545E3872760AB7"),
            Gy = HexToNumber("3617DE4A96262C6F5D9E98BF9292DC29F8F41DBD289A147CE9DA3113B5F0B8C00A60B1CE1D7E819D7A431D7C90EA0E5F")
        };

        public static EcCurve FindCurve(string oid)
        {
            if (oid == P256.Oid) return P256;
            if (oid == P384.Oid) return P384;
            return null;
        }

        /// <summary>Разбирает точку из SPKI: 0x04 || X || Y.</summary>
        public static EcKey ParsePoint(EcCurve curve, byte[] point)
        {
            if (curve == null || point == null || point.Length == 0) return null;

            if (point[0] != 0x04)
            {
                // Сжатые точки (0x02/0x03) в сертификатах почти не встречаются,
                // а восстановление Y требует извлечения корня — не поддерживаем.
                return null;
            }

            int size = curve.FieldBytes;
            if (point.Length != 1 + 2 * size) return null;

            var x = new byte[size];
            var y = new byte[size];
            Buffer.BlockCopy(point, 1, x, 0, size);
            Buffer.BlockCopy(point, 1 + size, y, 0, size);

            return new EcKey
            {
                Curve = curve,
                X = new BigInteger(Rsa.Positive(x)),
                Y = new BigInteger(Rsa.Positive(y))
            };
        }

        // ------------------------------------------------------------ проверка

        public static bool Verify(EcKey key, byte[] hash, byte[] signature)
        {
            if (key == null || key.Curve == null || hash == null || signature == null) return false;

            EcCurve c = key.Curve;

            BigInteger r, s;
            if (!ParseSignature(signature, out r, out s)) return false;

            if (r.Sign <= 0 || s.Sign <= 0) return false;
            if (r >= c.N || s >= c.N) return false;

            BigInteger z = TruncateHash(c, hash);
            BigInteger w = BigInteger.ModPow(s, c.N - 2, c.N);

            BigInteger u1 = Mod(c.N, z * w);
            BigInteger u2 = Mod(c.N, r * w);

            Jac g = new Jac { X = c.Gx, Y = c.Gy, Z = BigInteger.One };
            Jac q = new Jac { X = key.X, Y = key.Y, Z = BigInteger.One };

            Jac sum = Add(c, Multiply(c, g, u1), Multiply(c, q, u2));

            BigInteger x, y;
            if (!ToAffine(c, sum, out x, out y)) return false;

            return Mod(c.N, x) == r;
        }

        private static bool ParseSignature(byte[] der, out BigInteger r, out BigInteger s)
        {
            r = BigInteger.Zero;
            s = BigInteger.Zero;

            try
            {
                DerReader reader = new DerReader(der).ReadSequence();
                r = new BigInteger(Rsa.Positive(reader.ReadInteger()));
                s = new BigInteger(Rsa.Positive(reader.ReadInteger()));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static BigInteger TruncateHash(EcCurve curve, byte[] hash)
        {
            int take = Math.Min(hash.Length, curve.FieldBytes);
            var slice = new byte[take];
            Buffer.BlockCopy(hash, 0, slice, 0, take);
            return new BigInteger(Rsa.Positive(slice));
        }

        // ------------------------------------------------------------ арифметика

        /// <summary>Точка в координатах Якоби; Z = 0 — бесконечно удалённая.</summary>
        private struct Jac
        {
            public BigInteger X;
            public BigInteger Y;
            public BigInteger Z;
        }

        private static BigInteger Mod(BigInteger m, BigInteger value)
        {
            BigInteger result = value % m;
            return result.Sign < 0 ? result + m : result;
        }

        private static Jac Infinity()
        {
            return new Jac { X = BigInteger.One, Y = BigInteger.One, Z = BigInteger.Zero };
        }

        private static Jac Double(EcCurve c, Jac p)
        {
            if (p.Z.IsZero || p.Y.IsZero) return Infinity();

            BigInteger delta = Mod(c.P, p.Z * p.Z);
            BigInteger gamma = Mod(c.P, p.Y * p.Y);
            BigInteger beta = Mod(c.P, p.X * gamma);
            BigInteger alpha = Mod(c.P, 3 * Mod(c.P, (p.X - delta) * (p.X + delta)));

            BigInteger x3 = Mod(c.P, alpha * alpha - 8 * beta);
            BigInteger z3 = Mod(c.P, (p.Y + p.Z) * (p.Y + p.Z) - gamma - delta);
            BigInteger y3 = Mod(c.P, alpha * (4 * beta - x3) - 8 * gamma * gamma);

            return new Jac { X = x3, Y = y3, Z = z3 };
        }

        private static Jac Add(EcCurve c, Jac p, Jac q)
        {
            if (p.Z.IsZero) return q;
            if (q.Z.IsZero) return p;

            BigInteger z1z1 = Mod(c.P, p.Z * p.Z);
            BigInteger z2z2 = Mod(c.P, q.Z * q.Z);
            BigInteger u1 = Mod(c.P, p.X * z2z2);
            BigInteger u2 = Mod(c.P, q.X * z1z1);
            BigInteger s1 = Mod(c.P, p.Y * q.Z * z2z2);
            BigInteger s2 = Mod(c.P, q.Y * p.Z * z1z1);

            BigInteger h = Mod(c.P, u2 - u1);
            BigInteger r = Mod(c.P, s2 - s1);

            if (h.IsZero)
            {
                if (r.IsZero) return Double(c, p);
                return Infinity();
            }

            BigInteger hh = Mod(c.P, h * h);
            BigInteger hhh = Mod(c.P, hh * h);
            BigInteger v = Mod(c.P, u1 * hh);

            BigInteger x3 = Mod(c.P, r * r - hhh - 2 * v);
            BigInteger y3 = Mod(c.P, r * (v - x3) - s1 * hhh);
            BigInteger z3 = Mod(c.P, p.Z * q.Z * h);

            return new Jac { X = x3, Y = y3, Z = z3 };
        }

        private static Jac Multiply(EcCurve c, Jac point, BigInteger k)
        {
            Jac result = Infinity();
            if (k.Sign <= 0) return result;

            byte[] raw = k.ToByteArray();            // little-endian
            int top = raw.Length - 1;
            while (top > 0 && raw[top] == 0) top--;

            for (int i = top; i >= 0; i--)
            {
                int limit = i == top ? HighestBit(raw[i]) : 7;

                for (int bit = limit; bit >= 0; bit--)
                {
                    result = Double(c, result);
                    if (((raw[i] >> bit) & 1) != 0) result = Add(c, result, point);
                }
            }

            return result;
        }

        private static int HighestBit(byte value)
        {
            for (int bit = 7; bit > 0; bit--)
                if ((value >> bit) != 0) return bit;
            return 0;
        }

        private static bool ToAffine(EcCurve c, Jac p, out BigInteger x, out BigInteger y)
        {
            x = BigInteger.Zero;
            y = BigInteger.Zero;
            if (p.Z.IsZero) return false;

            BigInteger inverse = BigInteger.ModPow(p.Z, c.P - 2, c.P);
            BigInteger inverse2 = Mod(c.P, inverse * inverse);

            x = Mod(c.P, p.X * inverse2);
            y = Mod(c.P, p.Y * inverse2 * inverse);
            return true;
        }

        private static BigInteger HexToNumber(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return new BigInteger(Rsa.Positive(bytes));
        }
    }
}
