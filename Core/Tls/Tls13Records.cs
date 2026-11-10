using System;
using System.Collections.Generic;
using System.IO;

namespace NovaCode.Core.Tls
{
    public sealed partial class Tls13Connection
    {
        // ------------------------------------------------------------ транскрипт

        private void AddTranscript(byte[] message)
        {
            _transcript.Add(message);
        }

        private byte[] TranscriptHash()
        {
            int total = 0;
            for (int i = 0; i < _transcript.Count; i++) total += _transcript[i].Length;

            var buffer = new byte[total];
            int offset = 0;
            for (int i = 0; i < _transcript.Count; i++)
            {
                Buffer.BlockCopy(_transcript[i], 0, buffer, offset, _transcript[i].Length);
                offset += _transcript[i].Length;
            }

            return _hash.Hash(buffer);
        }

        private static byte[] MakeHandshake(byte type, byte[] body)
        {
            var message = new byte[4 + body.Length];
            message[0] = type;
            message[1] = (byte)(body.Length >> 16);
            message[2] = (byte)(body.Length >> 8);
            message[3] = (byte)body.Length;
            Buffer.BlockCopy(body, 0, message, 4, body.Length);
            return message;
        }

        // ------------------------------------------------------------ чтение рукопожатия

        /// <summary>Возвращает следующее сообщение рукопожатия (вместе с 4-байтовым заголовком).</summary>
        private byte[] ReadHandshakeMessage()
        {
            while (true)
            {
                if (_hsBuffer.Length >= 4)
                {
                    int length = (_hsBuffer[1] << 16) | (_hsBuffer[2] << 8) | _hsBuffer[3];
                    if (_hsBuffer.Length >= 4 + length)
                    {
                        var message = new byte[4 + length];
                        Buffer.BlockCopy(_hsBuffer, 0, message, 0, 4 + length);

                        var rest = new byte[_hsBuffer.Length - 4 - length];
                        Buffer.BlockCopy(_hsBuffer, 4 + length, rest, 0, rest.Length);
                        _hsBuffer = rest;

                        return message;
                    }
                }

                byte outer;
                byte[] body = _records.Receive(out outer);

                if (outer == 20) continue;                      // compatibility CCS
                if (outer == 21) throw new TlsException("Сервер вернул alert: " + TlsAlert.Describe(body));

                if (outer == 23)
                {
                    if (_serverKey == null) throw new TlsException("Сервер прислал шифрозапись до обмена ключами");

                    byte inner;
                    body = DecryptRecord(body, _serverKey, _serverIv, ref _serverSeq, out inner);
                    if (inner == 21) throw new TlsException("Сервер вернул alert: " + TlsAlert.Describe(body));
                    if (inner != 22) throw new TlsException("Ожидалось сообщение рукопожатия, получен тип " + inner);
                }
                else if (outer != 22)
                {
                    throw new TlsException("Неожиданный тип записи TLS: " + outer);
                }

                _hsBuffer = Secret.Concat(_hsBuffer, body);
            }
        }

        // ------------------------------------------------------------ шифрование записей

        private void SendEncrypted(byte type, byte[] data, byte[] key, byte[] iv, ref ulong sequence)
        {
            int offset = 0;
            while (offset < data.Length || offset == 0)
            {
                int count = Math.Min(16384, data.Length - offset);

                var inner = new byte[count + 1];
                Buffer.BlockCopy(data, offset, inner, 0, count);
                inner[count] = type;

                byte[] record = EncryptBytes(inner, key, iv, sequence);
                _records.Send(23, record);

                sequence++;
                offset += count;
                if (data.Length == 0) break;
            }
        }

