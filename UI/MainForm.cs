using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using NovaCode.Core;

namespace NovaCode.UI
{
    public sealed class MainForm : Form
    {
        private Config _config;
        private readonly MockLlmServer _mock;
        private Session _session;
        private Agent _agent;
        private Thread _agentThread;

        private Panel _header;
        private Panel _sidebar;
        private Panel _sidebarHeader;
        private ListBox _sessionList;
        private FlatButton _newSessionButton;
        private FlatButton _settingsButton;
        private FlatButton _toggleSidebarButton;
        private Label _brand;
        private Label _brandSub;

        private Panel _center;
        private Transcript _transcript;

        private Panel _inputPanel;
        private BorderedPanel _inputCard;
        private TextBox _input;
        private FlatButton _sendButton;
        private Label _hint;

        private Panel _statusBar;
        private Label _statusState;
        private Label _statusModel;
        private Label _statusTokens;
        private Label _statusWorkspace;

        private bool _busy;
        private bool _assistantStarted;
        private bool _loadingList;
        private bool _sidebarVisible = true;

        public MainForm() : this(null, null) { }

        /// <summary>
        /// config == null — читаем настройки из %APPDATA%. mock != null — демо-режим:
        /// в стенограмму добавляется пояснение, а настройки не пишутся на диск.
        /// </summary>
        public MainForm(Config config, MockLlmServer mock)
        {
            _mock = mock;
            _config = config ?? Config.Load();
            Theme.Init(_config.FontSize);
            LlmClient.PrepareTls(_config.InsecureTls);

            BuildUi();
            if (_mock != null) Text = "NovaCode — демо-режим (Windows XP)";
            ReloadSessions();
            ShowStartupNotice();
        }

        // ============================================================ UI

        private void BuildUi()
        {
            SuspendLayout();

            Text = "NovaCode — агент для Windows XP";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1140, 760);
            MinimumSize = new Size(760, 520);
            DoubleBuffered = true;
            KeyPreview = true;

            // ---- центральная область (добавляем первой: Dock=Fill считается последним)
            _center = new Panel();
            _center.Dock = DockStyle.Fill;
            _center.BackColor = Theme.Bg;
            _center.Padding = new Padding(18, 10, 8, 6);

            _transcript = new Transcript();
            _transcript.Dock = DockStyle.Fill;
            _transcript.TextChanged += delegate { };
            _center.Controls.Add(_transcript);

            // ---- боковая панель
            _sidebar = new Panel();
            _sidebar.Dock = DockStyle.Left;
            _sidebar.Width = 214;
            _sidebar.BackColor = Theme.Panel;

            _sidebarHeader = new Panel();
            _sidebarHeader.Dock = DockStyle.Top;
            _sidebarHeader.Height = 32;
            _sidebarHeader.BackColor = Theme.Panel;
            _sidebarHeader.Paint += delegate (object s, PaintEventArgs e)
            {
                using (var brush = new SolidBrush(Theme.Faint))
                    e.Graphics.DrawString("СЕССИИ", Theme.UiSmall, brush, 14, 10);
                using (var pen = new Pen(Theme.PanelAlt))
                    e.Graphics.DrawLine(pen, 12, _sidebarHeader.Height - 1, _sidebar.Width - 12, _sidebarHeader.Height - 1);
            };

            _newSessionButton = new FlatButton();
            _newSessionButton.Text = "＋  Новая сессия";
            _newSessionButton.Dock = DockStyle.Bottom;
            _newSessionButton.Height = 30;
            _newSessionButton.Margin = new Padding(0);
            _newSessionButton.ForeColor = Theme.Text;
            _newSessionButton.Click += delegate { NewSession(); };

            _sessionList = new ListBox();
            _sessionList.Dock = DockStyle.Fill;
            _sessionList.BorderStyle = BorderStyle.None;
            _sessionList.BackColor = Theme.Panel;
            _sessionList.ForeColor = Theme.Text;
            _sessionList.DrawMode = DrawMode.OwnerDrawFixed;
            _sessionList.ItemHeight = 44;
            _sessionList.IntegralHeight = false;
            _sessionList.DrawItem += DrawSessionItem;
            _sessionList.SelectedIndexChanged += OnSessionSelected;
            _sessionList.KeyDown += OnSessionListKeyDown;

