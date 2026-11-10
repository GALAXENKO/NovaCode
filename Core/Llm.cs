using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using NovaCode.Core.Tls;

namespace NovaCode.Core
{
    public sealed class ToolCall
    {
        public string Id = "";
        public string Name = "";
        public string Arguments = "";
    }

    public sealed class AssistantTurn
    {
        public string Content = "";
        public string Reasoning = "";
        public List<ToolCall> ToolCalls = new List<ToolCall>();
        public long PromptTokens;
        public long CompletionTokens;
        public string FinishReason = "";
    }

    public sealed class LlmException : Exception
    {
        public LlmException(string message) : base(message) { }
    }

    /// <summary>Ответ API с кодом ошибки: тело нужно, чтобы понять причину.</summary>
    public sealed class HttpStatusException : Exception
    {
        public readonly int Status;
        public readonly string BodyText;

        public HttpStatusException(int status, string reason, string body)
            : base(Format(status, reason, body))
        {
            Status = status;
            BodyText = body ?? "";
        }

        private static string Format(int status, string reason, string body)
        {
            string message = "";

            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    var root = Json.AsObject(Json.Parse(body));
                    var error = Json.Obj(root, "error");
                    message = Json.Str(error, "message", "");
                    if (message.Length == 0) message = Json.Str(root, "message", "");
                }
                catch { }

                if (message.Length == 0)
                    message = body.Length > 400 ? body.Substring(0, 400) : body;
            }

            if (string.IsNullOrEmpty(message)) message = "сервер не объяснил причину";