        private byte[] EncryptBytes(byte[] plain, byte[] key, byte[] iv, ulong sequence)
        {
            byte[] nonce = MakeNonce(iv, sequence);

            var header = new byte[5];
            header[0] = 23;
            header[1] = 0x03;
            header[2] = 0x03;
            header[3] = (byte)((plain.Length + 16) >> 8);
            header[4] = (byte)(plain.Length + 16);

            var cipher = new byte[plain.Length];
            var tag = new byte[16];

            if (_useAes)
                AesGcm.Encrypt(key, nonce, header, plain, 0, plain.Length, cipher, 0, tag, 0);
            else
                ChaCha20Poly1305.Encrypt(key, nonce, header, plain, 0, plain.Length, cipher, 0, tag, 0);

            var record = new byte[cipher.Length + 16];
            Buffer.BlockCopy(cipher, 0, record, 0, cipher.Length);
            Buffer.BlockCopy(tag, 0, record, cipher.Length, 16);
            return record;
        }

        private byte[] DecryptRecord(byte[] record, byte[] key, byte[] iv, ref ulong sequence, out byte innerType)
        {
            if (record.Length < 17) throw new TlsException("Слишком короткая шифрозапись");

            byte[] nonce = MakeNonce(iv, sequence);
            int count = record.Length - 16;

            var header = new byte[5];
            header[0] = 23;
            header[1] = 0x03;
            header[2] = 0x03;
            header[3] = (byte)(record.Length >> 8);
            header[4] = (byte)record.Length;

            var plain = new byte[count];
            bool ok;

            if (_useAes)
                ok = AesGcm.Decrypt(key, nonce, header, record, 0, count, record, count, plain, 0);
            else
                ok = ChaCha20Poly1305.Decrypt(key, nonce, header, record, 0, count, record, count, plain, 0);

            if (!ok) throw new TlsException("Не сошёлся тег записи TLS — данные повреждены");
            sequence++;

            int end = plain.Length - 1;
            while (end >= 0 && plain[end] == 0) end--;
            if (end < 0) throw new TlsException("Пустая запись TLS без типа");

            innerType = plain[end];

            var content = new byte[end];
            Buffer.BlockCopy(plain, 0, content, 0, end);
            return content;
        }

        private static byte[] MakeNonce(byte[] iv, ulong sequence)
        {
            var nonce = (byte[])iv.Clone();
            for (int i = 0; i < 8; i++)
                nonce[4 + i] ^= (byte)(sequence >> (56 - i * 8));
            return nonce;
        }

        // ------------------------------------------------------------ сертификаты

        private List<X509Cert> ParseCertificateMessage(byte[] msg)
        {
            var reader = new ByteReader(msg, 4, msg.Length - 4);
            reader.ReadBlock8();                                   // certificate_request_context
            byte[] list = reader.ReadBlock24();

            var result = new List<X509Cert>();
            var entries = new ByteReader(list);

            while (entries.Remaining > 0)
            {
                byte[] der = entries.ReadBlock24();
                entries.ReadBlock16();                             // extensions
                result.Add(X509Cert.Parse(der));
            }

            return result;
        }

        private void VerifyCertificateVerify(byte[] msg, X509Cert certificate)
        {
            var reader = new ByteReader(msg, 4, msg.Length - 4);
            int algorithm = reader.ReadU16();
            byte[] signature = reader.ReadBlock16();

            HashAlg hash;
            bool pss;
            bool ecdsa = false;

            switch (algorithm)
            {
                case 0x0804: hash = HashAlg.Sha256; pss = true; break;     // rsa_pss_rsae_sha256
                case 0x0805: hash = HashAlg.Sha384; pss = true; break;     // rsa_pss_rsae_sha384
                case 0x0806: hash = HashAlg.Sha256; pss = true; break;     // rsa_pss_pss_sha256
                case 0x0807: hash = HashAlg.Sha384; pss = true; break;     // rsa_pss_pss_sha384
                case 0x0401: hash = HashAlg.Sha256; pss = false; break;    // rsa_pkcs1_sha256
                case 0x0501: hash = HashAlg.Sha384; pss = false; break;    // rsa_pkcs1_sha384
                case 0x0403: hash = HashAlg.Sha256; pss = false; ecdsa = true; break;   // ecdsa_secp256r1_sha256
                case 0x0503: hash = HashAlg.Sha384; pss = false; ecdsa = true; break;   // ecdsa_secp384r1_sha384
                case 0x0603: hash = HashAlg.Sha512; pss = false; ecdsa = true; break;   // ecdsa_secp521r1_sha512
                default:
                    throw new TlsException("Неподдерживаемый алгоритм подписи 0x" + algorithm.ToString("x4"));
            }

            byte[] content = CertificateVerifyContent(TranscriptHash());
            byte[] digest = hash.Hash(content);
            bool ok;

            if (ecdsa)
            {
                if (certificate.Ec == null)
                    throw new TlsException("Сервер выбрал ECDSA, но ключ в сертификате не ECDSA");

                ok = Ecdsa.Verify(certificate.Ec, digest, signature);
            }
            else
            {
                if (certificate.Key == null)
                    throw new TlsException("Ключ сертификата сервера не RSA — такой сертификат не поддерживается");

                ok = pss
                    ? Rsa.VerifyPss(certificate.Key, hash, digest, signature, hash.Length)
                    : Rsa.VerifyPkcs1(certificate.Key, hash, digest, signature);
            }

            if (!ok) throw new TlsException("Подпись CertificateVerify неверна");
        }

