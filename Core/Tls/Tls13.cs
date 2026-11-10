using System;
using System.Collections.Generic;
using System.IO;

namespace NovaCode.Core.Tls
{
    /// <summary>
    /// Клиент TLS 1.3 поверх обычного сокета. SChannel на Windows XP умеет только
    /// TLS 1.0, поэтому протокол реализован целиком здесь: X25519 + AES-GCM или
    /// ChaCha20-Poly1305, key schedule по RFC 8446.
    /// </summary>
    public sealed partial class Tls13Connection : Stream
    {
        private readonly Stream _raw;
        private readonly TlsRecordLayer _records;
        private readonly string _host;
        private readonly bool _insecure;

        private HashAlg _hash = HashAlg.Sha256;
        private int _keyLength = 16;
        private bool _useAes = true;

        private byte[] _clientKey, _clientIv;
        private byte[] _serverKey, _serverIv;
        private ulong _clientSeq, _serverSeq;

        private readonly List<byte[]> _transcript = new List<byte[]>();
        private byte[] _hsBuffer = new byte[0];

        private byte[] _plain = new byte[0];
        private int _plainPos;
        private int _plainLength;

        private bool _closed;

        /// <summary>Согласованный протокол прикладного уровня (обычно http/1.1).</summary>
        public string NegotiatedProtocol = "";

        /// <summary>Сертификат сервера — для журнала и проверки.</summary>
        public X509Cert PeerCertificate;

        /// <summary>Вся присланная цепочка: leaf, промежуточные, иногда корень.</summary>
        public readonly List<X509Cert> PeerChain = new List<X509Cert>();

        public Tls13Connection(Stream raw, string host, bool insecure)
        {
            _raw = raw;
            _records = new TlsRecordLayer(raw);
            _host = host;
            _insecure = insecure;
        }

        // ------------------------------------------------------------ рукопожатие

        /// <summary>Полное рукопожатие TLS 1.3, включая проверку цепочки и Finished.</summary>
        public void Handshake()
        {
            byte[] clientPrivate;
            byte[] clientHello = BuildClientHello(out clientPrivate);

            AddTranscript(clientHello);
            _records.Send(22, clientHello);

            byte[] msg = ReadHandshakeMessage();
            if (msg[0] != 2)
            {
                if (msg[0] == 6)
                    throw new TlsException("Сервер потребовал HelloRetryRequest — такой сервер не поддерживается");
                throw new TlsException("Ожидался ServerHello, получен тип " + msg[0]);
            }

            byte[] serverKeyShare;
            ParseServerHello(msg, out serverKeyShare);
            AddTranscript(msg);

            byte[] shared = X25519.SharedSecret(clientPrivate, serverKeyShare);
            byte[] emptyHash = _hash.Hash(new byte[0]);
            byte[] zeros = new byte[_hash.Length];

            byte[] earlySecret = Hkdf.Extract(_hash, zeros, zeros);
            byte[] derived = Hkdf.DeriveSecret(_hash, earlySecret, "derived", emptyHash);
            byte[] handshakeSecret = Hkdf.Extract(_hash, derived, shared);

            byte[] transcript = TranscriptHash();
            byte[] clientHs = Hkdf.DeriveSecret(_hash, handshakeSecret, "c hs traffic", transcript);
            byte[] serverHs = Hkdf.DeriveSecret(_hash, handshakeSecret, "s hs traffic", transcript);

            _clientKey = Hkdf.ExpandLabel(_hash, clientHs, "key", new byte[0], _keyLength);
            _clientIv = Hkdf.ExpandLabel(_hash, clientHs, "iv", new byte[0], 12);
            _serverKey = Hkdf.ExpandLabel(_hash, serverHs, "key", new byte[0], _keyLength);
            _serverIv = Hkdf.ExpandLabel(_hash, serverHs, "iv", new byte[0], 12);
            _clientSeq = 0;
            _serverSeq = 0;

            // EncryptedExtensions
            msg = ReadHandshakeMessage();
            if (msg[0] != 8) throw new TlsException("Ожидался EncryptedExtensions, получен тип " + msg[0]);
            ParseEncryptedExtensions(msg);
            AddTranscript(msg);

            // Certificate
            msg = ReadHandshakeMessage();
            if (msg[0] != 11) throw new TlsException("Ожидался Certificate, получен тип " + msg[0]);
            List<X509Cert> chain = ParseCertificateMessage(msg);
            AddTranscript(msg);

            if (chain.Count == 0) throw new TlsException("Сервер не прислал сертификат");
            PeerCertificate = chain[0];
            PeerChain.Clear();
            PeerChain.AddRange(chain);

            string error;
            if (!_insecure && !TrustStore.Validate(_host, chain, DateTime.UtcNow, out error))
                throw new TlsException("Сертификат сервера не прошёл проверку: " + error);

            // CertificateVerify
            msg = ReadHandshakeMessage();
            if (msg[0] != 15) throw new TlsException("Ожидался CertificateVerify, получен тип " + msg[0]);
            VerifyCertificateVerify(msg, chain[0]);
            AddTranscript(msg);

            // Finished сервера
            msg = ReadHandshakeMessage();
            if (msg[0] != 20) throw new TlsException("Ожидался Finished, получен тип " + msg[0]);

            byte[] serverFinishedKey = Hkdf.ExpandLabel(_hash, serverHs, "finished", new byte[0], _hash.Length);
            byte[] expected = new Hmac(_hash, serverFinishedKey).Compute(TranscriptHash());
            if (!Secret.Equal(expected, ExtractFinished(msg)))
                throw new TlsException("Finished сервера не совпал — обмен ключами нарушен");
            AddTranscript(msg);

            // Ключи прикладных данных выводятся из транскрипта до Finished сервера.
            byte[] derived2 = Hkdf.DeriveSecret(_hash, handshakeSecret, "derived", emptyHash);
            byte[] masterSecret = Hkdf.Extract(_hash, derived2, zeros);
            byte[] appTranscript = TranscriptHash();
            byte[] clientAp = Hkdf.DeriveSecret(_hash, masterSecret, "c ap traffic", appTranscript);
            byte[] serverAp = Hkdf.DeriveSecret(_hash, masterSecret, "s ap traffic", appTranscript);

            // Finished клиента
            byte[] clientFinishedKey = Hkdf.ExpandLabel(_hash, clientHs, "finished", new byte[0], _hash.Length);
            byte[] verifyData = new Hmac(_hash, clientFinishedKey).Compute(TranscriptHash());

            byte[] finished = MakeHandshake(20, verifyData);
            AddTranscript(finished);

            SendEncrypted(22, finished, _clientKey, _clientIv, ref _clientSeq);

            // Переключаемся на ключи прикладных данных.
            _clientKey = Hkdf.ExpandLabel(_hash, clientAp, "key", new byte[0], _keyLength);
            _clientIv = Hkdf.ExpandLabel(_hash, clientAp, "iv", new byte[0], 12);
            _serverKey = Hkdf.ExpandLabel(_hash, serverAp, "key", new byte[0], _keyLength);
            _serverIv = Hkdf.ExpandLabel(_hash, serverAp, "iv", new byte[0], 12);
            _clientSeq = 0;
            _serverSeq = 0;
        }

