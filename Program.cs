using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using NovaCode.Core;
using NovaCode.UI;

namespace NovaCode
{
    internal static class Program
    {
        private static Mutex _singleInstance;

        [STAThread]
        private static int Main(string[] args)
        {
            if (HasFlag(args, "--selftest")) return SelfTest.Run();

            // --mock: демонстрация GUI без ключа и без сети. Ответы отдаёт локальная
            // заглушка, но инструменты (ls, read, write) работают по-настоящему.
            bool demo = HasFlag(args, "--mock") || HasFlag(args, "--demo");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Один экземпляр: на XP с малым объёмом памяти второй процесс только мешает.
            // У демо свой мьютекс — его можно держать открытым рядом с рабочей копией.
            bool created;
            _singleInstance = new Mutex(true,
                demo ? "NovaCode.SingleInstance.Demo" : "NovaCode.SingleInstance", out created);
            if (!created)
            {
                MessageBox.Show(demo ? "Демо-режим NovaCode уже запущен." : "NovaCode уже запущен.",
                    "NovaCode", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            Application.ThreadException += delegate (object sender, ThreadExceptionEventArgs e)
            {
                HandleCrash(e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e)
            {
                HandleCrash(e.ExceptionObject as Exception);
            };

            MockLlmServer mock = null;
            try
            {
                Config config;
                if (demo)
                {
                    // Настройки, сессии и журнал демо живут отдельно, в %TEMP%.
                    Config.RootOverride = Path.Combine(Path.GetTempPath(), "NovaCode-demo");
                    mock = MockLlmServer.Demo();
                    config = DemoConfig(mock);
                }
                else
                {
                    config = Config.Load();
                }

                Log.Write("Запуск NovaCode. ОС: " + Environment.OSVersion.VersionString
                          + ", .NET: " + Environment.Version
                          + ", x64-процесс: " + (IntPtr.Size == 8)
                          + (demo ? ", демо-режим на " + mock.BaseUrl : ""));

                Application.Run(new MainForm(config, mock));
                return 0;
            }
            catch (Exception ex)
            {
                HandleCrash(ex);
                return 1;
            }
            finally
            {
                if (mock != null) mock.Dispose();

                if (_singleInstance != null)
                {
                    try { _singleInstance.ReleaseMutex(); } catch { }
                    _singleInstance.Close();
                }
            }
        }

        // ------------------------------------------------------------ демо-режим

        private static Config DemoConfig(MockLlmServer mock)
        {
            // Тему и размер шрифта берём из демо-конфига, если он уже есть, —
            // так демо запоминает, что пользователь в нём менял.
            Config config = Config.Load();
            config.BaseUrl = mock.BaseUrl;
            config.Model = "demo-model";
            config.ApiKey = "demo";
            config.Workspace = DemoWorkspace();
            config.MaxSteps = 6;
            config.Temperature = 0;
            return config;
        }

        /// <summary>Рабочая папка демо: создаём её с парой файлов, чтобы ls было что показывать.</summary>
        private static string DemoWorkspace()
        {
            string dir = Path.Combine(Config.AppDir, "workspace");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            if (Directory.GetFileSystemEntries(dir).Length == 0)
            {
                var utf8 = new UTF8Encoding(false);
                File.WriteAllText(Path.Combine(dir, "hello.c"), DemoHelloC(), utf8);
                File.WriteAllText(Path.Combine(dir, "notes.md"), DemoNotes(), utf8);
                File.WriteAllText(Path.Combine(dir, "README.txt"), DemoReadme(), utf8);
            }
            return dir;
        }

        private static string DemoHelloC()
        {
            return
                "#include <stdio.h>\n" +
                "\n" +
                "/* Проверка старого доброго компилятора: gcc под XP справляется. */\n" +
                "int main(void)\n" +
                "{\n" +
                "    int i;\n" +
                "    for (i = 1; i <= 3; i++)\n" +
                "        printf(\"step %d\\n\", i);\n" +
                "    return 0;\n" +
                "}\n";
        }

        private static string DemoNotes()
        {
            return
                "# Заметки\n" +
                "\n" +
                "- [x] собрать NovaCode\n" +
                "- [x] прогнать самотест\n" +
                "- [ ] проверить на живой машине с XP SP3\n" +
                "\n" +
                "Файл создан демо-режимом, его можно править руками.\n";
        }

        private static string DemoReadme()
        {
            return
                "Это демо-режим NovaCode.\r\n" +
                "\r\n" +
                "Модель не вызывается: ответы заготовлены заранее, а вот инструменты\r\n" +
                "(ls, read, write, edit, bash) работают по-настоящему — в этой папке.\r\n" +
                "\r\n" +
                "Чтобы подключить настоящий сервер, запустите NovaCode без --mock\r\n" +
                "и заполните настройки (F2).\r\n";
        }

        // ------------------------------------------------------------ прочее

        private static bool HasFlag(string[] args, string flag)
        {
            if (args == null) return false;
            foreach (string arg in args)
                if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void HandleCrash(Exception ex)
        {
            if (ex == null) return;

            Log.Error("Необработанная ошибка", ex);

            string detail = ex.Message;
            if (detail != null && detail.Length > 500) detail = detail.Substring(0, 500) + "…";

            string message = "Произошла ошибка:\n\n" + detail
                           + "\n\nПодробности записаны в журнал:\n" + Log.Path_;

            try
            {
                MessageBox.Show(message, "NovaCode", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}
