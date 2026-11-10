using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace NovaCode.Core.Tls
{
    /// <summary>Разобранный http(s)-адрес: без Uri, чтобы не зависеть от его причуд.</summary>
    public sealed class HttpUrl
    {
        public string Scheme = "https";
        public string Host = "";
        public int Port = 443;
        public string Path = "/";

        public bool Secure { get { return Scheme == "https"; } }

        public static HttpUrl Parse(string url)
        {
            var result = new HttpUrl();
            string text = (url ?? "").Trim();

            int schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd > 0)
            {
                result.Scheme = text.Substring(0, schemeEnd).ToLowerInvariant();
                text = text.Substring(schemeEnd + 3);
            }

            result.Port = result.Scheme == "http" ? 80 : 443;

            int slash = text.IndexOf('/');
            string authority = slash < 0 ? text : text.Substring(0, slash);
            result.Path = slash < 0 ? "/" : text.Substring(slash);

            int at = authority.LastIndexOf('@');
            if (at >= 0) authority = authority.Substring(at + 1);

            int colon = authority.LastIndexOf(':');
            if (colon > 0)
            {
                int port;
                if (int.TryParse(authority.Substring(colon + 1), out port)) result.Port = port;
                authority = authority.Substring(0, colon);
            }

            result.Host = authority.ToLowerInvariant();
            if (result.Path.Length == 0) result.Path = "/";

            return result;
        }
    }

    /// <summary>
    /// Поток тела ответа. Умеет три режима из HTTP/1.1: Content-Length,
    /// chunked и «до закрытия соединения».
    /// </summary>
    public sealed class HttpBodyStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;          // -1 — читать до конца
        private readonly bool _chunked;
        private long _remaining;
        private long _chunkLeft;
        private bool _done;

        public HttpBodyStream(Stream inner, long length, bool chunked)
        {
            _inner = inner;
            _length = length;
            _chunked = chunked;
            _remaining = length;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_done) return 0;

            if (_chunked) return ReadChunked(buffer, offset, count);

            if (_length >= 0)
            {
                if (_remaining <= 0) { _done = true; return 0; }
                if (count > _remaining) count = (int)_remaining;
            }

            int read = _inner.Read(buffer, offset, count);
            if (read <= 0) { _done = true; return 0; }

            if (_length >= 0) _remaining -= read;
            return read;
        }

        private int ReadChunked(byte[] buffer, int offset, int count)
        {
            if (_chunkLeft == 0)
            {
                string line = ReadLine();
                if (line == null) { _done = true; return 0; }

                int semicolon = line.IndexOf(';');
                if (semicolon >= 0) line = line.Substring(0, semicolon);

                int size;
                if (!int.TryParse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out size))
                    throw new IOException("Повреждён chunked-ответ: " + line);

                if (size == 0)
                {
                    while (true)                       // трейлеры до пустой строки
                    {
                        string trailer = ReadLine();
                        if (trailer == null || trailer.Length == 0) break;
                    }
                    _done = true;
                    return 0;
                }

                _chunkLeft = size;
            }

            if (count > _chunkLeft) count = (int)_chunkLeft;

            int read = _inner.Read(buffer, offset, count);
            if (read <= 0) throw new IOException("Chunked-ответ оборвался");

            _chunkLeft -= read;
            if (_chunkLeft == 0) ReadLine();           // CRLF после данных чанка

            return read;
        }

        /// <summary>Читает строку до CRLF; null — конец потока.</summary>
        private string ReadLine()
        {
            var text = new StringBuilder();
            while (true)
            {
                int value = _inner.ReadByte();
                if (value < 0) return text.Length == 0 ? null : text.ToString();
                if (value == '\n')
                {
                    if (text.Length > 0 && text[text.Length - 1] == '\r') text.Length--;
                    return text.ToString();
                }
                text.Append((char)value);
                if (text.Length > 8192) throw new IOException("Слишком длинная строка в HTTP-ответе");
            }
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }
}
