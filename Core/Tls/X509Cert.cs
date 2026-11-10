using System;
using System.Collections.Generic;
using System.Text;

namespace NovaCode.Core.Tls
{
    /// <summary>Разобранный сертификат X.509 — только то, что нужно для TLS-клиента.</summary>
    public sealed class X509Cert
    {
        public byte[] Raw;
        public byte[] TbsRaw;
        public byte[] IssuerRaw;
        public byte[] SubjectRaw;

        public DateTime NotBefore;
        public DateTime NotAfter;

        public RsaKey Key;
        public EcKey Ec;
        public bool IsEc;

        /// <summary>Есть ли пригодный для проверки подписей открытый ключ.</summary>
        public bool HasKey { get { return Key != null || Ec != null; } }
        public string KeyAlgorithm = "";

        public string SignatureAlgorithm = "";
        public HashAlg SignatureHash;
        public bool SignatureIsPss;
        public int PssSaltLength;

        public byte[] Signature;

        public List<string> DnsNames = new List<string>();
        public bool IsCa;
        public string SubjectText = "";

        public string Fingerprint
        {
            get { return Hex.Encode(HashAlg.Sha256.Hash(Raw)); }
        }

        public bool IsSelfIssued
        {
            get { return Secret.Equal(IssuerRaw, SubjectRaw); }
        }

        // ------------------------------------------------------------ разбор

        public static X509Cert Parse(byte[] der)
        {
            var cert = new X509Cert();
            cert.Raw = der;

            DerReader outer = new DerReader(der).ReadSequence();

            int tbsTag;
            cert.TbsRaw = outer.ReadAnyRaw(out tbsTag);
            if (tbsTag != 0x30) throw new FormatException("X.509: нет TBSCertificate");

            byte[] sigAlg = outer.ReadRaw(0x30);
            cert.Signature = outer.ReadBitString();

            ParseSignatureAlgorithm(cert, sigAlg);
            ParseTbs(cert);
            return cert;
        }

        private static void ParseTbs(X509Cert cert)
        {
            DerReader tbs = new DerReader(cert.TbsRaw).ReadSequence();

            if (tbs.PeekTag() == 0xa0) tbs.Skip();          // version
            tbs.Skip();                                     // serialNumber
            tbs.Skip();                                     // signature (внутренняя копия)
            cert.IssuerRaw = tbs.ReadRaw(0x30);

            DerReader validity = tbs.ReadSequence();
            cert.NotBefore = validity.ReadTime();
            cert.NotAfter = validity.ReadTime();

            cert.SubjectRaw = tbs.ReadRaw(0x30);
            cert.SubjectText = DescribeName(cert.SubjectRaw);

            DerReader spki = tbs.ReadSequence();
            DerReader algorithm = spki.ReadSequence();       // AlgorithmIdentifier
            cert.KeyAlgorithm = algorithm.ReadOid();

            string keyCurve = "";
            if (algorithm.HasData && algorithm.PeekTag() == 0x06) keyCurve = algorithm.ReadOid();

            byte[] keyBits = spki.ReadBitString();
            ParsePublicKey(cert, keyBits, keyCurve);

            while (tbs.HasData)
            {
                int tag = tbs.PeekTag();
                if (tag == 0xa3)
                {
                    var wrapper = new DerReader(tbs.ReadValue(0xa3));
                    ParseExtensions(cert, wrapper.ReadSequence());
                }
                else
                {
                    tbs.Skip();
                }
            }
        }

        private static void ParsePublicKey(X509Cert cert, byte[] bits, string curveOid)
        {
            if (cert.KeyAlgorithm == "1.2.840.113549.1.1.1")
            {
                DerReader key = new DerReader(bits).ReadSequence();
                var rsa = new RsaKey();
                rsa.Modulus = new System.Numerics.BigInteger(Rsa.Positive(key.ReadInteger()));
                rsa.Exponent = new System.Numerics.BigInteger(Rsa.Positive(key.ReadInteger()));
                rsa.ModulusBytes = (rsa.ModulusBits + 7) / 8;
                cert.Key = rsa;
            }
            else if (cert.KeyAlgorithm == "1.2.840.10045.2.1")
            {
                cert.IsEc = true;
                cert.Ec = Ecdsa.ParsePoint(Ecdsa.FindCurve(curveOid), bits);
            }
            else
            {
                cert.IsEc = true;   // Ed25519 и прочее — не поддерживаем, ключ останется пустым
            }
        }

