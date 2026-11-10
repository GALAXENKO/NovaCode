using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace NovaCode.Core
{
    public sealed class MockResponse
    {
        public int Status = 200;
        public string Reason = "OK";
        public string Body = "";

        /// <summary>Если задано — ответ отдаётся по частям с паузами (имитация живого стриминга).</summary>
        public List<string> Chunks;
        public int ChunkDelayMs = 45;
    }

    /// <summary>
    /// Минимальный HTTP-сервер на TcpListener, отдающий заранее заданные SSE-ответы.
    /// Нужен для двух вещей: самотест агентского цикла без сети и демо-режим (--mock),
    /// в котором GUI можно посмотреть вообще без ключа API.
    /// HttpListener не подходит: на XP без прав администратора он требует URL ACL.
    /// </summary>
    public sealed class MockLlmServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Thread _thread;
        private readonly Func<string, MockResponse> _handler;
        private readonly object _gate = new object();
        private volatile bool _stopped;

        public int RequestCount;
        public string FirstBody = "";
        public string LastBody = "";
        public string LastAuthorization = "";

        public MockLlmServer(Func<string, MockResponse> handler)
        {
            _handler = handler;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Start();
        }

        public int Port
        {
            get { return ((IPEndPoint)_listener.LocalEndpoint).Port; }
        }

        public string BaseUrl
        {
            get { return "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture) + "/v1"; }
        }

        /// <summary>Собирает SSE-тело из готовых JSON-кусков.</summary>
        public static MockResponse Sse(List<string> chunks)
        {
            var sb = new StringBuilder();
            foreach (string chunk in chunks)
                sb.Append("data: ").Append(chunk).Append("\n\n");
            sb.Append("data: [DONE]\n\n");
            return new MockResponse { Body = sb.ToString() };
        }

        /// <summary>Демо-сценарий: стриминг текста + настоящий инструмент ls, затем итог.</summary>
        public static MockLlmServer Demo()
        {
            return new MockLlmServer(delegate (string body)
            {
                if (body.Contains("\"role\":\"tool\""))
                {
                    return new MockResponse
                    {
                        Chunks = DemoChunks(new[]
                        {
                            "Готово. ",
                            "Это демонстрационный режим: ",
                            "модель не вызывается, ",
                            "ответы заготовлены заранее. ",
                            "А вот инструмент ls самый настоящий — ",
                            "он показал реальное содержимое рабочей папки."
                        }, 150, 60)
                    };
                }

                return new MockResponse
                {
                    Chunks = DemoChunks(new[]
                    {
                        "Смотрю, ",
                        "что лежит ",
                        "в рабочей папке."
                    }, 120, 30, true)
                };
            });
        }

        private static List<string> DemoChunks(string[] parts, long promptTokens, long completionTokens, bool withTool = false)
        {
            var chunks = new List<string>();
            foreach (string part in parts)
                chunks.Add(Chunk(TextDelta(part), null));

            if (withTool)
            {
                chunks.Add(Chunk(ToolDelta(0, "call_demo1", "ls", "{}"), null));
                chunks.Add(Chunk(Json.NewObject(), "tool_calls", promptTokens, completionTokens));
            }
            else
            {
                chunks.Add(Chunk(Json.NewObject(), "stop", promptTokens, completionTokens));
            }
            return chunks;
        }

        private static Dictionary<string, object> TextDelta(string text)
        {
            var delta = Json.NewObject();
            delta["content"] = text;
            return delta;
        }

        private static Dictionary<string, object> ToolDelta(int index, string id, string name, string arguments)
        {
            var fn = Json.NewObject();
            fn["name"] = name;
            fn["arguments"] = arguments;

            var call = Json.NewObject();
            call["index"] = index;
            call["id"] = id;
            call["type"] = "function";
            call["function"] = fn;

            var calls = new List<object>();
            calls.Add(call);

            var delta = Json.NewObject();
            delta["tool_calls"] = calls;
            return delta;
        }

        private static string Chunk(Dictionary<string, object> delta, string finish)
        {
            return Chunk(delta, finish, 0, 0);
        }

        private static string Chunk(Dictionary<string, object> delta, string finish,
            long promptTokens, long completionTokens)
        {
            var choice = Json.NewObject();
            choice["delta"] = delta;
            choice["finish_reason"] = finish;

            var choices = new List<object>();
            choices.Add(choice);

            var root = Json.NewObject();
            root["choices"] = choices;

            if (promptTokens > 0 || completionTokens > 0)
            {
                var usage = Json.NewObject();
                usage["prompt_tokens"] = promptTokens;
                usage["completion_tokens"] = completionTokens;
                root["usage"] = usage;
            }
            return Json.Stringify(root);
        }

        // ------------------------------------------------------------ транспорт

        private void Loop()
        {
            while (!_stopped)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { return; }

                try { Serve(client); }
                catch { }
                finally { try { client.Close(); } catch { } }
            }
        }

        private void Serve(TcpClient client)
        {
            client.ReceiveTimeout = 15000;
            NetworkStream stream = client.GetStream();
            var chunk = new byte[8192];
            var collected = new MemoryStream();

            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int read = stream.Read(chunk, 0, chunk.Length);
                if (read <= 0) return;
                collected.Write(chunk, 0, read);
                byte[] probe = collected.ToArray();
                headerEnd = FindHeaderEnd(probe, probe.Length);
            }

            byte[] received = collected.ToArray();
            string head = Encoding.ASCII.GetString(received, 0, headerEnd);

            int contentLength = 0;
            string authorization = "";
            bool expectContinue = false;

            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                string name = lines[i].Substring(0, colon).Trim().ToLowerInvariant();
                string value = lines[i].Substring(colon + 1).Trim();
                if (name == "content-length")
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
                else if (name == "authorization")
                    authorization = value;
                else if (name == "expect" && value.ToLowerInvariant().Contains("100-continue"))
                    expectContinue = true;
            }

            if (expectContinue)
            {
                byte[] cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                stream.Write(cont, 0, cont.Length);
                stream.Flush();
            }

            var body = new MemoryStream();
            int already = received.Length - (headerEnd + 4);
            if (already > 0) body.Write(received, headerEnd + 4, already);
            while (body.Length < contentLength)
            {
                int read = stream.Read(chunk, 0, chunk.Length);
                if (read <= 0) break;
                body.Write(chunk, 0, read);
            }

            byte[] bodyBytes = body.ToArray();
            int bodyLength = (int)Math.Min(bodyBytes.Length, contentLength);
            string bodyText = Encoding.UTF8.GetString(bodyBytes, 0, bodyLength);

            lock (_gate)
            {
                RequestCount++;
                if (RequestCount == 1) FirstBody = bodyText;
                LastBody = bodyText;
                LastAuthorization = authorization;
            }

            MockResponse response = _handler(bodyText);
            Send(stream, response);
        }

        private static void Send(NetworkStream stream, MockResponse response)
        {
            string contentType = response.Status == 200 ? "text/event-stream" : "application/json";
            bool streaming = response.Chunks != null && response.Chunks.Count > 0;
            byte[] payload = streaming ? null : Encoding.UTF8.GetBytes(response.Body);

            string headerBlock =
                "HTTP/1.1 " + response.Status.ToString(CultureInfo.InvariantCulture) + " " + response.Reason + "\r\n" +
                "Content-Type: " + contentType + "\r\n" +
                "Cache-Control: no-cache\r\n" +
                (streaming
                    ? "Connection: close\r\n\r\n"
                    : "Content-Length: " + payload.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n");

            byte[] headerBytes = Encoding.ASCII.GetBytes(headerBlock);
            stream.Write(headerBytes, 0, headerBytes.Length);

            if (streaming)
            {
                // Без Content-Length — соединение закрывается в конце,
                // зато клиент получает куски по мере отправки, как от настоящего сервера.
                foreach (string json in response.Chunks)
                {
                    byte[] part = Encoding.UTF8.GetBytes("data: " + json + "\n\n");
                    stream.Write(part, 0, part.Length);
                    stream.Flush();
                    if (response.ChunkDelayMs > 0) Thread.Sleep(response.ChunkDelayMs);
                }
                byte[] done = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
                stream.Write(done, 0, done.Length);
                stream.Flush();
            }
            else
            {
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
            }
        }

        private static int FindHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 3; i < length; i++)
            {
                if (buffer[i] == (byte)'\n' && buffer[i - 1] == (byte)'\r' &&
                    buffer[i - 2] == (byte)'\n' && buffer[i - 3] == (byte)'\r')
                    return i - 3;
            }
            return -1;
        }

        public void Dispose()
        {
            _stopped = true;
            try { _listener.Stop(); } catch { }
            try { _thread.Join(2000); } catch { }
        }
    }
}