            var menu = new ContextMenuStrip();
            menu.BackColor = Theme.PanelAlt;
            menu.ForeColor = Theme.Text;
            menu.Items.Add("Переименовать", null, delegate { RenameSession(); });
            menu.Items.Add("Удалить", null, delegate { DeleteSession(); });
            _sessionList.ContextMenuStrip = menu;

            _sidebar.Controls.Add(_sessionList);
            _sidebar.Controls.Add(_newSessionButton);
            _sidebar.Controls.Add(_sidebarHeader);

            // ---- нижняя панель ввода
            _inputPanel = new Panel();
            _inputPanel.Dock = DockStyle.Bottom;
            _inputPanel.Height = 112;
            _inputPanel.BackColor = Theme.Bg;
            _inputPanel.Padding = new Padding(18, 6, 18, 10);

            _inputCard = new BorderedPanel();
            _inputCard.Dock = DockStyle.Fill;
            _inputCard.Padding = new Padding(10, 8, 10, 6);

            _input = new TextBox();
            _input.Multiline = true;
            _input.BorderStyle = BorderStyle.None;
            _input.BackColor = Theme.InputBg;
            _input.ForeColor = Theme.Text;
            _input.Font = Theme.Mono;
            _input.Dock = DockStyle.Fill;
            _input.ScrollBars = ScrollBars.Vertical;
            _input.KeyDown += OnInputKeyDown;
            _input.TextChanged += delegate { UpdateHint(); };

            var inputFooter = new Panel();
            inputFooter.Dock = DockStyle.Bottom;
            inputFooter.Height = 26;
            inputFooter.BackColor = Theme.InputBg;

            _hint = new Label();
            _hint.Dock = DockStyle.Fill;
            _hint.ForeColor = Theme.Faint;
            _hint.Font = Theme.UiSmall;
            _hint.TextAlign = ContentAlignment.MiddleLeft;
            _hint.Padding = new Padding(2, 0, 0, 0);
            _hint.Text = "";

            _sendButton = new FlatButton();
            _sendButton.Text = "Отправить";
            _sendButton.Primary = true;
            _sendButton.Dock = DockStyle.Right;
            _sendButton.Width = 104;
            _sendButton.Height = 24;
            _sendButton.Click += delegate { OnSendClicked(); };

            inputFooter.Controls.Add(_hint);
            inputFooter.Controls.Add(_sendButton);

            _inputCard.Controls.Add(_input);
            _inputCard.Controls.Add(inputFooter);
            _inputPanel.Controls.Add(_inputCard);

            // ---- статус-бар
            _statusBar = new Panel();
            _statusBar.Dock = DockStyle.Bottom;
            _statusBar.Height = 24;
            _statusBar.BackColor = Theme.Panel;

            _statusState = MakeStatusLabel("готова", Theme.Success, 168);
            _statusModel = MakeStatusLabel("", Theme.Dim, 150);
            _statusTokens = MakeStatusLabel("", Theme.Dim, 210);
            _statusWorkspace = MakeStatusLabel("", Theme.Faint, 0);
            _statusWorkspace.Dock = DockStyle.Fill;
            _statusWorkspace.TextAlign = ContentAlignment.MiddleRight;
            _statusWorkspace.Padding = new Padding(0, 0, 12, 0);

            _statusBar.Controls.Add(_statusWorkspace);
            _statusBar.Controls.Add(_statusTokens);
            _statusBar.Controls.Add(_statusModel);
            _statusBar.Controls.Add(_statusState);

            // ---- шапка
            _header = new Panel();
            _header.Dock = DockStyle.Top;
            _header.Height = 48;
            _header.BackColor = Theme.Panel;
            _header.Paint += delegate (object s, PaintEventArgs e)
            {
                using (var pen = new Pen(Theme.PanelAlt))
                    e.Graphics.DrawLine(pen, 0, _header.Height - 1, _header.Width, _header.Height - 1);
            };

            _brand = new Label();
            _brand.Text = "NovaCode";
            _brand.Font = Theme.Title;
            _brand.ForeColor = Theme.Text;
            _brand.AutoSize = false;
            _brand.Location = new Point(16, 6);
            _brand.Size = new Size(220, 20);

