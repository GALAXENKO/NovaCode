using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using NovaCode.Core;

namespace NovaCode.UI
{
    /// <summary>
    /// Лента диалога. RichTextBox даёт бесплатно выделение, копирование и прокрутку —
    /// на XP это RichEdit, а значит всё работает без своих движков отрисовки.
    /// </summary>
    public sealed class Transcript : RichTextBox
    {
        private const int MaxToolLines = 16;
        private bool _autoScroll = true;

        public Transcript()
        {
            ReadOnly = true;
            BorderStyle = BorderStyle.None;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Mono;
            WordWrap = true;
            ScrollBars = RichTextBoxScrollBars.Vertical;
            DetectUrls = false;
            HideSelection = false;
            Cursor = Cursors.IBeam;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        }

        // ------------------------------------------------------------ прокрутка

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (e.Delta > 0) _autoScroll = false;
            else if (IsNearBottom()) _autoScroll = true;
        }

        private bool IsNearBottom()
        {
            try
            {
                if (TextLength == 0) return true;
                Point bottom = new Point(ClientSize.Width - 4, ClientSize.Height - 4);
                int index = GetCharIndexFromPosition(bottom);
                return index >= TextLength - 8;
            }
            catch { return true; }
        }

        private void ScrollIfNeeded()
        {
            if (!_autoScroll) return;
            try
            {
                SelectionStart = TextLength;
                SelectionLength = 0;
                ScrollToCaret();
            }
            catch { }
        }

        public void StickToBottom()
        {
            _autoScroll = true;
            ScrollIfNeeded();
        }

        // ------------------------------------------------------------ низкоуровневая запись

        private void Write(string text, Color color, Font font, Color back, bool underline)
        {
            if (string.IsNullOrEmpty(text)) return;

            int start = TextLength;
            SelectionStart = start;
            SelectionLength = 0;
            SelectionColor = color;
            SelectionBackColor = back;
            if (font != null) SelectionFont = font;
            SelectedText = text;

            if (underline)
            {
                SelectionStart = start;
                SelectionLength = text.Length;
                Font current = SelectionFont;
                if (current != null)
                    SelectionFont = new Font(current, current.Style | FontStyle.Underline);
            }

            SelectionStart = TextLength;
            SelectionLength = 0;
        }

        public new void Clear()
        {
            base.Clear();
            _autoScroll = true;
        }

        private void NewLine()
        {
            Write("\n", Theme.Text, Theme.Mono, Theme.Bg, false);
        }

        private void Blank()
        {
            Write("\n", Theme.Text, Theme.Mono, Theme.Bg, false);
        }

        // ------------------------------------------------------------ блоки

        public void AppendUser(string text, string time)
        {
            EnsureGap();
            Write("ВЫ", Theme.Accent, Theme.UiBold, Theme.Bg, false);
            if (!string.IsNullOrEmpty(time))
                Write("  " + time, Theme.Faint, Theme.UiSmall, Theme.Bg, false);
            NewLine();
            AppendPlain(text, Theme.Text, Theme.Mono, 2);
            Blank();
            ScrollIfNeeded();
        }

        public void BeginAssistant()
        {
            EnsureGap();
            Write("АГЕНТ", Theme.Success, Theme.UiBold, Theme.Bg, false);
            NewLine();
            ScrollIfNeeded();
        }

        public void AppendAssistantDelta(string delta)
        {
            AppendPlain(delta, Theme.Assistant, Theme.Mono, 2);
            ScrollIfNeeded();
        }

        public void AppendReasoningDelta(string delta)
        {
            AppendPlain(delta, Theme.Faint, Theme.MonoSelf, 2);
            ScrollIfNeeded();
        }

        public void AppendToolStart(ToolCall call, string subtitle)
        {
            NewLine();
            Write("  ✓ ", Theme.Faint, Theme.UiBold, Theme.Bg, false);
            Write(call.Name, Theme.Tool, Theme.MonoBold, Theme.Bg, false);
            if (!string.IsNullOrEmpty(subtitle))
                Write("  " + subtitle, Theme.Dim, Theme.MonoSelf, Theme.Bg, false);
            Write("   …", Theme.Faint, Theme.MonoSelf, Theme.Bg, false);
            NewLine();
            ScrollIfNeeded();
        }