        // ------------------------------------------------------------ ClientHello

        private byte[] BuildClientHello(out byte[] privateKey)
        {
            privateKey = RandomBytes(32);
            byte[] publicKey = X25519.PublicKey(privateKey);

            var extensions = new ByteWriter();

            // server_name
            var sni = new ByteWriter();
            sni.WriteU16(3 + _host.Length);
            sni.WriteByte(0);
            sni.WriteU16(_host.Length);
            sni.WriteAscii(_host);
            extensions.WriteU16(0x0000).WriteBlock16(sni.ToArray());

            // supported_groups: x25519
            extensions.WriteU16(0x000a).WriteBlock16(new byte[] { 0x00, 0x02, 0x00, 0x1d });

            // signature_algorithms: RSA-PSS, RSA PKCS#1 и ECDSA (для цепочек и CertificateVerify)
            var sig = new ByteWriter();
            sig.WriteU16(14);
            sig.WriteU16(0x0804);       // rsa_pss_rsae_sha256
            sig.WriteU16(0x0805);       // rsa_pss_rsae_sha384
            sig.WriteU16(0x0806);       // rsa_pss_pss_sha256
            sig.WriteU16(0x0403);       // ecdsa_secp256r1_sha256
            sig.WriteU16(0x0503);       // ecdsa_secp384r1_sha384
            sig.WriteU16(0x0401);       // rsa_pkcs1_sha256
            sig.WriteU16(0x0501);       // rsa_pkcs1_sha384
            extensions.WriteU16(0x000d).WriteBlock16(sig.ToArray());

            // ALPN: только http/1.1
            var alpn = new ByteWriter();
            alpn.WriteU16(9);
            alpn.WriteBlock8(System.Text.Encoding.ASCII.GetBytes("http/1.1"));
            extensions.WriteU16(0x0010).WriteBlock16(alpn.ToArray());

            // supported_versions: TLS 1.3
            extensions.WriteU16(0x002b).WriteBlock16(new byte[] { 0x02, 0x03, 0x04 });

            // key_share: x25519
            var keyShare = new ByteWriter();
            keyShare.WriteU16(36);
            keyShare.WriteU16(0x001d);
            keyShare.WriteBlock16(publicKey);
            extensions.WriteU16(0x0033).WriteBlock16(keyShare.ToArray());

            // psk_key_exchange_modes: psk_dhe_ke (не используем PSK, но некоторые серверы ждут)
            extensions.WriteU16(0x002d).WriteBlock16(new byte[] { 0x01, 0x01 });

            byte[] ext = extensions.ToArray();

            var hello = new ByteWriter();
            hello.WriteU16(0x0303);                       // legacy_version
            hello.WriteBytes(RandomBytes(32));            // random
            hello.WriteBlock8(RandomBytes(32));           // legacy_session_id
            hello.WriteU16(6);                            // cipher_suites
            hello.WriteU16(0x1301);                       // TLS_AES_128_GCM_SHA256
            hello.WriteU16(0x1302);                       // TLS_AES_256_GCM_SHA384
            hello.WriteU16(0x1303);                       // TLS_CHACHA20_POLY1305_SHA256
            hello.WriteBlock8(new byte[] { 0 });          // compression: ровно один null (RFC 8446)
            hello.WriteBlock16(ext);

            return MakeHandshake(1, hello.ToArray());
        }