            _brandSub = new Label();
            _brandSub.Text = "";
            _brandSub.Font = Theme.UiSmall;
            _brandSub.ForeColor = Theme.Faint;
            _brandSub.AutoSize = false;
            _brandSub.Location = new Point(18, 27);
            _brandSub.Size = new Size(560, 16);

            _settingsButton = new FlatButton();
            _settingsButton.Text = "Настройки";
            _settingsButton.Width = 92;
            _settingsButton.Height = 26;
            _settingsButton.Click += delegate { OpenSettings(); };

            _toggleSidebarButton = new FlatButton();
            _toggleSidebarButton.Text = "◧";
            _toggleSidebarButton.Width = 32;
            _toggleSidebarButton.Height = 26;
            _toggleSidebarButton.Click += delegate { ToggleSidebar(); };

            _header.Controls.Add(_brand);
            _header.Controls.Add(_brandSub);
            _header.Controls.Add(_settingsButton);
            _header.Controls.Add(_toggleSidebarButton);
            _header.Resize += delegate { LayoutHeader(); };

            Controls.Add(_center);
            Controls.Add(_sidebar);
            Controls.Add(_inputPanel);
            Controls.Add(_statusBar);
            Controls.Add(_header);

            KeyDown += OnFormKeyDown;
            ResumeLayout();

            LayoutHeader();
            UpdateHint();
            UpdateStatusBar();
        }

        private Label MakeStatusLabel(string text, Color color, int width)
        {
            var label = new Label();
            label.Text = text;
            label.ForeColor = color;
            label.Font = Theme.UiSmall;
            label.AutoSize = false;
            label.Dock = DockStyle.Left;
            label.Width = width;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Padding = new Padding(12, 0, 0, 0);
            return label;
        }

        private void LayoutHeader()
        {
            if (_settingsButton == null) return;
            int right = _header.Width - 12;
            _settingsButton.Location = new Point(right - _settingsButton.Width, 11);
            right -= _settingsButton.Width + 6;
            _toggleSidebarButton.Location = new Point(right - _toggleSidebarButton.Width, 11);
        }

        // ============================================================ сессии

        private void ReloadSessions()
        {
            _loadingList = true;
            try
            {
                _sessionList.Items.Clear();
                foreach (Session s in Session.LoadAll()) _sessionList.Items.Add(s);

                if (_session != null)
                {
                    for (int i = 0; i < _sessionList.Items.Count; i++)
                    {
                        var item = _sessionList.Items[i] as Session;
                        if (item != null && item.Id == _session.Id) { _sessionList.SelectedIndex = i; break; }
                    }
                }
            }
            finally
            {
                _loadingList = false;
            }
        }

        private void DrawSessionItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var session = _sessionList.Items[e.Index] as Session;
            if (session == null) return;

            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color back = selected ? Theme.PanelAlt : Theme.Panel;

            using (var brush = new SolidBrush(back))
                e.Graphics.FillRectangle(brush, e.Bounds);

            if (selected)
            {
                using (var accent = new SolidBrush(Theme.Accent))
                    e.Graphics.FillRectangle(accent, e.Bounds.X, e.Bounds.Y + 6, 2, e.Bounds.Height - 12);
            }

            var titleRect = new Rectangle(e.Bounds.X + 14, e.Bounds.Y + 7, e.Bounds.Width - 24, 18);
            var dateRect = new Rectangle(e.Bounds.X + 14, e.Bounds.Y + 25, e.Bounds.Width - 24, 14);

            TextRenderer.DrawText(e.Graphics, Ellipsis(session.Title, 26), Theme.UiBold, titleRect,
                selected ? Theme.Text : Color.FromArgb(200, 200, 206),
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            TextRenderer.DrawText(e.Graphics, session.Updated.ToString("dd.MM HH:mm"), Theme.UiSmall, dateRect,
                Theme.Faint, TextFormatFlags.NoPrefix);
        }