        public void AppendToolResult(ToolCall call, ToolResult result)
        {
            // Стираем «…» у последней строки — заменяем её маркером результата.
            NewLine();
            Write("  " + (result.IsError ? "× " : "✓ "), result.IsError ? Theme.Error : Theme.Success,
                  Theme.UiBold, Theme.Bg, false);
            Write(call.Name, Theme.Tool, Theme.MonoBold, Theme.Bg, false);
            if (!string.IsNullOrEmpty(result.Subtitle))
                Write("  " + result.Subtitle, Theme.Dim, Theme.MonoSelf, Theme.Bg, false);
            NewLine();

            string output = result.Output ?? "";
            if (output.Length > 0)
                AppendToolOutput(output, result.IsError);
            ScrollIfNeeded();
        }

        private void AppendToolOutput(string output, bool isError)
        {
            string[] lines = Util.NormalizeNewlines(output).Split('\n');
            int limit = Math.Min(lines.Length, MaxToolLines);

            for (int i = 0; i < limit; i++)
            {
                Write("    ", Theme.Faint, Theme.MonoSelf, Theme.ToolBg, false);
                Write(lines[i], isError ? Theme.Error : Theme.Dim, Theme.MonoSelf, Theme.ToolBg, false);
                NewLine();
            }
            if (lines.Length > limit)
            {
                Write("    ", Theme.Faint, Theme.MonoSelf, Theme.ToolBg, false);
                Write("… ещё " + (lines.Length - limit) + " строк", Theme.Faint, Theme.MonoSelf, Theme.ToolBg, false);
                NewLine();
            }
        }

        public void AppendError(string text)
        {
            NewLine();
            Write("  ! ", Theme.Error, Theme.UiBold, Theme.Bg, false);
            Write(text, Theme.Error, Theme.Mono, Theme.Bg, false);
            NewLine();
            ScrollIfNeeded();
        }

        public void AppendNotice(string text)
        {
            NewLine();
            Write("  · " + text, Theme.Faint, Theme.MonoSelf, Theme.Bg, false);
            NewLine();
            ScrollIfNeeded();
        }

        private void EnsureGap()
        {
            if (TextLength > 0) Blank();
        }

        // ------------------------------------------------------------ текст с отступом

