using System;
using System.IO;

namespace NovaCode.Core.Tls
{
    /// <summary>Ошибка TLS: текст показывается пользователю как есть.</summary>
    public sealed class TlsException : Exception
    {
        public TlsException(string message) : base(message) { }
    }

    /// <summary>Записи TLS: 5-байтовый заголовок и полезная нагрузка.</summary>
    internal sealed class TlsRecordLayer
    {
        private readonly Stream _stream;
        private readonly byte[] _header = new byte[5];

        public TlsRecordLayer(Stream stream)
        {
            _stream = stream;
        }

        public void Send(byte type, byte[] data)
        {
            Send(type, data, 0, data.Length);
        }

        public void Send(byte type, byte[] data, int offset, int count)
        {
            _header[0] = type;
            _header[1] = 0x03;
            _header[2] = 0x03;
            _header[3] = (byte)(count >> 8);
            _header[4] = (byte)count;

            _stream.Write(_header, 0, 5);
            if (count > 0) _stream.Write(data, offset, count);
            _stream.Flush();
        }

        /// <summary>Читает одну запись целиком; тело возвращается как есть.</summary>
        public byte[] Receive(out byte type)
        {
            ReadExact(_header, 0, 5);

            type = _header[0];
            int length = (_header[3] << 8) | _header[4];
            if (length > 18432) throw new TlsException("Слишком большая TLS-запись: " + length);

            var body = new byte[length];
            if (length > 0) ReadExact(body, 0, length);
            return body;
        }

        private void ReadExact(byte[] buffer, int offset, int count)
        {
            int done = 0;
            while (done < count)
            {
                int read = _stream.Read(buffer, offset + done, count - done);
                if (read <= 0) throw new TlsException("Соединение закрыто сервером");
                done += read;
            }
        }
    }

    /// <summary>Имена alert'ов — чтобы в журнале было понятно, что случилось.</summary>
    public static class TlsAlert
    {
        public static string Describe(byte[] body)
        {
            if (body == null || body.Length < 2) return "неизвестный alert";

            int level = body[0];
            int code = body[1];

            string text;
            switch (code)
            {
                case 0: text = "close_notify"; break;
                case 10: text = "unexpected_message"; break;
                case 20: text = "bad_record_mac"; break;
                case 22: text = "record_overflow"; break;
                case 40: text = "handshake_failure"; break;
                case 42: text = "bad_certificate"; break;
                case 43: text = "unsupported_certificate"; break;
                case 44: text = "certificate_revoked"; break;
                case 45: text = "certificate_expired"; break;
                case 46: text = "certificate_unknown"; break;
                case 47: text = "illegal_parameter"; break;
                case 48: text = "unknown_ca"; break;
                case 50: text = "decode_error"; break;
                case 51: text = "decrypt_error"; break;
                case 70: text = "protocol_version"; break;
                case 71: text = "insufficient_security"; break;
                case 80: text = "internal_error"; break;
                case 109: text = "missing_extension"; break;
                case 110: text = "unsupported_extension"; break;
                case 112: text = "unrecognized_name"; break;
                case 116: text = "certificate_required"; break;
                case 120: text = "no_application_protocol"; break;
                default: text = "код " + code; break;
            }

            return text + (level == 2 ? " (fatal)" : "");
        }
    }
}