        private static string Ellipsis(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "Сессия";
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        private void OnSessionSelected(object sender, EventArgs e)
        {
            if (_loadingList) return;
            if (_busy) { Status("сначала остановите текущий запрос"); return; }

            var session = _sessionList.SelectedItem as Session;
            if (session == null) return;

            _session = session;
            _transcript.RenderSession(_session);
            UpdateStatusBar();
        }

        private void OnSessionListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete) { DeleteSession(); e.Handled = true; }
            else if (e.KeyCode == Keys.F2) { RenameSession(); e.Handled = true; }
        }

        private void RenameSession()
        {
            var session = _sessionList.SelectedItem as Session;
            if (session == null) return;

            string name = InputBox.Ask(this, "Переименовать сессию", "Название:", session.Title);
            if (name == null) return;
            session.Title = name.Trim().Length == 0 ? session.Title : name.Trim();
            session.Touch();
            session.Save();
            ReloadSessions();
            _sessionList.Invalidate();
        }

        private void DeleteSession()
        {
            var session = _sessionList.SelectedItem as Session;
            if (session == null) return;

            var answer = MessageBox.Show(this, "Удалить сессию «" + Ellipsis(session.Title, 40) + "»?",
                "NovaCode", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            if (_busy) { _agent.Cancel(); WaitForAgent(3000); }

            session.Delete();
            if (_session != null && _session.Id == session.Id)
            {
                _session = null;
                _transcript.Clear();
            }
            ReloadSessions();
        }

        private void NewSession()
        {
            if (_busy) { _agent.Cancel(); WaitForAgent(3000); }

            var session = Session.Create(_config.Model);
            session.Save();
            _session = session;
            _transcript.Clear();
            ReloadSessions();
            UpdateStatusBar();
            _input.Focus();
        }

        // ============================================================ отправка

        private void OnSendClicked()
        {
            if (_busy) { StopAgent(); return; }
            SendPrompt();
        }

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                if (_busy) StopAgent();
                else SendPrompt();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                if (_busy) StopAgent();
            }
        }

        private void SendPrompt()
        {
            string text = _input.Text.Trim();
            if (text.Length == 0) return;

            if (text.StartsWith("/", StringComparison.Ordinal))
            {
                _input.Clear();
                HandleCommand(text);
                return;
            }

            if (string.IsNullOrEmpty(_config.ApiKey) && !_config.BaseUrl.Contains("localhost"))
            {
                Status("нужен API-ключ");
                OpenSettings();
                return;
            }

            _input.Clear();
            if (_session == null) NewSession();

            _transcript.AppendUser(text, Util.ShortTime());
            _transcript.StickToBottom();
            StartAgent(text);
        }

        private void StartAgent(string text)
        {
            if (_session == null) NewSession();

            _agent = new Agent(_config, _session);
            _agent.Event += OnAgentEvent;
            _assistantStarted = false;
            SetBusy(true);
            Status("думает…");

            var sessionSnapshot = _session;
            _agentThread = new Thread(delegate ()
            {
                try { _agent.Run(text); }
                catch (Exception ex) { Log.Error("Поток агента", ex); }
                finally { SafeInvoke(delegate { FinishTurn(sessionSnapshot); }); }
            });
            _agentThread.IsBackground = true;
            _agentThread.Name = "novacode-agent";
            _agentThread.Start();
        }

        private void StopAgent()
        {
            if (_agent == null) return;
            _agent.Cancel();
            Status("останавливаю…");
        }

        private void WaitForAgent(int milliseconds)
        {
            Thread thread = _agentThread;
            if (thread == null) return;
            try { thread.Join(milliseconds); }
            catch { }
        }

        private void FinishTurn(Session finished)
        {
            SetBusy(false);
            if (finished != null && _session != null && finished.Id == _session.Id)
            {
                _transcript.RenderSession(_session);
                _transcript.StickToBottom();
            }
            UpdateStatusBar();
            _sessionList.Invalidate();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _sendButton.Text = busy ? "Стоп" : "Отправить";
            _sendButton.Primary = !busy;
            _sendButton.Invalidate();
            UpdateStatusBar();
        }

        // ============================================================ события агента

        private void OnAgentEvent(AgentEvent e)
        {
            SafeInvoke(delegate { HandleAgentEvent(e); });
        }

        private void SafeInvoke(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch (Exception ex)
            {
                Log.Write("UI-маршалинг: " + ex.Message);
            }
        }

        private void HandleAgentEvent(AgentEvent e)
        {
            switch (e.Kind)
            {
                case AgentEventKind.Status:
                    Status(e.Text);
                    break;

                case AgentEventKind.TextDelta:
                    if (!_assistantStarted)
                    {
                        _assistantStarted = true;
                        _transcript.BeginAssistant();
                    }
                    _transcript.AppendAssistantDelta(e.Text);
                    break;

                case AgentEventKind.ReasoningDelta:
                    if (!_assistantStarted)
                    {
                        _assistantStarted = true;
                        _transcript.BeginAssistant();
                    }
                    _transcript.AppendReasoningDelta(e.Text);
                    break;

                case AgentEventKind.ToolStarted:
                    _assistantStarted = false;
                    _transcript.AppendToolStart(e.Call, ToolSubtitleOf(e.Call));
                    break;

                case AgentEventKind.ToolFinished:
                    _transcript.AppendToolResult(e.Call, e.Result);
                    break;

                case AgentEventKind.Error:
                    _transcript.AppendError(e.Text);
                    Status("ошибка");
                    break;

                case AgentEventKind.Usage:
                    UpdateTokenLabel(e.PromptTokens, e.CompletionTokens);
                    break;

                case AgentEventKind.TurnDone:
                    _assistantStarted = false;
                    break;
            }
        }

        private static string ToolSubtitleOf(ToolCall call)
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

        // ============================================================ команды

        private void HandleCommand(string text)
        {
            string[] parts = text.Split(new[] { ' ' }, 2);
            string command = parts[0].ToLowerInvariant();
            string argument = parts.Length > 1 ? parts[1].Trim() : "";

            switch (command)
            {
                case "/new":
                    NewSession();
                    break;

                case "/clear":
                    if (_session != null) { _session.Messages.Clear(); _session.Save(); }
                    _transcript.Clear();
                    break;

                case "/model":
                    if (argument.Length == 0)
                    {
                        _transcript.AppendNotice("Текущая модель: " + _config.Model + ". Использование: /model <id>");
                    }
                    else
                    {
                        _config.Model = argument;
                        _config.Save();
                        _transcript.AppendNotice("Модель: " + argument);
                        UpdateStatusBar();
                    }
                    break;

                case "/settings":
                    OpenSettings();
                    break;

                case "/help":
                    _transcript.AppendNotice(
                        "/new — новая сессия · /clear — очистить историю · /model <id> — сменить модель · " +
                        "/settings — настройки · Enter — отправить · Shift+Enter — новая строка · Esc — остановить");
                    break;

                default:
                    _transcript.AppendNotice("Неизвестная команда: " + command + ". Введите /help.");
                    break;
            }
        }

        // ============================================================ прочее UI

        private void ToggleSidebar()
        {
            _sidebarVisible = !_sidebarVisible;
            _sidebar.Visible = _sidebarVisible;
        }

        private void OpenSettings()
        {
            using (var form = new SettingsForm(_config.Clone()))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;

                _config = form.Config;
                if (_mock == null) _config.Save();   // в демо не засоряем настоящий config.json
                Theme.Init(_config.FontSize);
                LlmClient.PrepareTls(_config.InsecureTls);

                ApplyTheme();
                UpdateStatusBar();
                _transcript.RenderSession(_session);
            }
        }

        private void ApplyTheme()
        {
            BackColor = Theme.Bg;
            _transcript.BackColor = Theme.Bg;
            _transcript.Font = Theme.Mono;
            _input.BackColor = Theme.InputBg;
            _input.ForeColor = Theme.Text;
            _input.Font = Theme.Mono;
            _hint.ForeColor = Theme.Faint;
            _brand.Font = Theme.Title;
            _sessionList.Invalidate();
        }

        private void Status(string text)
        {
            _statusState.Text = "● " + text;
            bool busy = text != "готова" && text != "ошибка" && text != "остановлено";
            _statusState.ForeColor = busy ? Theme.Warn : (text == "ошибка" ? Theme.Error : Theme.Success);
        }

        private void UpdateTokenLabel(long prompt, long completion)
        {
            string cost = "";
            if (_config.PriceIn > 0 || _config.PriceOut > 0)
            {
                double money = (prompt / 1000000.0) * _config.PriceIn + (completion / 1000000.0) * _config.PriceOut;
                cost = " · $" + money.ToString("0.0000", CultureInfo.InvariantCulture);
            }
            _statusTokens.Text = "↑ " + prompt.ToString("N0", CultureInfo.InvariantCulture)
                               + "  ↓ " + completion.ToString("N0", CultureInfo.InvariantCulture) + cost;
        }

        private void UpdateStatusBar()
        {
            if (_session != null) UpdateTokenLabel(_session.PromptTokens, _session.CompletionTokens);
            else _statusTokens.Text = "";

            _statusModel.Text = _config.Model;
            _statusWorkspace.Text = _config.Workspace;
            _brandSub.Text = _config.Model + "  ·  " + _config.BaseUrl;
        }

        private void UpdateHint()
        {
            int lines = _input.Lines.Length;
            if (_busy) _hint.Text = "агент работает…  Esc — остановить";
            else if (lines > 3) _hint.Text = lines + " строк · Enter — отправить · Shift+Enter — перенос";
            else _hint.Text = "Enter — отправить · Shift+Enter — перенос · Esc — остановить · F2 — настройки";
        }

        private void ShowStartupNotice()
        {
            // Сначала восстанавливаем прошлую сессию, потом пишем приветствие:
            // RenderSession очищает стенограмму, и подсказки не должны пропадать.
            var sessions = Session.LoadAll();
            if (sessions.Count > 0 && _session == null)
            {
                _session = sessions[0];
                _transcript.RenderSession(_session);
            }

            if (_mock != null)
            {
                _transcript.AppendNotice("Демо-режим: модель не вызывается, ответы заготовлены заранее. Инструменты при этом работают по-настоящему.");
                _transcript.AppendNotice("Рабочая папка демо: " + _config.Workspace);
            }
            else if (string.IsNullOrEmpty(_config.ApiKey) && !_config.BaseUrl.Contains("localhost"))
            {
                _transcript.AppendNotice("Ключ API не задан. Нажмите F2 и заполните адрес сервера и ключ.");
                _transcript.AppendNotice("Подходит любой [OI]-совместимый сервер: DeepSeek, OpenRouter, Groq, llama.cpp, LM Studio.");
            }
            else
            {
                _transcript.AppendNotice("Готова. Модель: " + _config.Model + " · рабочая папка: " + _config.Workspace);
            }

            _transcript.StickToBottom();
        }

        // ============================================================ клавиши формы

        private void OnFormKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F2) { OpenSettings(); e.Handled = true; }
            else if (e.KeyCode == Keys.F9) { ToggleSidebar(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.N) { NewSession(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape && _busy) { StopAgent(); e.Handled = true; }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy && _agent != null)
            {
                _agent.Cancel();
                WaitForAgent(1500);
            }
            if (_session != null) _session.Save();
            base.OnFormClosing(e);
        }
    }

    /// <summary>Крошечный диалог ввода: на XP InputBox из VB не хочется тащить.</summary>
    public static class InputBox
    {
        public static string Ask(IWin32Window owner, string title, string label, string initial)
        {
            using (var form = new Form())
            {
                form.Text = title;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.StartPosition = FormStartPosition.CenterParent;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.ClientSize = new Size(400, 110);
                form.BackColor = Theme.Panel;
                form.ForeColor = Theme.Text;
                form.Font = Theme.Ui;

                var caption = new Label();
                caption.Text = label;
                caption.ForeColor = Theme.Dim;
                caption.Location = new Point(12, 12);
                caption.Size = new Size(370, 18);

                var box = new TextBox();
                box.Text = initial;
                box.Location = new Point(12, 34);
                box.Size = new Size(376, 22);
                box.BackColor = Theme.InputBg;
                box.ForeColor = Theme.Text;
                box.BorderStyle = BorderStyle.FixedSingle;

                var ok = new Button();
                ok.Text = "ОК";
                ok.DialogResult = DialogResult.OK;
                ok.Location = new Point(232, 68);
                ok.Size = new Size(76, 26);

                var cancel = new Button();
                cancel.Text = "Отмена";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Location = new Point(312, 68);
                cancel.Size = new Size(76, 26);

                form.Controls.Add(caption);
                form.Controls.Add(box);
                form.Controls.Add(ok);
                form.Controls.Add(cancel);
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                return form.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
            }
        }
    }
}
