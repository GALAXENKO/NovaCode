using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NovaCode.Core
{
    /// <summary>Настройки приложения. Хранятся в %APPDATA%\NovaCode\config.json.</summary>
    public sealed class Config
    {
        public string BaseUrl = "https://api.deepseek.com/v1";
        public string ApiKey = "";
        public string Model = "deepseek-chat";
        public double Temperature = 0.2;
        public int MaxTokens = 4096;
        public int MaxSteps = 40;
        public string Workspace = "";
        public string SystemPrompt = "";
        public bool InsecureTls = false;
        /// <summary>Свой TLS 1.3 вместо системного Schannel (на XP он умеет только TLS 1.0).</summary>
        public bool OwnTls = true;
        public double PriceIn = 0;    // $ за 1M входных токенов
        public double PriceOut = 0;   // $ за 1M выходных токенов
        public string Theme = "dark";
        public int FontSize = 10;

        // ------------------------------------------------------------ пути

        /// <summary>
        /// Другой корень для настроек, сессий и журнала. Нужен демо-режиму (--mock),
        /// чтобы он не трогал настоящие настройки и историю пользователя.
        /// Пустая строка — обычный %APPDATA%\NovaCode.
        /// </summary>
        public static string RootOverride = "";

        public static string AppDir
        {
            get
            {
                string dir = string.IsNullOrEmpty(RootOverride)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaCode")
                    : RootOverride;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string SessionsDir
        {
            get
            {
                string dir = Path.Combine(AppDir, "sessions");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string ConfigPath
        {
            get { return Path.Combine(AppDir, "config.json"); }
        }

        // ------------------------------------------------------------ загрузка / сохранение

        public static Config Load()
        {
            var cfg = new Config();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var root = Json.AsObject(Json.Parse(File.ReadAllText(ConfigPath, Encoding.UTF8)));
                    if (root != null) cfg.ReadFrom(root);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Не удалось прочитать config.json: " + ex.Message);
            }

            if (string.IsNullOrEmpty(cfg.Workspace) || !Directory.Exists(cfg.Workspace))
                cfg.Workspace = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            return cfg;
        }

        private void ReadFrom(Dictionary<string, object> root)
        {
            BaseUrl = Json.Str(root, "baseUrl", BaseUrl);
            ApiKey = Json.Str(root, "apiKey", ApiKey);
            Model = Json.Str(root, "model", Model);
            Temperature = Json.Num(root, "temperature", Temperature);
            MaxTokens = Json.Int(root, "maxTokens", MaxTokens);
            MaxSteps = Json.Int(root, "maxSteps", MaxSteps);
            Workspace = Json.Str(root, "workspace", Workspace);
            SystemPrompt = Json.Str(root, "systemPrompt", SystemPrompt);
            InsecureTls = Json.Bool(root, "insecureTls", InsecureTls);
            OwnTls = Json.Bool(root, "ownTls", OwnTls);
            PriceIn = Json.Num(root, "priceIn", PriceIn);
            PriceOut = Json.Num(root, "priceOut", PriceOut);
            Theme = Json.Str(root, "theme", Theme);
            FontSize = Json.Int(root, "fontSize", FontSize);
        }

        public void Save()
        {
            var d = Json.NewObject();
            d["baseUrl"] = BaseUrl;
            d["apiKey"] = ApiKey;
            d["model"] = Model;
            d["temperature"] = Temperature;
            d["maxTokens"] = MaxTokens;
            d["maxSteps"] = MaxSteps;
            d["workspace"] = Workspace;
            d["systemPrompt"] = SystemPrompt;
            d["insecureTls"] = InsecureTls;
            d["ownTls"] = OwnTls;
            d["priceIn"] = PriceIn;
            d["priceOut"] = PriceOut;
            d["theme"] = Theme;
            d["fontSize"] = FontSize;

            try
            {
                File.WriteAllText(ConfigPath, Json.StringifyIndented(d), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Write("Не удалось сохранить config.json: " + ex.Message);
            }
        }

        public string ChatCompletionsUrl
        {
            get
            {
                string url = (BaseUrl ?? "").Trim();
                if (url.Length == 0) url = "https://api.deepseek.com/v1";
                url = url.TrimEnd('/');
                if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return url;
                return url + "/chat/completions";
            }
        }

        public Config Clone()
        {
            return (Config)MemberwiseClone();
        }
    }

    /// <summary>Простой лог в %APPDATA%\NovaCode\novacode.log — без него на XP не отладишь.</summary>
    public static class Log
    {
        private static readonly object Gate = new object();

        public static string Path_
        {
            get { return System.IO.Path.Combine(Config.AppDir, "novacode.log"); }
        }

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    var info = new FileInfo(Path_);
                    if (info.Exists && info.Length > 2 * 1024 * 1024) info.Delete();
                    File.AppendAllText(Path_,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch { /* лог не должен ронять приложение */ }
        }

        public static void Error(string context, Exception ex)
        {
            Write(context + ": " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
        }
    }
}