        private static void ParseExtensions(X509Cert cert, DerReader extensions)
        {
            while (extensions.HasData)
            {
                DerReader ext = extensions.ReadSequence();
                string oid = ext.ReadOid();

                if (ext.PeekTag() == 0x01) ext.Skip();       // critical
                byte[] value = ext.ReadValue(0x04);

                if (oid == "2.5.29.17") ParseSubjectAltName(cert, value);
                else if (oid == "2.5.29.19") ParseBasicConstraints(cert, value);
            }
        }

        private static void ParseSubjectAltName(X509Cert cert, byte[] value)
        {
            DerReader names = new DerReader(value).ReadSequence();
            while (names.HasData)
            {
                int tag;
                byte[] raw = names.ReadAnyRaw(out tag);
                if (tag != 0x82) continue;                   // только dNSName

                // Содержимое IA5String: 2 байта заголовка (тег + длина) в коротком виде.
                int offset = 2;
                if (raw.Length > 2 && (raw[1] & 0x80) != 0) offset = 2 + (raw[1] & 0x7f);

                cert.DnsNames.Add(Encoding.ASCII.GetString(raw, offset, raw.Length - offset));
            }
        }

        private static void ParseBasicConstraints(X509Cert cert, byte[] value)
        {
            DerReader inner = new DerReader(value).ReadSequence();
            if (inner.PeekTag() == 0x01)
            {
                byte[] flag = inner.ReadValue(0x01);
                cert.IsCa = flag.Length > 0 && flag[0] != 0;
            }
        }

        private static void ParseSignatureAlgorithm(X509Cert cert, byte[] sigAlg)
        {
            DerReader reader = new DerReader(sigAlg).ReadSequence();
            cert.SignatureAlgorithm = reader.ReadOid();

            switch (cert.SignatureAlgorithm)
            {
                case "1.2.840.113549.1.1.5": cert.SignatureHash = HashAlg.Sha1; break;
                case "1.2.840.113549.1.1.11": cert.SignatureHash = HashAlg.Sha256; break;
                case "1.2.840.113549.1.1.12": cert.SignatureHash = HashAlg.Sha384; break;
                case "1.2.840.113549.1.1.13": cert.SignatureHash = HashAlg.Sha384; break;
                case "1.2.840.10045.4.3.2": cert.SignatureHash = HashAlg.Sha256; break;   // ecdsa-with-SHA256
                case "1.2.840.10045.4.3.3": cert.SignatureHash = HashAlg.Sha384; break;   // ecdsa-with-SHA384
                case "1.2.840.10045.4.3.4": cert.SignatureHash = HashAlg.Sha512; break;   // ecdsa-with-SHA512
                case "1.3.101.112": cert.SignatureHash = HashAlg.Sha256; break;           // Ed25519 (не поддерживается)
                case "1.2.840.113549.1.1.10":
                    cert.SignatureIsPss = true;
                    cert.SignatureHash = HashAlg.Sha256;
                    cert.PssSaltLength = 32;
                    if (reader.HasData) ParsePssParameters(cert, reader.ReadRaw(0x30));
                    break;
                default:
                    cert.SignatureHash = HashAlg.Sha256;
                    break;
            }
        }

        private static void ParsePssParameters(X509Cert cert, byte[] parameters)
        {
            DerReader reader = new DerReader(parameters).ReadSequence();
            while (reader.HasData)
            {
                int tag = reader.PeekTag();
                if (tag == 0xa0)
                {
                    DerReader hashAlg = new DerReader(reader.ReadValue(0xa0)).ReadSequence();
                    string oid = hashAlg.ReadOid();
                    if (oid == "1.3.14.3.2.26") { cert.SignatureHash = HashAlg.Sha1; cert.PssSaltLength = 20; }
                    else if (oid == "2.16.840.1.101.3.4.2.1") { cert.SignatureHash = HashAlg.Sha256; cert.PssSaltLength = 32; }
                    else if (oid == "2.16.840.1.101.3.4.2.2") { cert.SignatureHash = HashAlg.Sha384; cert.PssSaltLength = 48; }
                }
                else if (tag == 0xa2)
                {
                    byte[] salt = new DerReader(reader.ReadValue(0xa2)).ReadInteger();
                    int value = 0;
                    for (int i = 0; i < salt.Length; i++) value = (value << 8) | salt[i];
                    cert.PssSaltLength = value;
                }
                else
                {
                    reader.Skip();
                }
            }
        }