        private void AppendPlain(string text, Color color, Font font, int indent)
        {
            if (string.IsNullOrEmpty(text)) return;
            string pad = new string(' ', indent);
            string[] lines = Util.NormalizeNewlines(text).Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length > 0) Write(pad, color, font, Theme.Bg, false);
                Write(lines[i], color, font, Theme.Bg, false);
                if (i < lines.Length - 1) NewLine();
            }
        }

        // ------------------------------------------------------------ markdown

        /// <summary>Финальная отрисовка ответа: заголовки, списки, `код`, ```блоки```.</summary>
        public void AppendMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            string[] lines = Util.NormalizeNewlines(text).Split('\n');
            bool inCode = false;
            bool first = true;

            foreach (string raw in lines)
            {
                string line = raw ?? "";

                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inCode = !inCode;
                    continue;
                }

                if (inCode)
                {
                    Write("    ", Theme.Faint, Theme.MonoSelf, Theme.CodeBg, false);
                    Write(line, Color.FromArgb(200, 200, 210), Theme.MonoSelf, Theme.CodeBg, false);
                    NewLine();
                    first = false;
                    continue;
                }

                if (!first) NewLine();
                first = false;

                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("### ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("## ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("# ", StringComparison.Ordinal))
                {
                    string title = trimmed.TrimStart('#').Trim();
                    Write("  ", Theme.Text, Theme.Mono, Theme.Bg, false);
                    Write(title, Theme.Text, Theme.MonoBold, Theme.Bg, false);
                    continue;
                }

                if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
                {
                    Write("  ", Theme.Text, Theme.Mono, Theme.Bg, false);
                    Write("• ", Theme.Accent, Theme.Mono, Theme.Bg, false);
                    AppendInline(trimmed.Substring(2), Theme.Mono, Theme.Assistant);
                    continue;
                }

                Write("  ", Theme.Text, Theme.Mono, Theme.Bg, false);
                AppendInline(line, Theme.Mono, Theme.Assistant);
            }
            ScrollIfNeeded();
        }

        /// <summary>Разбирает **жирный** и `код` внутри строки.</summary>
        private void AppendInline(string line, Font font, Color color)
        {
            int i = 0;
            while (i < line.Length)
            {
                int bold = line.IndexOf("**", i, StringComparison.Ordinal);
                int code = line.IndexOf('`', i);

                if (code >= 0 && (bold < 0 || code < bold))
                {
                    if (code > i) Write(line.Substring(i, code - i), color, font, Theme.Bg, false);
                    int close = line.IndexOf('`', code + 1);
                    if (close < 0)
                    {
                        Write(line.Substring(code), color, font, Theme.Bg, false);
                        return;
                    }
                    Write(line.Substring(code + 1, close - code - 1), Theme.Warn, Theme.MonoSelf, Theme.CodeBg, false);
                    i = close + 1;
                    continue;
                }

                if (bold >= 0)
                {
                    if (bold > i) Write(line.Substring(i, bold - i), color, font, Theme.Bg, false);
                    int close = line.IndexOf("**", bold + 2, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        Write(line.Substring(bold), color, font, Theme.Bg, false);
                        return;
                    }
                    Write(line.Substring(bold + 2, close - bold - 2), color, Theme.MonoBold, Theme.Bg, false);
                    i = close + 2;
                    continue;
                }

                Write(line.Substring(i), color, font, Theme.Bg, false);
                return;
            }
        }

        // ------------------------------------------------------------ полная перерисовка

        public void RenderSession(Session session)
        {
            Clear();
            if (session == null) return;

            var pending = new Dictionary<string, ToolCall>();

            foreach (ChatMessage m in session.Messages)
            {
                if (m.Role == "user")
                {
                    AppendUser(m.Content, m.Time);
                }
                else if (m.Role == "assistant")
                {
                    if (!string.IsNullOrEmpty(m.Content))
                    {
                        BeginAssistant();
                        AppendMarkdown(m.Content);
                        Blank();
                    }
                    if (m.ToolCalls != null)
                    {
                        foreach (ToolCall call in m.ToolCalls)
                        {
                            pending[call.Id] = call;
                            NewLine();
                            Write("  ✓ ", Theme.Success, Theme.UiBold, Theme.Bg, false);
                            Write(call.Name, Theme.Tool, Theme.MonoBold, Theme.Bg, false);
                            string sub = ToolSubtitle(call);
                            if (sub.Length > 0) Write("  " + sub, Theme.Dim, Theme.MonoSelf, Theme.Bg, false);
                            NewLine();
                        }
                    }
                }
                else if (m.Role == "tool")
                {
                    ToolCall call;
                    if (!pending.TryGetValue(m.ToolCallId ?? "", out call))
                        call = new ToolCall { Name = m.ToolName };

                    var res = new ToolResult { Output = m.Content, IsError = m.ToolError };
                    if (m.ToolError)
                    {
                        NewLine();
                        Write("  × ", Theme.Error, Theme.UiBold, Theme.Bg, false);
                        Write(call.Name, Theme.Tool, Theme.MonoBold, Theme.Bg, false);
                        NewLine();
                    }
                    AppendToolOutput(m.Content ?? "", m.ToolError);
                }
            }

            StickToBottom();
        }

        private static string ToolSubtitle(ToolCall call)
        {
            try
            {
                var args = Json.AsObject(Json.Parse(string.IsNullOrEmpty(call.Arguments) ? "{}" : call.Arguments));
                if (args == null) return "";
                switch (call.Name)
                {
                    case "bash": return Util.Quote(Json.Str(args, "command", ""), 80);
                    case "webfetch": return Json.Str(args, "url", "");
                    case "glob": case "grep": return Json.Str(args, "pattern", "");
                    default: return Json.Str(args, "path", "");
                }
            }
            catch { return ""; }
        }
    }
}
