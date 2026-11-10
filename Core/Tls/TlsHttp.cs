using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace NovaCode.Core.Tls
{
    /// <summary>Ответ HTTP/1.1: код, заголовки и поток тела.</summary>
    public sealed class HttpResponse : IDisposable
    {
        public int Status;
        public string Reason = "";
        public readonly Dictionary<string, string> Headers =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Stream Body;
        public Tls13Connection Tls;

        private TcpClient _tcp;
        private bool _disposed;

        internal void Attach(TcpClient tcp) { _tcp = tcp; }

        public string Header(string name)
        {
            string value;
            return Headers.TryGetValue(name, out value) ? value : null;
        }

        public bool IsSuccess { get { return Status >= 200 && Status < 300; } }

        /// <summary>Читает тело целиком как текст — для коротких ответов и ошибок.</summary>
        public string ReadAllText()
        {
            if (Body == null) return "";

            using (var reader = new StreamReader(Body, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        /// <summary>Рвёт соединение из другого потока — отмена запроса.</summary>
        public void Abort()
        {
            try { if (_tcp != null) _tcp.Close(); }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { if (Tls != null) Tls.Close(); } catch { }
            try { if (_tcp != null) _tcp.Close(); } catch { }
        }
    }

    /// <summary>
    /// Минимальный клиент HTTP/1.1 поверх собственного TLS. Нужен потому, что
    /// HttpWebRequest на XP ходит только через Schannel с TLS 1.0.
    /// </summary>
    public static class TlsHttp
    {
        public const string UserAgent = "NovaCode/1.0 (Windows NT 5.1; .NET 4.0)";

        public static HttpResponse Get(string url, string accept, bool insecure, int timeoutMs)
        {
            return Send(url, "GET", null, null, accept, insecure, timeoutMs);
        }

        public static HttpResponse PostJson(string url, string json, string accept, bool insecure, int timeoutMs)
        {
            var headers = new Dictionary<string, string>();
            headers["Content-Type"] = "application/json";
            return Send(url, "POST", headers, Encoding.UTF8.GetBytes(json), accept, insecure, timeoutMs);
        }

        public static HttpResponse Send(string url, string method,
                                        Dictionary<string, string> headers, byte[] body,
                                        string accept, bool insecure, int timeoutMs)
        {
            HttpUrl target = HttpUrl.Parse(url);
            if (target.Host.Length == 0) throw new TlsException("Некорректный адрес: " + url);

            var tcp = new TcpClient();
            tcp.NoDelay = true;
            tcp.ReceiveTimeout = timeoutMs;
            tcp.SendTimeout = timeoutMs;

            Connect(tcp, target.Host, target.Port, timeoutMs);

            Stream stream;
            Tls13Connection tls = null;

            if (target.Secure)
            {
                tls = new Tls13Connection(tcp.GetStream(), target.Host, insecure);
                tls.Handshake();
                stream = tls;
            }
            else
            {
                stream = new BufferedStream(tcp.GetStream(), 16384);
            }

            try
            {
                var request = new StringBuilder();
                request.Append(method).Append(' ').Append(target.Path).Append(" HTTP/1.1\r\n");
                request.Append("Host: ").Append(target.Host);
                if ((target.Secure && target.Port != 443) || (!target.Secure && target.Port != 80))
                    request.Append(':').Append(target.Port.ToString(CultureInfo.InvariantCulture));
                request.Append("\r\n");
                request.Append("User-Agent: ").Append(UserAgent).Append("\r\n");
                request.Append("Accept: ").Append(string.IsNullOrEmpty(accept) ? "*/*" : accept).Append("\r\n");
                request.Append("Accept-Encoding: identity\r\n");

                if (headers != null)
                {
                    foreach (KeyValuePair<string, string> header in headers)
                        request.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
                }

                if (body != null)
                {
                    request.Append("Content-Length: ")
                           .Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                }

                request.Append("Connection: close\r\n\r\n");

                byte[] head = Encoding.UTF8.GetBytes(request.ToString());
                stream.Write(head, 0, head.Length);
                if (body != null) stream.Write(body, 0, body.Length);
                stream.Flush();

                var response = new HttpResponse();
                response.Tls = tls;
                response.Attach(tcp);

                ReadHead(stream, response);

                bool chunked = false;
                string transfer = response.Header("Transfer-Encoding");
                if (transfer != null && transfer.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                    chunked = true;

                long length = -1;
                string contentLength = response.Header("Content-Length");
                if (!chunked && contentLength != null)
                {
                    long parsed;
                    if (long.TryParse(contentLength.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                        length = parsed;
                }

                // 204/304 и ответы на HEAD тела не имеют.
                if (response.Status == 204 || response.Status == 304) length = 0;

                response.Body = new HttpBodyStream(stream, length, chunked);
                return response;
            }
            catch
            {
                try { if (tls != null) tls.Close(); } catch { }
                try { tcp.Close(); } catch { }
                throw;
            }
        }

        private static void Connect(TcpClient tcp, string host, int port, int timeoutMs)
        {
            IAsyncResult pending = tcp.BeginConnect(host, port, null, null);

            if (!pending.AsyncWaitHandle.WaitOne(timeoutMs, false))
            {
                try { tcp.Close(); } catch { }
                throw new TlsException("Не удалось подключиться к " + host + ":" + port + " — таймаут");
            }

            try
            {
                tcp.EndConnect(pending);
            }
            catch (SocketException ex)
            {
                throw new TlsException("Не удалось подключиться к " + host + ":" + port + ": " + ex.Message);
            }
        }

        /// <summary>Читает строку статуса и заголовки.</summary>
        private static void ReadHead(Stream stream, HttpResponse response)
        {
            string statusLine = ReadLine(stream);
            if (statusLine == null) throw new TlsException("Сервер закрыл соединение, не ответив");

            string[] parts = statusLine.Split(' ');
            if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal))
                throw new TlsException("Некорректная строка ответа: " + statusLine);

            int status;
            if (!int.TryParse(parts[1], out status))
                throw new TlsException("Некорректный код ответа: " + statusLine);

            response.Status = status;
            response.Reason = parts.Length > 2 ? statusLine.Substring(parts[0].Length + parts[1].Length + 2) : "";

            while (true)
            {
                string line = ReadLine(stream);
                if (line == null) break;
                if (line.Length == 0) break;

                int colon = line.IndexOf(':');
                if (colon <= 0) continue;

                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();

                // Повторяющиеся заголовки склеиваем через запятую.
                string existing;
                if (response.Headers.TryGetValue(name, out existing))
                    response.Headers[name] = existing + ", " + value;
                else
                    response.Headers[name] = value;
            }
        }

        private static string ReadLine(Stream stream)
        {
            var text = new StringBuilder();
            while (true)
            {
                int value = stream.ReadByte();
                if (value < 0) return text.Length == 0 ? null : text.ToString();
                if (value == '\n')
                {
                    if (text.Length > 0 && text[text.Length - 1] == '\r') text.Length--;
                    return text.ToString();
                }
                text.Append((char)value);
                if (text.Length > 16384) throw new TlsException("Слишком длинная строка в ответе HTTP");
            }
        }
    }
}