        // ------------------------------------------------------------ проверки

        /// <summary>Проверяет подпись этого сертификата ключом издателя (RSA или ECDSA).</summary>
        public bool VerifyWith(X509Cert issuer)
        {
            if (issuer == null || Signature == null) return false;

            byte[] hash = SignatureHash.Hash(TbsRaw);

            if (issuer.Ec != null)
                return Ecdsa.Verify(issuer.Ec, hash, Signature);

            if (issuer.Key == null) return false;
            if (SignatureIsPss)
                return Rsa.VerifyPss(issuer.Key, SignatureHash, hash, Signature, PssSaltLength);

            return Rsa.VerifyPkcs1(issuer.Key, SignatureHash, hash, Signature);
        }

        public bool MatchesHost(string host)
        {
            host = (host ?? "").Trim().ToLowerInvariant();
            if (host.Length == 0) return false;

            foreach (string name in DnsNames)
            {
                string pattern = name.Trim().ToLowerInvariant();
                if (pattern == host) return true;

                if (pattern.StartsWith("*.", StringComparison.Ordinal))
                {
                    string suffix = pattern.Substring(1);           // ".example.com"
                    if (!host.EndsWith(suffix, StringComparison.Ordinal)) continue;

                    string label = host.Substring(0, host.Length - suffix.Length);
                    if (label.Length > 0 && label.IndexOf('.') < 0) return true;
                }
            }

            return false;
        }

        public bool IsTimeValid(DateTime now)
        {
            return now >= NotBefore && now <= NotAfter;
        }

        // ------------------------------------------------------------ имена

        private static string DescribeName(byte[] name)
        {
            var sb = new StringBuilder();
            try
            {
                DerReader reader = new DerReader(name).ReadSequence();
                while (reader.HasData)
                {
                    DerReader set = reader.ReadSet();
                    while (set.HasData)
                    {
                        DerReader pair = set.ReadSequence();
                        string oid = pair.ReadOid();
                        string text = ReadString(pair);

                        string label = oid == "2.5.4.3" ? "CN"
                                     : oid == "2.5.4.10" ? "O"
                                     : oid == "2.5.4.6" ? "C"
                                     : oid == "2.5.4.11" ? "OU"
                                     : oid;

                        if (sb.Length > 0) sb.Append(", ");
                        sb.Append(label).Append('=').Append(text);
                    }
                }
            }
            catch { }

            return sb.ToString();
        }

        private static string ReadString(DerReader reader)
        {
            int tag = reader.PeekTag();
            if (tag == 0x0c) return Encoding.UTF8.GetString(reader.ReadValue(0x0c));       // UTF8String
            if (tag == 0x13) return Encoding.ASCII.GetString(reader.ReadValue(0x13));      // PrintableString
            if (tag == 0x16) return Encoding.ASCII.GetString(reader.ReadValue(0x16));      // IA5String
            if (tag == 0x14) return Encoding.ASCII.GetString(reader.ReadValue(0x14));      // TeletexString
            if (tag == 0x1e) return Encoding.BigEndianUnicode.GetString(reader.ReadValue(0x1e)); // BMPString
            if (tag == 0x1b) return Encoding.ASCII.GetString(reader.ReadValue(0x1b));      // GeneralString

            reader.Skip();
            return "?";
        }

        internal static byte[] Reversed(byte[] bigEndian)
        {
            var result = new byte[bigEndian.Length];
            for (int i = 0; i < bigEndian.Length; i++) result[i] = bigEndian[bigEndian.Length - 1 - i];
            return result;
        }
    }
}
