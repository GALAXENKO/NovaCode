using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NovaCode.Core
{
    public enum AgentEventKind
    {
        Status,
        AssistantStarted,
        TextDelta,
        ReasoningDelta,
        ToolStarted,
        ToolFinished,
        TurnDone,
        Error,
        Usage
    }

    public sealed class AgentEvent
    {
        public AgentEventKind Kind;
        public string Text = "";
        public ToolCall Call;
        public ToolResult Result;
        public long PromptTokens;
        public long CompletionTokens;
    }

    /// <summary>
    /// Цикл агента: запрос к модели → вызов инструментов → снова запрос,
    /// пока модель не перестанет просить инструменты или не кончится лимит шагов.
    /// </summary>
    public sealed class Agent
    {
        private readonly Config _config;
        private readonly Session _session;
        private readonly LlmClient _llm;
        private volatile bool _cancelled;

        public event Action<AgentEvent> Event;

        public Agent(Config config, Session session)
        {
            _config = config;
            _session = session;
            _llm = new LlmClient(config);
        }

        public bool IsBusy { get; private set; }

        public void Cancel()
        {
            _cancelled = true;
            _llm.Cancel();
        }

        private void Raise(AgentEvent e)
        {
            Action<AgentEvent> handler = Event;
            if (handler != null) handler(e);
        }

        private void Status(string text)
        {
            Raise(new AgentEvent { Kind = AgentEventKind.Status, Text = text });
        }

        // ------------------------------------------------------------ основной цикл

        public void Run(string userText)
        {
            IsBusy = true;
            _cancelled = false;
            _llm.Reset();

            try
            {
                if (!string.IsNullOrEmpty(userText))
                {
                    var userMessage = ChatMessage.User(userText);
                    _session.Messages.Add(userMessage);
                    if (_session.Title == "Новая сессия" || string.IsNullOrEmpty(_session.Title))
                        _session.Title = TitleFrom(userText);
                }

                List<Dictionary<string, object>> api = BuildApiMessages();
                bool toolsEnabled = true;
                int steps = 0;

                while (!_cancelled && steps < Math.Max(1, _config.MaxSteps))
                {
                    steps++;
                    Status("думает…");

                    AssistantTurn turn;
                    try
                    {
                        turn = _llm.Chat(api, toolsEnabled ? Tools.Schemas() : null,
                            delegate (string delta)
                            {
                                Raise(new AgentEvent { Kind = AgentEventKind.TextDelta, Text = delta });
                            },
                            delegate (string delta)
                            {
                                Raise(new AgentEvent { Kind = AgentEventKind.ReasoningDelta, Text = delta });
                            });
                    }
                    catch (LlmException ex)
                    {
                        if (toolsEnabled && LooksLikeToolUnsupported(ex.Message))
                        {
                            toolsEnabled = false;
                            steps--;
                            Status("модель не поддерживает инструменты — работаю без них");
                            continue;
                        }
                        Raise(new AgentEvent { Kind = AgentEventKind.Error, Text = ex.Message });
                        return;
                    }

                    if (_cancelled)
                    {
                        Status("остановлено");
                        return;
                    }

                    var assistant = new ChatMessage
                    {
                        Role = "assistant",
                        Content = turn.Content,
                        Reasoning = turn.Reasoning,
                        ToolCalls = turn.ToolCalls.Count > 0 ? turn.ToolCalls : null,
                        Time = Util.ShortTime()
                    };
                    _session.Messages.Add(assistant);
                    api.Add(assistant.ToApi());

                    if (turn.PromptTokens > 0 || turn.CompletionTokens > 0)
                    {
                        _session.PromptTokens += turn.PromptTokens;
                        _session.CompletionTokens += turn.CompletionTokens;
                        Raise(new AgentEvent
                        {
                            Kind = AgentEventKind.Usage,
                            PromptTokens = _session.PromptTokens,
                            CompletionTokens = _session.CompletionTokens
                        });
                    }

                    _session.Touch();
                    _session.Save();

                    if (turn.ToolCalls.Count == 0)
                    {
                        Raise(new AgentEvent { Kind = AgentEventKind.TurnDone });
                        Status("готова");
                        return;
                    }

                    foreach (ToolCall call in turn.ToolCalls)
                    {
                        if (_cancelled) break;

                        Status("инструмент: " + call.Name);
                        Raise(new AgentEvent { Kind = AgentEventKind.ToolStarted, Call = call });

                        ToolResult result = Tools.Run(call.Name, call.Arguments, _config.Workspace,
                            delegate { return _cancelled; });

                        var toolMessage = ChatMessage.Tool(call, result);
                        _session.Messages.Add(toolMessage);
                        api.Add(toolMessage.ToApi());

                        Raise(new AgentEvent
                        {
                            Kind = AgentEventKind.ToolFinished,
                            Call = call,
                            Result = result
                        });

                        _session.Touch();
                        _session.Save();
                    }

                    api = BuildApiMessages();
                }

                if (steps >= _config.MaxSteps)
                    Raise(new AgentEvent
                    {
                        Kind = AgentEventKind.Error,
                        Text = "Достигнут лимит шагов (" + _config.MaxSteps + "). Увеличьте maxSteps в настройках."
                    });

                Raise(new AgentEvent { Kind = AgentEventKind.TurnDone });
                Status("готова");
            }
            catch (Exception ex)
            {
                Log.Error("Цикл агента", ex);
                Raise(new AgentEvent { Kind = AgentEventKind.Error, Text = ex.GetType().Name + ": " + ex.Message });
            }
            finally
            {
                IsBusy = false;
                _session.Touch();
                _session.Save();
            }
        }

        private static bool LooksLikeToolUnsupported(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            string m = message.ToLowerInvariant();
            return (m.Contains("tool") || m.Contains("function"))
                && (m.Contains("not support") || m.Contains("unsupported") || m.Contains("unknown") || m.Contains("invalid"));
        }

        private static string TitleFrom(string text)
        {
            string line = Util.FirstLine(text).Trim();
            if (line.Length == 0) return "Сессия";
            return line.Length > 60 ? line.Substring(0, 60) : line;
        }

        // ------------------------------------------------------------ история

        private List<Dictionary<string, object>> BuildApiMessages()
        {
            var list = new List<Dictionary<string, object>>();

            var system = Json.NewObject();
            system["role"] = "system";
            system["content"] = BuildSystemPrompt();
            list.Add(system);

            List<ChatMessage> history = Trim(_session.Messages);
            foreach (ChatMessage m in history)
            {
                if (m.Role == "system") continue;
                list.Add(m.ToApi());
            }
            return list;
        }

        /// <summary>
        /// Не даём истории распухнуть: оставляем хвост, но не начинаем с ответа инструмента,
        /// иначе API вернёт ошибку о нарушенной последовательности сообщений.
        /// </summary>
        private static List<ChatMessage> Trim(List<ChatMessage> messages)
        {
            const int maxMessages = 60;
            if (messages.Count <= maxMessages) return messages;

            int start = messages.Count - maxMessages;
            while (start < messages.Count && messages[start].Role == "tool") start++;

            var result = new List<ChatMessage>();
            for (int i = start; i < messages.Count; i++) result.Add(messages[i]);
            return result;
        }

        private string BuildSystemPrompt()
        {
            if (!string.IsNullOrEmpty(_config.SystemPrompt)) return _config.SystemPrompt;

            var sb = new StringBuilder();
            sb.AppendLine("Ты — NovaCode, агент для программирования, работающий на Windows XP.");
            sb.AppendLine("Ты помогаешь пользователю с кодом: читаешь файлы, правишь их, запускаешь команды.");
            sb.AppendLine();
            sb.AppendLine("Рабочая папка проекта: " + _config.Workspace);
            sb.AppendLine("Все пути указывай относительно неё. Выход за пределы папки запрещён.");
            sb.AppendLine();
            sb.AppendLine("Правила:");
            sb.AppendLine("- Перед изменением файла прочитай его инструментом read.");
            sb.AppendLine("- Правь файлы инструментом edit (точная замена фрагмента), а не переписывай целиком без нужды.");
            sb.AppendLine("- Команды выполняются через cmd.exe на Windows XP: нет PowerShell, нет ls/grep/cat —");
            sb.AppendLine("  используй dir, findstr, type или соответствующие инструменты ls/grep/read.");
            sb.AppendLine("- Не выдумывай содержимое файлов: сначала посмотри, потом отвечай.");
            sb.AppendLine("- Отвечай кратко и по делу, на языке пользователя.");
            sb.AppendLine("- Когда задача выполнена, напиши короткий итог без лишних подробностей.");
            return sb.ToString();
        }
    }
}