        private void ParseServerHello(byte[] msg, out byte[] serverKeyShare)
        {
            var reader = new ByteReader(msg, 4, msg.Length - 4);

            reader.ReadU16();                             // legacy_version
            byte[] random = reader.ReadBytes(32);
            reader.ReadBlock8();                          // session_id echo
            int suite = reader.ReadU16();
            reader.ReadByte();                            // compression

            switch (suite)
            {
                case 0x1301: _useAes = true; _keyLength = 16; _hash = HashAlg.Sha256; break;
                case 0x1302: _useAes = true; _keyLength = 32; _hash = HashAlg.Sha384; break;
                case 0x1303: _useAes = false; _keyLength = 32; _hash = HashAlg.Sha256; break;
                default:
                    throw new TlsException("Сервер выбрал неподдерживаемый шифр 0x" + suite.ToString("x4"));
            }

            // HelloRetryRequest узнаём по специальному random из RFC 8446, 4.1.3.
            var hrrRandom = new byte[]
            {
                0xcf, 0x21, 0xad, 0x74, 0xe5, 0x9a, 0x61, 0x11, 0xbe, 0x1d, 0x8c, 0x02,
                0x1e, 0x65, 0xb8, 0x91, 0xc2, 0xa2, 0x11, 0x16, 0x7a, 0xbb, 0x8c, 0x5e,
                0x07, 0x9e, 0x09, 0xe2, 0xc8, 0xa8, 0x33, 0x9c
            };
            if (Secret.Equal(random, hrrRandom))
                throw new TlsException("Сервер потребовал HelloRetryRequest — такой сервер не поддерживается");

            byte[] extensions = reader.ReadBlock16();
            var ext = new ByteReader(extensions);

            serverKeyShare = null;
            bool versionOk = false;

            while (ext.Remaining > 0)
            {
                int type = ext.ReadU16();
                byte[] data = ext.ReadBlock16();

                if (type == 0x002b)
                {
                    versionOk = data.Length == 2 && data[0] == 0x03 && data[1] == 0x04;
                }
                else if (type == 0x0033)
                {
                    var share = new ByteReader(data);
                    int group = share.ReadU16();
                    serverKeyShare = share.ReadBlock16();

                    if (group != 0x001d)
                        throw new TlsException("Сервер выбрал группу 0x" + group.ToString("x4") + ", поддерживается только x25519");
                }
            }

            if (!versionOk) throw new TlsException("Сервер не подтвердил TLS 1.3");
            if (serverKeyShare == null) throw new TlsException("Сервер не прислал key_share");
        }

        private void ParseEncryptedExtensions(byte[] msg)
        {
            var reader = new ByteReader(msg, 4, msg.Length - 4);
            byte[] extensions = reader.ReadBlock16();
            var ext = new ByteReader(extensions);

            while (ext.Remaining > 0)
            {
                int type = ext.ReadU16();
                byte[] data = ext.ReadBlock16();

                if (type == 0x0010)
                {
                    var alpn = new ByteReader(data);
                    byte[] list = alpn.ReadBlock16();
                    var item = new ByteReader(list);
                    NegotiatedProtocol = System.Text.Encoding.ASCII.GetString(item.ReadBlock8());
                }
            }
        }
    }
}