            return "HTTP " + status + (string.IsNullOrEmpty(reason) ? "" : " " + reason) + ": " + message;
        }
    }

    /// <summary>
    /// Клиент [OI]-совместимого API (/chat/completions) с потоковой передачей.
    /// Работает на .NET 4.0: HttpWebRequest + чтение SSE построчно, без async/await
    /// (в 4.0 нет TaskAwaiter без Async Targeting Pack).
    /// </summary>
    public sealed class LlmClient
    {
        private readonly Config _config;
        private HttpWebRequest _current;
        private volatile HttpResponse _currentResponse;
        private volatile bool _cancelled;

        public LlmClient(Config config)
        {
            _config = config;
        }

        public bool IsCancelled { get { return _cancelled; } }

        public void Cancel()
        {
            _cancelled = true;

            HttpWebRequest request = _current;
            if (request != null)
            {
                try { request.Abort(); }
                catch { }
            }

            HttpResponse response = _currentResponse;
            if (response != null)
            {
                try { response.Abort(); }
                catch { }
            }
        }

        public void Reset()
        {
            _cancelled = false;
        }

        public static void PrepareTls(bool insecure)
        {
            try
            {
                // В .NET 4.0 доступны только Ssl3 и Tls (1.0).
                // На Windows XP большего Schannel и не умеет, а на новых ОС
                // HTTPS к современным API придётся пускать через локальный прокси.
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Ssl3 | SecurityProtocolType.Tls;
            }
            catch { }

            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = 8;

            if (insecure)
            {
                ServicePointManager.ServerCertificateValidationCallback =
                    delegate { return true; };
            }
        }

        // ------------------------------------------------------------ запрос

        public AssistantTurn Chat(
            List<Dictionary<string, object>> messages,
            List<Dictionary<string, object>> tools,
            Action<string> onText,
            Action<string> onReasoning)
        {
            _cancelled = false;

            if (_config.OwnTls) return ChatOwnTls(messages, tools, onText, onReasoning);

            bool includeUsage = true;
            HttpWebResponse response = null;
            try
            {
                response = Send(messages, tools, includeUsage);
            }
            catch (WebException ex)
            {
                string detail = ReadErrorBody(ex);
                if (detail.IndexOf("stream_options", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Провайдер не знает stream_options — повторяем без него.
                    try
                    {
                        response = Send(messages, tools, false);
                    }
                    catch (WebException retry)
                    {
                        throw new LlmException(FriendlyWebError(retry, ReadErrorBody(retry)));
                    }
                }
                else
                {
                    throw new LlmException(FriendlyWebError(ex, detail));
                }
            }

            try
            {
                using (response)
                using (Stream stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return ReadStream(reader, onText, onReasoning);
                }
            }
            catch (WebException ex)
            {
                if (_cancelled) return new AssistantTurn();
                throw new LlmException(FriendlyWebError(ex, ""));
            }
            catch (IOException)
            {
                if (_cancelled) return new AssistantTurn();
                throw;
            }
            finally
            {
                _current = null;
            }
        }

        private HttpWebResponse Send(List<Dictionary<string, object>> messages,
                                     List<Dictionary<string, object>> tools,
                                     bool includeUsage)
        {
            string payload = BuildPayload(messages, tools, includeUsage);
            byte[] bytes = new UTF8Encoding(false).GetBytes(payload);

            var request = (HttpWebRequest)WebRequest.Create(_config.ChatCompletionsUrl);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "text/event-stream";
            request.UserAgent = "NovaCode/1.0 (Windows XP; .NET 4.0)";
            request.Timeout = 120000;
            request.ReadWriteTimeout = 300000;
            request.KeepAlive = true;

            if (!string.IsNullOrEmpty(_config.ApiKey))
                request.Headers["Authorization"] = "Bearer " + _config.ApiKey;

            _current = request;

            try
            {
                using (Stream requestStream = request.GetRequestStream())
                {
                    requestStream.Write(bytes, 0, bytes.Length);
                }
                return (HttpWebResponse)request.GetResponse();
            }
            catch
            {
                _current = null;
                throw;
            }
        }

        // ------------------------------------------------------------ свой TLS

        private string BuildPayload(List<Dictionary<string, object>> messages,
                                    List<Dictionary<string, object>> tools,
                                    bool includeUsage)
        {
            var body = Json.NewObject();
            body["model"] = _config.Model;
            body["messages"] = messages;
            body["stream"] = true;
            body["temperature"] = _config.Temperature;
            if (_config.MaxTokens > 0) body["max_tokens"] = _config.MaxTokens;

            if (tools != null && tools.Count > 0)
            {
                body["tools"] = tools;
                body["tool_choice"] = "auto";
            }

            if (includeUsage)
            {
                var opts = Json.NewObject();
                opts["include_usage"] = true;
                body["stream_options"] = opts;
            }

            return Json.Stringify(body);
        }

        /// <summary>
        /// Запрос через собственный TLS 1.3 и HTTP/1.1 — единственный путь,
        /// который работает на Windows XP: системный Schannel знает только TLS 1.0.
        /// </summary>
        private AssistantTurn ChatOwnTls(
            List<Dictionary<string, object>> messages,
            List<Dictionary<string, object>> tools,
            Action<string> onText,
            Action<string> onReasoning)
        {
            HttpResponse response = null;

            try
            {
                response = SendOwnTls(BuildPayload(messages, tools, true));
            }
            catch (HttpStatusException ex)
            {
                if (ex.BodyText.IndexOf("stream_options", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Провайдер не знает stream_options — повторяем без него.
                    try
                    {
                        response = SendOwnTls(BuildPayload(messages, tools, false));
                    }
                    catch (HttpStatusException retry)
                    {
                        throw new LlmException(retry.Message);
                    }
                    catch (Exception retry)
                    {
                        if (_cancelled) return new AssistantTurn();
                        throw new LlmException(DescribeTransportError(retry));
                    }
                }
                else
                {
                    throw new LlmException(ex.Message);
                }
            }
            catch (Exception ex)
            {
                if (_cancelled) return new AssistantTurn();
                throw new LlmException(DescribeTransportError(ex));
            }

            try
            {
                using (response)
                using (var reader = new StreamReader(response.Body, Encoding.UTF8))
                {
                    return ReadStream(reader, onText, onReasoning);
                }
            }
            catch (Exception ex)
            {
                if (_cancelled) return new AssistantTurn();
                throw new LlmException(DescribeTransportError(ex));
            }
            finally
            {
                _currentResponse = null;
            }
        }

        private HttpResponse SendOwnTls(string payload)
        {
            var headers = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(_config.ApiKey))
                headers["Authorization"] = "Bearer " + _config.ApiKey;

            HttpResponse response = TlsHttp.Send(
                _config.ChatCompletionsUrl,
                "POST",
                headers,
                new UTF8Encoding(false).GetBytes(payload),
                "text/event-stream",
                _config.InsecureTls,
                120000);

            if (!response.IsSuccess)
            {
                int status = response.Status;
                string reason = response.Reason;
                string body = "";
                try { body = response.ReadAllText(); }
                catch { }
                response.Dispose();

                throw new HttpStatusException(status, reason, body);
            }

            _currentResponse = response;
            return response;
        }

        private static string DescribeTransportError(Exception ex)
        {
            if (ex is TlsException) return "TLS: " + ex.Message;

            if (ex is System.Net.Sockets.SocketException)
                return "Сетевая ошибка: " + ex.Message;

            if (ex is IOException)
                return "Обрыв соединения: " + ex.Message;

            return ex.GetType().Name + ": " + ex.Message;
        }

        private static string ReadErrorBody(WebException ex)
        {
            try
            {
                if (ex.Response == null) return "";
                using (var s = ex.Response.GetResponseStream())
                using (var r = new StreamReader(s, Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch { return ""; }
        }

        private static string FriendlyWebError(WebException ex, string body)
        {
            string message = "";

            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    var root = Json.AsObject(Json.Parse(body));
                    var err = Json.Obj(root, "error");
                    message = Json.Str(err, "message", "");
                    if (message.Length == 0) message = Json.Str(root, "message", "");
                }
                catch { message = body.Length > 400 ? body.Substring(0, 400) : body; }
            }

            if (string.IsNullOrEmpty(message)) message = ex.Message;

            var http = ex.Response as HttpWebResponse;
            if (http != null)
                return "HTTP " + (int)http.StatusCode + " " + http.StatusDescription + ": " + message;

            switch (ex.Status)
            {
                case WebExceptionStatus.NameResolutionFailure:
                    return "Не удалось разрешить имя сервера. Проверьте адрес API и DNS.";
                case WebExceptionStatus.ConnectFailure:
                    return "Не удалось подключиться к серверу. Проверьте адрес, порт и сеть.";
                case WebExceptionStatus.TrustFailure:
                    return "Сертификат сервера не принят. На Windows XP Schannel понимает только TLS 1.0 — "
                         + "включите «Не проверять сертификат» или используйте HTTP-прокси.";
                case WebExceptionStatus.SecureChannelFailure:
                    return "Ошибка TLS. Windows XP поддерживает только TLS 1.0, а сервер, скорее всего, требует 1.2.";
                case WebExceptionStatus.Timeout:
                    return "Таймаут запроса к API.";
                case WebExceptionStatus.ProtocolError:
                    return "Ошибка протокола: " + message;
                default:
                    return message;
            }
        }

        // ------------------------------------------------------------ SSE

        private AssistantTurn ReadStream(StreamReader reader, Action<string> onText, Action<string> onReasoning)
        {
            var turn = new AssistantTurn();
            var toolBuilders = new Dictionary<int, ToolCall>();
            var order = new List<int>();
            var text = new StringBuilder();
            var reasoning = new StringBuilder();

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (_cancelled) break;
                if (line.Length == 0) continue;

                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    string payload = line.Substring(5).Trim();
                    if (payload.Length == 0) continue;
                    if (payload == "[DONE]") break;

                    Dictionary<string, object> chunk;
                    try { chunk = Json.AsObject(Json.Parse(payload)); }
                    catch { continue; }
                    if (chunk == null) continue;

                    var error = Json.Obj(chunk, "error");
                    if (error != null)
                        throw new LlmException(Json.Str(error, "message", "Ошибка API"));

                    var usage = Json.Obj(chunk, "usage");
                    if (usage != null)
                    {
                        turn.PromptTokens = (long)Json.Num(usage, "prompt_tokens", turn.PromptTokens);
                        turn.CompletionTokens = (long)Json.Num(usage, "completion_tokens", turn.CompletionTokens);
                    }

                    var choices = Json.Arr(chunk, "choices");
                    if (choices == null || choices.Count == 0) continue;

                    var choice = Json.AsObject(choices[0]);
                    if (choice == null) continue;

                    string finish = Json.Str(choice, "finish_reason", "");
                    if (finish.Length > 0) turn.FinishReason = finish;

                    var delta = Json.Obj(choice, "delta");
                    if (delta == null) delta = Json.Obj(choice, "message");
                    if (delta == null) continue;

                    string reasoningDelta = Json.Str(delta, "reasoning_content", null);
                    if (string.IsNullOrEmpty(reasoningDelta)) reasoningDelta = Json.Str(delta, "reasoning", null);
                    if (!string.IsNullOrEmpty(reasoningDelta))
                    {
                        reasoning.Append(reasoningDelta);
                        if (onReasoning != null) onReasoning(reasoningDelta);
                    }

                    string contentDelta = Json.Str(delta, "content", null);
                    if (!string.IsNullOrEmpty(contentDelta))
                    {
                        text.Append(contentDelta);
                        if (onText != null) onText(contentDelta);
                    }

                    var toolCalls = Json.Arr(delta, "tool_calls");
                    if (toolCalls != null)
                    {
                        foreach (object item in toolCalls)
                        {
                            var tc = Json.AsObject(item);
                            if (tc == null) continue;

                            int index = Json.Int(tc, "index", 0);
                            ToolCall builder;
                            if (!toolBuilders.TryGetValue(index, out builder))
                            {
                                builder = new ToolCall();
                                toolBuilders[index] = builder;
                                order.Add(index);
                            }

                            string id = Json.Str(tc, "id", null);
                            if (!string.IsNullOrEmpty(id)) builder.Id = id;

                            var fn = Json.Obj(tc, "function");
                            if (fn != null)
                            {
                                string name = Json.Str(fn, "name", null);
                                if (!string.IsNullOrEmpty(name)) builder.Name = name;

                                string args = Json.Str(fn, "arguments", null);
                                if (args != null) builder.Arguments += args;
                            }
                        }
                    }
                }
            }

            turn.Content = text.ToString();
            turn.Reasoning = reasoning.ToString();

            order.Sort();
            foreach (int index in order)
            {
                ToolCall call = toolBuilders[index];
                if (string.IsNullOrEmpty(call.Id)) call.Id = "call_" + index.ToString(CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(call.Name)) continue;
                if (string.IsNullOrEmpty(call.Arguments)) call.Arguments = "{}";
                turn.ToolCalls.Add(call);
            }

            return turn;
        }

        /// <summary>Быстрая проверка соединения из настроек.</summary>
        public string TestConnection()
        {
            var messages = new List<Dictionary<string, object>>();
            var user = Json.NewObject();
            user["role"] = "user";
            user["content"] = "ping";
            messages.Add(user);

            var savedMax = _config.MaxTokens;
            _config.MaxTokens = 16;
            try
            {
                AssistantTurn turn = Chat(messages, null, null, null);
                return "OK. Модель ответила: " + Util.Quote(turn.Content, 60);
            }
            finally
            {
                _config.MaxTokens = savedMax;
            }
        }
    }
}