        /// <summary>Содержимое, которое подписывает сервер в CertificateVerify (RFC 8446, 4.4.3).</summary>
        private static byte[] CertificateVerifyContent(byte[] transcriptHash)
        {
            byte[] context = System.Text.Encoding.ASCII.GetBytes("TLS 1.3, server CertificateVerify");

            var buffer = new byte[64 + context.Length + 1 + transcriptHash.Length];
            for (int i = 0; i < 64; i++) buffer[i] = 0x20;
            Buffer.BlockCopy(context, 0, buffer, 64, context.Length);
            buffer[64 + context.Length] = 0;
            Buffer.BlockCopy(transcriptHash, 0, buffer, 64 + context.Length + 1, transcriptHash.Length);
            return buffer;
        }

        private static byte[] ExtractFinished(byte[] msg)
        {
            var body = new byte[msg.Length - 4];
            Buffer.BlockCopy(msg, 4, body, 0, body.Length);
            return body;
        }

        private static byte[] RandomBytes(int count)
        {
            var buffer = new byte[count];
            using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
                rng.GetBytes(buffer);
            return buffer;
        }

        // ------------------------------------------------------------ Stream

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (_plainPos >= _plainLength)
            {
                if (_closed) return 0;

                byte outer;
                byte[] record = _records.Receive(out outer);

                if (outer == 21)
                {
                    string alert = TlsAlert.Describe(record);
                    _closed = true;
                    if (alert.StartsWith("close_notify", StringComparison.Ordinal)) return 0;
                    throw new TlsException("Сервер вернул alert: " + alert);
                }

                if (outer != 23) continue;

                byte inner;
                byte[] content = DecryptRecord(record, _serverKey, _serverIv, ref _serverSeq, out inner);

                if (inner == 22) continue;                       // NewSessionTicket и прочее
                if (inner == 21)
                {
                    _closed = true;
                    string alert = TlsAlert.Describe(content);
                    if (alert.StartsWith("close_notify", StringComparison.Ordinal)) return 0;
                    throw new TlsException("Сервер вернул alert: " + alert);
                }

                _plain = content;
                _plainPos = 0;
                _plainLength = content.Length;
            }

            int take = Math.Min(count, _plainLength - _plainPos);
            Buffer.BlockCopy(_plain, _plainPos, buffer, offset, take);
            _plainPos += take;
            return take;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count == 0) return;

            int done = 0;
            while (done < count)
            {
                int chunk = Math.Min(16384, count - done);
                var data = new byte[chunk];
                Buffer.BlockCopy(buffer, offset + done, data, 0, chunk);

                SendEncrypted(23, data, _clientKey, _clientIv, ref _clientSeq);
                done += chunk;
            }
        }

        public override void Flush()
        {
            _raw.Flush();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Close()
        {
            try
            {
                if (!_closed)
                {
                    var closeNotify = new byte[] { 1, 0 };
                    SendEncrypted(21, closeNotify, _clientKey, _clientIv, ref _clientSeq);
                    _closed = true;
                }
            }
            catch { }

            try { _raw.Close(); } catch { }
            base.Close();
        }
    }
}
