using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NovaCode.Core
{
    public sealed class ChatMessage
    {
        public string Role = "user";
        public string Content = "";
        public string Reasoning = "";
        public List<ToolCall> ToolCalls;
        public string ToolCallId = "";
        public string ToolName = "";
        public bool ToolError;
        public string Time = "";

        public static ChatMessage User(string text)
        {
            return new ChatMessage { Role = "user", Content = text, Time = Util.ShortTime() };
        }

        public static ChatMessage Tool(ToolCall call, ToolResult result)
        {
            return new ChatMessage
            {
                Role = "tool",
                ToolCallId = call.Id,
                ToolName = call.Name,
                Content = result.Output,
                ToolError = result.IsError,
                Time = Util.ShortTime()
            };
        }

        /// <summary>Представление для [OI]-совместимого API.</summary>
        public Dictionary<string, object> ToApi()
        {
            var d = Json.NewObject();
            d["role"] = Role;

            if (Role == "assistant")
            {
                d["content"] = Content ?? "";
                if (ToolCalls != null && ToolCalls.Count > 0)
                {
                    var arr = new List<object>();
                    foreach (ToolCall call in ToolCalls)
                    {
                        var fn = Json.NewObject();
                        fn["name"] = call.Name;
                        fn["arguments"] = string.IsNullOrEmpty(call.Arguments) ? "{}" : call.Arguments;

                        var item = Json.NewObject();
                        item["id"] = call.Id;
                        item["type"] = "function";
                        item["function"] = fn;
                        arr.Add(item);
                    }
                    d["tool_calls"] = arr;
                }
            }
            else if (Role == "tool")
            {
                d["tool_call_id"] = ToolCallId;
                d["content"] = Content ?? "";
            }
            else
            {
                d["content"] = Content ?? "";
            }

            return d;
        }

        public Dictionary<string, object> ToStorage()
        {
            var d = Json.NewObject();
            d["role"] = Role;
            d["content"] = Content ?? "";
            if (!string.IsNullOrEmpty(Reasoning)) d["reasoning"] = Reasoning;
            if (!string.IsNullOrEmpty(ToolCallId)) d["toolCallId"] = ToolCallId;
            if (!string.IsNullOrEmpty(ToolName)) d["toolName"] = ToolName;
            if (ToolError) d["toolError"] = true;
            if (!string.IsNullOrEmpty(Time)) d["time"] = Time;

            if (ToolCalls != null && ToolCalls.Count > 0)
            {
                var arr = new List<object>();
                foreach (ToolCall call in ToolCalls)
                {
                    var c = Json.NewObject();
                    c["id"] = call.Id;
                    c["name"] = call.Name;
                    c["arguments"] = call.Arguments;
                    arr.Add(c);
                }
                d["toolCalls"] = arr;
            }
            return d;
        }

        public static ChatMessage FromStorage(Dictionary<string, object> d)
        {
            var m = new ChatMessage();
            m.Role = Json.Str(d, "role", "user");
            m.Content = Json.Str(d, "content", "");
            m.Reasoning = Json.Str(d, "reasoning", "");
            m.ToolCallId = Json.Str(d, "toolCallId", "");
            m.ToolName = Json.Str(d, "toolName", "");
            m.ToolError = Json.Bool(d, "toolError", false);
            m.Time = Json.Str(d, "time", "");

            var arr = Json.Arr(d, "toolCalls");
            if (arr != null)
            {
                m.ToolCalls = new List<ToolCall>();
                foreach (object o in arr)
                {
                    var c = Json.AsObject(o);
                    if (c == null) continue;
                    m.ToolCalls.Add(new ToolCall
                    {
                        Id = Json.Str(c, "id", ""),
                        Name = Json.Str(c, "name", ""),
                        Arguments = Json.Str(c, "arguments", "{}")
                    });
                }
            }
            return m;
        }
    }

    public sealed class Session
    {
        public string Id = "";
        public string Title = "";
        public string Model = "";
        public DateTime Created = DateTime.Now;
        public DateTime Updated = DateTime.Now;
        public long PromptTokens;
        public long CompletionTokens;
        public List<ChatMessage> Messages = new List<ChatMessage>();

        public string FilePath
        {
            get { return Path.Combine(Config.SessionsDir, Id + ".json"); }
        }

        public static Session Create(string model)
        {
            var s = new Session();
            s.Id = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                 + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            s.Title = "Новая сессия";
            s.Model = model;
            return s;
        }

        public void Touch()
        {
            Updated = DateTime.Now;
        }

        public void Save()
        {
            try
            {
                var d = Json.NewObject();
                d["id"] = Id;
                d["title"] = Title;
                d["model"] = Model;
                d["created"] = Created.ToString("o", CultureInfo.InvariantCulture);
                d["updated"] = Updated.ToString("o", CultureInfo.InvariantCulture);
                d["promptTokens"] = PromptTokens;
                d["completionTokens"] = CompletionTokens;

                var arr = new List<object>();
                foreach (ChatMessage m in Messages) arr.Add(m.ToStorage());
                d["messages"] = arr;

                File.WriteAllText(FilePath, Json.StringifyIndented(d), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Error("Сохранение сессии " + Id, ex);
            }
        }

        public static Session Load(string path)
        {
            try
            {
                var d = Json.AsObject(Json.Parse(File.ReadAllText(path, Encoding.UTF8)));
                if (d == null) return null;

                var s = new Session();
                s.Id = Json.Str(d, "id", Path.GetFileNameWithoutExtension(path));
                s.Title = Json.Str(d, "title", "Сессия");
                s.Model = Json.Str(d, "model", "");
                s.Created = ParseDate(Json.Str(d, "created", ""), DateTime.Now);
                s.Updated = ParseDate(Json.Str(d, "updated", ""), DateTime.Now);
                s.PromptTokens = (long)Json.Num(d, "promptTokens", 0);
                s.CompletionTokens = (long)Json.Num(d, "completionTokens", 0);

                var arr = Json.Arr(d, "messages");
                if (arr != null)
                {
                    foreach (object o in arr)
                    {
                        var m = Json.AsObject(o);
                        if (m != null) s.Messages.Add(ChatMessage.FromStorage(m));
                    }
                }
                return s;
            }
            catch (Exception ex)
            {
                Log.Error("Чтение сессии " + path, ex);
                return null;
            }
        }

        private static DateTime ParseDate(string value, DateTime fallback)
        {
            DateTime parsed;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
                return parsed;
            return fallback;
        }

        public static List<Session> LoadAll()
        {
            var result = new List<Session>();
            try
            {
                foreach (string file in Directory.GetFiles(Config.SessionsDir, "*.json"))
                {
                    Session s = Load(file);
                    if (s != null) result.Add(s);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Список сессий", ex);
            }

            result.Sort(delegate (Session a, Session b) { return b.Updated.CompareTo(a.Updated); });
            return result;
        }

        public void Delete()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch (Exception ex) { Log.Error("Удаление сессии", ex); }
        }
    }
}
