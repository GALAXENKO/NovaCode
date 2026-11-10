using System;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using NovaCode.Core;

namespace NovaCode.UI
{
    /// <summary>Настройки: подключение к API, параметры агента, вид.</summary>
    public sealed class SettingsForm : Form
    {
        private readonly Config _config;

        private TextBox _baseUrl;
        private TextBox _apiKey;
        private TextBox _model;
        private FlatButton _testButton;
        private Label _testResult;

        private TextBox _workspace;
        private NumericUpDown _maxSteps;
        private NumericUpDown _maxTokens;
        private NumericUpDown _temperature;
        private TextBox _systemPrompt;

        private NumericUpDown _fontSize;
        private CheckBox _insecureTls;
        private NumericUpDown _priceIn;
        private NumericUpDown _priceOut;

        public Config Config { get { return _config; } }

        public SettingsForm(Config config)
        {
            _config = config;
            BuildUi();
            LoadValues();
        }

        // ------------------------------------------------------------ UI

        private void BuildUi()
        {
            Text = "Настройки — NovaCode";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(520, 452);
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
            Font = Theme.Ui;

            var tabs = new TabControl();
            tabs.Location = new Point(10, 10);
            tabs.Size = new Size(500, 396);

            tabs.TabPages.Add(BuildConnectionTab());
            tabs.TabPages.Add(BuildAgentTab());
            tabs.TabPages.Add(BuildAppearanceTab());

            var ok = new Button();
            ok.Text = "Сохранить";
            ok.DialogResult = DialogResult.OK;
            ok.Location = new Point(336, 416);
            ok.Size = new Size(84, 26);
            ok.Click += delegate { ApplyValues(); };

            var cancel = new Button();
            cancel.Text = "Отмена";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(426, 416);
            cancel.Size = new Size(84, 26);

            Controls.Add(tabs);
            Controls.Add(ok);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private TabPage BuildConnectionTab()
        {
            var page = MakePage("Подключение");

            page.Controls.Add(MakeLabel("Адрес API (base URL)", 16));
            _baseUrl = MakeTextBox(36, false);
            page.Controls.Add(_baseUrl);

            page.Controls.Add(MakeLabel("Ключ API", 68));
            _apiKey = MakeTextBox(88, true);
            page.Controls.Add(_apiKey);

            page.Controls.Add(MakeLabel("Модель", 120));
            _model = MakeTextBox(140, false);
            page.Controls.Add(_model);

            _testButton = new FlatButton();
            _testButton.Text = "Проверить соединение";
            _testButton.Location = new Point(16, 176);
            _testButton.Size = new Size(150, 26);
            _testButton.Click += delegate { TestConnection(); };
            page.Controls.Add(_testButton);

            _testResult = new Label();
            _testResult.Location = new Point(176, 178);
            _testResult.Size = new Size(300, 44);
            _testResult.ForeColor = Theme.Dim;
            _testResult.Font = Theme.UiSmall;
            page.Controls.Add(_testResult);

            var note = MakeNote(
                "Подходит любой сервер с [OI]-совместимым API: DeepSeek, OpenRouter, Groq, Together,\n" +
                "а также локальные llama.cpp, LM Studio и Ollama. Адрес указывается без /chat/completions.",
                232);
            page.Controls.Add(note);

            return page;
        }

        private TabPage BuildAgentTab()
        {
            var page = MakePage("Агент");

            page.Controls.Add(MakeLabel("Рабочая папка проекта", 16));
            _workspace = MakeTextBox(36, false);
            _workspace.Width = 372;
            page.Controls.Add(_workspace);

            var browse = new FlatButton();
            browse.Text = "Обзор…";
            browse.Location = new Point(392, 35);
            browse.Size = new Size(84, 24);
            browse.Click += delegate { BrowseWorkspace(); };
            page.Controls.Add(browse);

            page.Controls.Add(MakeLabel("Лимит шагов агента", 68));
            _maxSteps = MakeNumber(88, 1, 200, 1, 0);
            page.Controls.Add(_maxSteps);

            page.Controls.Add(MakeLabel("Максимум токенов в ответе", 68, 220));
            _maxTokens = MakeNumber(88, 256, 200000, 256, 0);
            _maxTokens.Left = 220;
            page.Controls.Add(_maxTokens);

            page.Controls.Add(MakeLabel("Температура", 120));
            _temperature = MakeNumber(140, 0m, 2m, 0.1m, 2);
            page.Controls.Add(_temperature);

            page.Controls.Add(MakeLabel("Системный промпт (пусто — встроенный)", 172));
            _systemPrompt = new TextBox();
            _systemPrompt.Location = new Point(16, 192);
            _systemPrompt.Size = new Size(460, 168);
            _systemPrompt.Multiline = true;
            _systemPrompt.ScrollBars = ScrollBars.Vertical;
            _systemPrompt.AcceptsReturn = true;
            _systemPrompt.BackColor = Theme.InputBg;
            _systemPrompt.ForeColor = Theme.Text;
            _systemPrompt.BorderStyle = BorderStyle.FixedSingle;
            _systemPrompt.Font = Theme.MonoSelf;
            page.Controls.Add(_systemPrompt);

            return page;
        }

        private TabPage BuildAppearanceTab()
        {
            var page = MakePage("Вид и сеть");

            page.Controls.Add(MakeLabel("Размер шрифта", 16));
            _fontSize = MakeNumber(36, 8, 16, 1, 0);
            page.Controls.Add(_fontSize);

            _insecureTls = new CheckBox();
            _insecureTls.Text = "Не проверять сертификат TLS (для самоподписанных и локальных прокси)";
            _insecureTls.Location = new Point(16, 72);
            _insecureTls.Size = new Size(460, 20);
            _insecureTls.ForeColor = Theme.Text;
            _insecureTls.FlatStyle = FlatStyle.System;
            page.Controls.Add(_insecureTls);

            page.Controls.Add(MakeLabel("Цена за 1M входных токенов, $", 104));
            _priceIn = MakeNumber(124, 0m, 1000m, 0.01m, 4);
            page.Controls.Add(_priceIn);

            page.Controls.Add(MakeLabel("Цена за 1M выходных токенов, $", 156));
            _priceOut = MakeNumber(176, 0m, 1000m, 0.01m, 4);
            page.Controls.Add(_priceOut);

            page.Controls.Add(MakeNote(
                "Windows XP умеет только TLS 1.0. Если сервер требует TLS 1.2, поставьте рядом\n" +
                "локальный прокси (например, stunnel) и укажите его HTTP-адрес, либо работайте\n" +
                "через сервер в локальной сети.",
                216));

            return page;
        }

        private static TabPage MakePage(string title)
        {
            var page = new TabPage(title);
            page.BackColor = Theme.Panel;
            page.ForeColor = Theme.Text;
            return page;
        }

        private static Label MakeLabel(string text, int top)
        {
            return MakeLabel(text, top, 16);
        }

        private static Label MakeLabel(string text, int top, int left)
        {
            var label = new Label();
            label.Text = text;
            label.Location = new Point(left, top);
            label.Size = new Size(460, 16);
            label.ForeColor = Theme.Dim;
            label.Font = Theme.UiSmall;
            return label;
        }

        private static Label MakeNote(string text, int top)
        {
            var label = new Label();
            label.Text = text;
            label.Location = new Point(16, top);
            label.Size = new Size(460, 70);
            label.ForeColor = Theme.Faint;
            label.Font = Theme.UiSmall;
            return label;
        }

        private static TextBox MakeTextBox(int top, bool password)
        {
            var box = new TextBox();
            box.Location = new Point(16, top);
            box.Size = new Size(460, 22);
            box.BackColor = Theme.InputBg;
            box.ForeColor = Theme.Text;
            box.BorderStyle = BorderStyle.FixedSingle;
            if (password) box.PasswordChar = '•';
            return box;
        }

        private static NumericUpDown MakeNumber(int top, decimal min, decimal max, decimal step, int decimals)
        {
            var box = new NumericUpDown();
            box.Location = new Point(16, top);
            box.Size = new Size(110, 22);
            box.Minimum = min;
            box.Maximum = max;
            box.Increment = step;
            box.DecimalPlaces = decimals;
            box.BackColor = Theme.InputBg;
            box.ForeColor = Theme.Text;
            box.BorderStyle = BorderStyle.FixedSingle;
            return box;
        }

        // ------------------------------------------------------------ значения

        private void LoadValues()
        {
            _baseUrl.Text = _config.BaseUrl;
            _apiKey.Text = _config.ApiKey;
            _model.Text = _config.Model;
            _workspace.Text = _config.Workspace;
            _systemPrompt.Text = _config.SystemPrompt;
            _insecureTls.Checked = _config.InsecureTls;

            _maxSteps.Value = Clamp(_maxSteps, _config.MaxSteps);
            _maxTokens.Value = Clamp(_maxTokens, _config.MaxTokens);
            _temperature.Value = Clamp(_temperature, (decimal)_config.Temperature);
            _fontSize.Value = Clamp(_fontSize, _config.FontSize);
            _priceIn.Value = Clamp(_priceIn, (decimal)_config.PriceIn);
            _priceOut.Value = Clamp(_priceOut, (decimal)_config.PriceOut);
        }

        private static decimal Clamp(NumericUpDown box, decimal value)
        {
            if (value < box.Minimum) return box.Minimum;
            if (value > box.Maximum) return box.Maximum;
            return value;
        }

        private void ApplyValues()
        {
            _config.BaseUrl = _baseUrl.Text.Trim();
            _config.ApiKey = _apiKey.Text.Trim();
            _config.Model = _model.Text.Trim();

            string workspace = _workspace.Text.Trim();
            _config.Workspace = workspace;

            _config.SystemPrompt = _systemPrompt.Text;
            _config.InsecureTls = _insecureTls.Checked;

            _config.MaxSteps = (int)_maxSteps.Value;
            _config.MaxTokens = (int)_maxTokens.Value;
            _config.Temperature = (double)_temperature.Value;
            _config.FontSize = (int)_fontSize.Value;
            _config.PriceIn = (double)_priceIn.Value;
            _config.PriceOut = (double)_priceOut.Value;
        }

        private Config BuildConfig()
        {
            Config clone = _config.Clone();
            clone.BaseUrl = _baseUrl.Text.Trim();
            clone.ApiKey = _apiKey.Text.Trim();
            clone.Model = _model.Text.Trim();
            clone.InsecureTls = _insecureTls.Checked;
            clone.MaxTokens = (int)_maxTokens.Value;
            clone.Temperature = (double)_temperature.Value;
            return clone;
        }

        private void BrowseWorkspace()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Рабочая папка проекта";
                dialog.ShowNewFolderButton = true;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _workspace.Text = dialog.SelectedPath;
            }
        }

        // ------------------------------------------------------------ проверка связи

        private void TestConnection()
        {
            Config probe = BuildConfig();
            if (string.IsNullOrEmpty(probe.ApiKey) && !probe.BaseUrl.Contains("localhost"))
            {
                _testResult.Text = "Сначала укажите ключ API.";
                _testResult.ForeColor = Theme.Warn;
                return;
            }

            _testButton.Enabled = false;
            _testResult.ForeColor = Theme.Dim;
            _testResult.Text = "проверяю…";
            LlmClient.PrepareTls(probe.InsecureTls);

            var thread = new Thread(delegate ()
            {
                string result;
                bool ok;
                try
                {
                    result = new LlmClient(probe).TestConnection();
                    ok = true;
                }
                catch (Exception ex)
                {
                    result = ex.Message;
                    ok = false;
                }
                SafeUpdate(delegate
                {
                    _testResult.Text = result;
                    _testResult.ForeColor = ok ? Theme.Success : Theme.Error;
                    _testButton.Enabled = true;
                });
            });
            thread.IsBackground = true;
            thread.Start();
        }

        private void SafeUpdate(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch { }
        }
    }
}
