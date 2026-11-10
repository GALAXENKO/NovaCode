using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using NovaCode.Core.Tls;

namespace NovaCode.Core
{
    /// <summary>
    /// Быстрая проверка сборки без сети: JSON, пути, glob, инструменты, сериализация сообщений.
    /// Запуск: NovaCode.exe --selftest
    /// </summary>
    public static class SelfTest
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        private static int _passed;
        private static int _failed;
        private static readonly StringBuilder Report = new StringBuilder();

        public static int Run()
        {
            try { AttachConsole(-1); } catch { }

            var watch = Stopwatch.StartNew();

            TestJson();
            TestGlob();
            TestPaths();
            TestTools();
            TestMessages();
            TestTruncate();
            TestHtmlToText();
            TestTls();
            TestAgentLoop();

            watch.Stop();

            Say("");
            Say(string.Format(CultureInfo.InvariantCulture, "Итог: {0} пройдено, {1} провалено, {2} мс",
                _passed, _failed, watch.ElapsedMilliseconds));

            try
            {
                File.WriteAllText(Path.Combine(Config.AppDir, "selftest.log"), Report.ToString(),
                    new UTF8Encoding(false));
            }
            catch { }

            return _failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------ инфраструктура

        private static void Say(string text)
        {
            Report.AppendLine(text);
            try { Console.WriteLine(text); } catch { }
        }

        private static void Check(string name, bool condition, string detail)
        {
            if (condition)
            {
                _passed++;
                Say("  ok   " + name);
            }
            else
            {
                _failed++;
                Say("  FAIL " + name + (string.IsNullOrEmpty(detail) ? "" : "  -> " + detail));
            }
        }

        private static void Section(string title)
        {
            Say("");
            Say("[" + title + "]");
        }

        // ------------------------------------------------------------ тесты

        private static void TestJson()
        {
            Section("JSON");

            const string source = "{\"a\":[1,2.5,\"x\",true,null],\"b\":{\"c\":-3e2},\"u\":\"\\u041f\\u0440\\u0438\\u0432\\u0435\\u0442\"}";
            object parsed = null;
            bool parsedOk = true;
            try { parsed = Json.Parse(source); }
            catch (Exception ex) { parsedOk = false; Say("    исключение: " + ex.Message); }

            Check("разбор объекта", parsedOk, "");
            if (!parsedOk) return;

            var root = Json.AsObject(parsed);
            Check("объект не null", root != null, "");

            var a = Json.Arr(root, "a");
            Check("массив из 5 элементов", a != null && a.Count == 5, a == null ? "null" : a.Count.ToString());
            Check("число 2.5", a != null && a.Count > 1 && Math.Abs(Convert.ToDouble(a[1], CultureInfo.InvariantCulture) - 2.5) < 1e-9, "");
            Check("строка x", a != null && a.Count > 2 && (a[2] as string) == "x", "");
            Check("true", a != null && a.Count > 3 && a[3] is bool && (bool)a[3], "");
            Check("null", a != null && a.Count > 4 && a[4] == null, "");

            var b = Json.Obj(root, "b");
            Check("вложенный объект -300", Math.Abs(Json.Num(b, "c", 0) + 300) < 1e-9, "");
            Check("\\u escape → Привет", Json.Str(root, "u", "") == "Привет", Json.Str(root, "u", ""));

            string round = Json.Stringify(parsed);
            object again = Json.Parse(round);
            Check("round-trip устойчив", Json.Stringify(again) == round, round);

            string pretty = Json.StringifyIndented(parsed);
            Check("indented разбирается обратно", Json.Stringify(Json.Parse(pretty)) == round, "");

            var tricky = Json.NewObject();
            tricky["quote"] = "он сказал \"да\"";
            tricky["slash"] = "a\\b";
            tricky["nl"] = "1\n2\r\n3";
            string trickyJson = Json.Stringify(tricky);
            var trickyBack = Json.AsObject(Json.Parse(trickyJson));
            Check("экранирование кавычек и переводов строк",
                Json.Str(trickyBack, "quote", "") == "он сказал \"да\"" &&
                Json.Str(trickyBack, "slash", "") == "a\\b" &&
                Json.Str(trickyBack, "nl", "") == "1\n2\r\n3", trickyJson);

            Check("битый JSON не роняет TryParse", Json.AsObject(Json.TryParse("{oops", Json.NewObject())) != null, "");
        }

        private static void TestGlob()
        {
            Section("Glob");

            Regex recursive = Util.GlobToRegex("**/*.cs");
            Check("**/*.cs → src/a/b.cs", recursive.IsMatch("src\\a\\b.cs"), "");
            Check("**/*.cs → Program.cs", recursive.IsMatch("Program.cs"), "");

            Regex flat = Util.GlobToRegex("*.cs");
            Check("*.cs → Program.cs", flat.IsMatch("Program.cs"), "");
            Check("*.cs не матчит src\\a.cs", !flat.IsMatch("src\\a.cs"), "");

            Regex single = Util.GlobToRegex("src/?ain.cs");
            Check("? матчит один символ", single.IsMatch("src\\main.cs"), "");
            Check("? не матчит два символа", !single.IsMatch("src\\maain.cs"), "");

            Regex star = Util.GlobToRegex("Core/*.cs");
            Check("Core/*.cs → Core/Json.cs", star.IsMatch("Core\\Json.cs"), "");
            Check("Core/*.cs не матчит Core/sub/Json.cs", !star.IsMatch("Core\\sub\\Json.cs"), "");
        }

        private static void TestPaths()
        {
            Section("Пути");

            string root = Path.Combine(Path.GetTempPath(), "novacode-selftest");
            Directory.CreateDirectory(root);

            bool inside = false;
            try
            {
                string p = Util.ResolveInside(root, "sub/file.txt");
                inside = p.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            Check("относительный путь внутри папки", inside, "");

            bool escaped = false;
            try { Util.ResolveInside(root, "..\\..\\windows\\system32\\cmd.exe"); }
            catch (ToolException) { escaped = true; }
            Check("выход за папку запрещён", escaped, "");

            bool absoluteInside = false;
            try
            {
                string p = Util.ResolveInside(root, Path.Combine(root, "a.txt"));
                absoluteInside = p.EndsWith("a.txt", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            Check("абсолютный путь внутри разрешён", absoluteInside, "");

            Check("Relative возвращает путь без корня",
                Util.Relative(root, Path.Combine(root, "x", "y.txt")) == "x\\y.txt",
                Util.Relative(root, Path.Combine(root, "x", "y.txt")));

            Check("SafeFileName чистит символы", Util.SafeFileName("a/b:c*d") == "a_b_c_d", Util.SafeFileName("a/b:c*d"));
        }

        private static void TestTools()
        {
            Section("Инструменты");

            string root = Path.Combine(Path.GetTempPath(), "novacode-selftest", "tools");
            if (Directory.Exists(root)) { try { Directory.Delete(root, true); } catch { } }
            Directory.CreateDirectory(root);

            ToolResult write = Tools.Run("write", "{\"path\":\"hello.txt\",\"content\":\"строка один\\nстрока два\"}", root, null);
            Check("write создаёт файл", !write.IsError && File.Exists(Path.Combine(root, "hello.txt")), write.Output);

            ToolResult read = Tools.Run("read", "{\"path\":\"hello.txt\"}", root, null);
            Check("read возвращает нумерованные строки",
                !read.IsError && read.Output.Contains("1| строка один") && read.Output.Contains("2| строка два"), read.Output);

            ToolResult edit = Tools.Run("edit",
                "{\"path\":\"hello.txt\",\"oldString\":\"два\",\"newString\":\"три\"}", root, null);
            Check("edit заменяет фрагмент", !edit.IsError, edit.Output);
            Check("edit применился", File.ReadAllText(Path.Combine(root, "hello.txt"), Encoding.UTF8).Contains("строка три"), "");

            ToolResult missing = Tools.Run("edit",
                "{\"path\":\"hello.txt\",\"oldString\":\"нет такого\",\"newString\":\"x\"}", root, null);
            Check("edit без совпадения возвращает ошибку", missing.IsError, missing.Output);

            Tools.Run("write", "{\"path\":\"dup.txt\",\"content\":\"aa\\naa\"}", root, null);
            ToolResult ambiguous = Tools.Run("edit",
                "{\"path\":\"dup.txt\",\"oldString\":\"aa\",\"newString\":\"bb\"}", root, null);
            Check("edit требует replaceAll при нескольких совпадениях", ambiguous.IsError, ambiguous.Output);

            ToolResult all = Tools.Run("edit",
                "{\"path\":\"dup.txt\",\"oldString\":\"aa\",\"newString\":\"bb\",\"replaceAll\":true}", root, null);
            Check("replaceAll заменяет всё", !all.IsError &&
                File.ReadAllText(Path.Combine(root, "dup.txt"), Encoding.UTF8).Replace("\r\n", "\n") == "bb\nbb", all.Output);

            ToolResult outside = Tools.Run("read", "{\"path\":\"../../boot.ini\"}", root, null);
            Check("read вне папки запрещён", outside.IsError, outside.Output);

            Tools.Run("write", "{\"path\":\"sub/inner/a.cs\",\"content\":\"class A {}\"}", root, null);
            ToolResult glob = Tools.Run("glob", "{\"pattern\":\"**/*.cs\"}", root, null);
            Check("glob находит вложенный файл", !glob.IsError && glob.Output.Contains("a.cs"), glob.Output);

            Tools.Run("write", "{\"path\":\"sub/inner/b.txt\",\"content\":\"needle here\"}", root, null);
            ToolResult grep = Tools.Run("grep", "{\"pattern\":\"needle\",\"path\":\"sub\"}", root, null);
            Check("grep находит строку", !grep.IsError && grep.Output.Contains("b.txt"), grep.Output);

            ToolResult ls = Tools.Run("ls", "{}", root, null);
            Check("ls перечисляет файлы", !ls.IsError && ls.Output.Contains("hello.txt"), ls.Output);

            ToolResult bash = Tools.Run("bash", "{\"command\":\"echo novacode-ok\"}", root, null);
            Check("bash выполняет команду", !bash.IsError && bash.Output.Contains("novacode-ok"), bash.Output);

            ToolResult unknown = Tools.Run("nope", "{}", root, null);
            Check("неизвестный инструмент даёт ошибку", unknown.IsError, unknown.Output);

            ToolResult badJson = Tools.Run("read", "{not json", root, null);
            Check("битые аргументы не роняют запуск", badJson.IsError, badJson.Output);

            Check("схемы инструментов описывают 8 функций", Tools.Schemas().Count == 8, Tools.Schemas().Count.ToString());
        }

        private static void TestMessages()
        {
            Section("Сообщения");

            var call = new ToolCall { Id = "call_1", Name = "read", Arguments = "{\"path\":\"a.txt\"}" };
            var assistant = new ChatMessage
            {
                Role = "assistant",
                Content = "Сейчас посмотрю.",
                ToolCalls = new List<ToolCall> { call }
            };

            var api = assistant.ToApi();
            Check("ToApi отдаёт role", Json.Str(api, "role", "") == "assistant", "");
            var calls = Json.Arr(api, "tool_calls");
            Check("ToApi отдаёт tool_calls", calls != null && calls.Count == 1, "");
            if (calls != null && calls.Count == 1)
            {
                var item = Json.AsObject(calls[0]);
                var fn = Json.Obj(item, "function");
                Check("tool_call содержит имя и аргументы",
                    Json.Str(fn, "name", "") == "read" && Json.Str(fn, "arguments", "").Contains("a.txt"), "");
            }

            var restored = ChatMessage.FromStorage(assistant.ToStorage());
            Check("storage round-trip сохраняет вызов инструмента",
                restored.ToolCalls != null && restored.ToolCalls.Count == 1 && restored.ToolCalls[0].Name == "read", "");

            var toolMessage = ChatMessage.Tool(call, ToolResult.Ok("содержимое", "read", "a.txt"));
            var toolApi = toolMessage.ToApi();
            Check("tool-сообщение содержит tool_call_id", Json.Str(toolApi, "tool_call_id", "") == "call_1", "");

            var user = ChatMessage.User("привет");
            Check("user-сообщение без tool_calls", Json.Arr(user.ToApi(), "tool_calls") == null, "");
        }

        private static void TestTruncate()
        {
            Section("Утилиты");

            var sb = new StringBuilder();
            for (int i = 1; i <= 40; i++) sb.AppendLine("line " + i.ToString(CultureInfo.InvariantCulture));
            string middle = Util.TruncateMiddle(sb.ToString(), 10);
            Check("TruncateMiddle держит голову и хвост",
                middle.Contains("line 1") && middle.Contains("line 40") && middle.Contains("скрыто"), middle);

            Check("Truncate обрезает длинный текст", Util.Truncate(new string('a', 100), 10).Length < 60, "");
            Check("Truncate не трогает короткий", Util.Truncate("abc", 10) == "abc", "");
            Check("HumanSize форматирует КБ", Util.HumanSize(2048).Contains("КБ"), Util.HumanSize(2048));
            Check("CountOccurrences", Util.CountOccurrences("ababab", "ab") == 3, "");
            Check("FirstLine", Util.FirstLine("первая\nвторая") == "первая", "");
            Check("WithLineNumbers", Util.WithLineNumbers("a\nb", 1).Contains("2| b"), Util.WithLineNumbers("a\nb", 1));
        }

        private static void TestHtmlToText()
        {
            Section("HTML");

            string html = "<html><head><style>b{}</style></head><body><h1>Заголовок</h1><p>Текст &amp; ещё</p>"
                        + "<script>var x = 1;</script><ul><li>один</li><li>два</li></ul></body></html>";
            string text = Tools.HtmlToText(html);
            Check("теги удалены", !text.Contains("<"), text);
            Check("скрипты и стили вырезаны", !text.Contains("var x") && !text.Contains("b{}"), text);
            Check("сущности раскрыты", text.Contains("Текст & ещё"), text);
            Check("заголовок на месте", text.Contains("Заголовок"), text);
        }

        // ------------------------------------------------------------ TLS и HTTP

        /// <summary>
        /// Криптопримитивы и HTTP-разбор на эталонных векторах. Сети не требуют:
        /// проверяются RFC-векторы и вшитые корневые сертификаты.
        /// </summary>
        private static void TestTls()
        {
            Section("TLS: примитивы");

            // HKDF RFC 5869, тест 1
            var ikm = new byte[22];
            for (int i = 0; i < ikm.Length; i++) ikm[i] = 0x0b;
            byte[] salt = Hex.Decode("000102030405060708090a0b0c");
            byte[] info = Hex.Decode("f0f1f2f3f4f5f6f7f8f9");

            byte[] prk = Hkdf.Extract(HashAlg.Sha256, salt, ikm);
            Check("HKDF-Extract (RFC 5869)",
                Hex.Encode(prk) == "077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5",
                Hex.Encode(prk));

            byte[] okm = Hkdf.Expand(HashAlg.Sha256, prk, info, 42);
            Check("HKDF-Expand (RFC 5869)",
                Hex.Encode(okm) == "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865",
                Hex.Encode(okm));

            // AES-128-GCM, векторы NIST
            var key = new byte[16];
            var iv = new byte[12];
            var empty = new byte[0];
            var tag = new byte[16];
            AesGcm.Encrypt(key, iv, empty, empty, 0, 0, empty, 0, tag, 0);
            Check("AES-GCM тег пустого текста", Hex.Encode(tag) == "58e2fccefa7e3061367f1d57a4e7455a", Hex.Encode(tag));

            var plain = new byte[16];
            var cipher = new byte[16];
            AesGcm.Encrypt(key, iv, empty, plain, 0, 16, cipher, 0, tag, 0);
            Check("AES-GCM шифртекст", Hex.Encode(cipher) == "0388dace60b6a392f328c2b971b2fe78", Hex.Encode(cipher));
            Check("AES-GCM тег", Hex.Encode(tag) == "ab6e47d42cec13bdf53a67b21257bddf", Hex.Encode(tag));

            var restored = new byte[16];
            bool gcmOk = AesGcm.Decrypt(key, iv, empty, cipher, 0, 16, tag, 0, restored, 0);
            Check("AES-GCM расшифровка", gcmOk && Hex.Encode(restored) == Hex.Encode(plain), Hex.Encode(restored));

            // ChaCha20-Poly1305 RFC 8439, 2.8.2
            byte[] ckey = Hex.Decode("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f");
            byte[] cnonce = Hex.Decode("070000004041424344454647");
            byte[] caad = Hex.Decode("50515253c0c1c2c3c4c5c6c7");
            byte[] cplain = Encoding.ASCII.GetBytes(
                "Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");
            var ccip = new byte[cplain.Length];
            var ctag = new byte[16];
            ChaCha20Poly1305.Encrypt(ckey, cnonce, caad, cplain, 0, cplain.Length, ccip, 0, ctag, 0);
            Check("ChaCha20-Poly1305 шифртекст",
                Hex.Encode(ccip).StartsWith("d31a8d34648e60db7b86afbc53ef7ec2", StringComparison.Ordinal),
                Hex.Encode(ccip).Substring(0, 32));
            Check("ChaCha20-Poly1305 тег", Hex.Encode(ctag) == "1ae10b594f09e26a7e902ecbd0600691", Hex.Encode(ctag));

            // X25519 RFC 7748, 5.2
            byte[] shared = X25519.SharedSecret(
                Hex.Decode("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4"),
                Hex.Decode("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c"));
            Check("X25519 (RFC 7748)",
                Hex.Encode(shared) == "c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552",
                Hex.Encode(shared));

            byte[] alicePriv = Hex.Decode("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
            Check("X25519 открытый ключ",
                Hex.Encode(X25519.PublicKey(alicePriv)) == "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a",
                Hex.Encode(X25519.PublicKey(alicePriv)));

            Section("TLS: сертификаты");

            var roots = new List<X509Cert>();
            int broken = 0;
            foreach (string base64 in TrustStoreData.Roots)
            {
                try { roots.Add(X509Cert.Parse(Convert.FromBase64String(base64))); }
                catch { broken++; }
            }

            Check("вшитые корни разбираются", broken == 0 && roots.Count >= 20,
                "корней " + roots.Count + ", битых " + broken);

            // Без этих корней не проверяются цепочки Cloudflare/SSL.com 2022 года
            // (например, example.com) и старые Comodo-цепочки.
            int haveSsl2022 = 0, haveAaa = 0;
            foreach (X509Cert root in roots)
            {
                if (root.SubjectText.IndexOf("SSL.com TLS", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    root.SubjectText.IndexOf("2022", StringComparison.OrdinalIgnoreCase) >= 0) haveSsl2022++;
                if (root.SubjectText.IndexOf("AAA Certificate Services", StringComparison.OrdinalIgnoreCase) >= 0) haveAaa++;
            }

            Check("есть корни SSL.com 2022 и AAA", haveSsl2022 == 2 && haveAaa == 1,
                "SSL.com 2022: " + haveSsl2022 + ", AAA: " + haveAaa);

            int selfSigned = 0, signedOk = 0;
            foreach (X509Cert root in roots)
            {
                if (!root.IsSelfIssued || !root.HasKey) continue;
                selfSigned++;
                if (root.VerifyWith(root)) signedOk++;
            }

            Check("подписи самоподписанных корней верны", signedOk == selfSigned,
                signedOk + " из " + selfSigned);

            // Кросс-подписанные корни (Amazon Root CA 1 выписан Starfield Services Root CA G2)
            // должны сходиться к другому корню из этого же списка.
            int linked = 0, orphans = 0;
            foreach (X509Cert root in roots)
            {
                if (root.IsSelfIssued && root.VerifyWith(root)) { linked++; continue; }

                bool found = false;
                foreach (X509Cert other in roots)
                {
                    if (ReferenceEquals(other, root)) continue;
                    if (root.VerifyWith(other)) { found = true; break; }
                }

                if (found) linked++;
                else { orphans++; Say("    без издателя: " + root.SubjectText); }
            }

            Check("у каждого корня есть проверяемый издатель", orphans == 0,
                "проверено " + linked + ", без издателя " + orphans);

            Section("TLS: HTTP");

            HttpUrl parsed = HttpUrl.Parse("https://api.deepseek.com/v1/chat/completions");
            Check("разбор https-адреса",
                parsed.Scheme == "https" && parsed.Host == "api.deepseek.com" &&
                parsed.Port == 443 && parsed.Path == "/v1/chat/completions",
                parsed.Scheme + " " + parsed.Host + ":" + parsed.Port + parsed.Path);

            HttpUrl local = HttpUrl.Parse("http://127.0.0.1:8080");
            Check("разбор локального адреса без пути",
                local.Scheme == "http" && local.Host == "127.0.0.1" && local.Port == 8080 && local.Path == "/",
                local.Host + ":" + local.Port + local.Path);

            HttpUrl upper = HttpUrl.Parse("HTTPS://Example.COM/A?b=1");
            Check("схема и хост в нижнем регистре", upper.Scheme == "https" && upper.Host == "example.com" && upper.Path == "/A?b=1",
                upper.Scheme + " " + upper.Host + " " + upper.Path);

            // Тело с Content-Length
            byte[] payload = Encoding.ASCII.GetBytes("hello body");
            using (var body = new HttpBodyStream(new MemoryStream(payload), payload.Length, false))
            using (var reader = new StreamReader(body, Encoding.ASCII))
                Check("тело по Content-Length", reader.ReadToEnd() == "hello body", "");

            // Тело chunked
            byte[] chunked = Encoding.ASCII.GetBytes(
                "5\r\nhello\r\n7\r\n, world\r\n0\r\n\r\n");
            using (var body = new HttpBodyStream(new MemoryStream(chunked), -1, true))
            using (var reader = new StreamReader(body, Encoding.ASCII))
                Check("тело chunked", reader.ReadToEnd() == "hello, world", "");

            // Тело до закрытия соединения
            using (var body = new HttpBodyStream(new MemoryStream(payload), -1, false))
            using (var reader = new StreamReader(body, Encoding.ASCII))
                Check("тело до закрытия", reader.ReadToEnd() == "hello body", "");
        }

        // ------------------------------------------------------------ агент на мок-сервере

        /// <summary>
        /// Полный прогон агентского цикла без сети: локальный сервер отдаёт заранее
        /// записанные SSE-ответы, агент вызывает настоящий инструмент write.
        /// </summary>
        private static void TestAgentLoop()
        {
            Section("Агент и SSE (мок-сервер)");

            string root = Path.Combine(Path.GetTempPath(), "novacode-selftest", "agent");
            if (Directory.Exists(root)) { try { Directory.Delete(root, true); } catch { } }
            Directory.CreateDirectory(root);

            IWebProxy savedProxy = WebRequest.DefaultWebProxy;
            WebRequest.DefaultWebProxy = null;   // локальный сокет не должен идти через прокси IE
            try
            {
                AgentToolCallScenario(root);
                AgentHttpErrorScenario(root);
                AgentStreamOptionsScenario(root);
                SystemHttpScenario(root);
            }
            finally
            {
                WebRequest.DefaultWebProxy = savedProxy;
            }
        }

        private static void AgentToolCallScenario(string root)
        {
            int calls = 0;
            using (var server = new MockLlmServer(delegate (string body)
            {
                calls++;
                if (calls == 1) return MockLlmServer.Sse(ToolCallChunks());
                return MockLlmServer.Sse(FinalChunks("Готово. Файл создан."));
            }))
            {
                Config config = MockConfig(server.Port, root);
                Session session = Session.Create(config.Model);
                var agent = new Agent(config, session);

                var streamed = new StringBuilder();
                var kinds = new List<AgentEventKind>();
                ToolResult toolResult = null;
                string error = null;

                agent.Event += delegate (AgentEvent e)
                {
                    kinds.Add(e.Kind);
                    if (e.Kind == AgentEventKind.TextDelta) streamed.Append(e.Text);
                    if (e.Kind == AgentEventKind.ToolFinished) toolResult = e.Result;
                    if (e.Kind == AgentEventKind.Error) error = e.Text;
                };

                try
                {
                    agent.Run("создай note.txt");

                    string note = Path.Combine(root, "note.txt");
                    Check("агент выполнил инструмент write", File.Exists(note),
                        error ?? note);

                    string content = File.Exists(note) ? File.ReadAllText(note, Encoding.UTF8) : "";
                    Check("аргументы, пришедшие кусками, склеены верно", content.Contains("привет из мока"), content);

                    Check("текст ответа стримился кусками",
                        streamed.ToString().Contains("Сейчас запишу") && streamed.ToString().Contains("Готово"), streamed.ToString());

                    Check("событие ToolFinished с успехом", toolResult != null && !toolResult.IsError,
                        toolResult == null ? "нет события" : toolResult.Output);

                    Check("usage накоплен в сессии",
                        session.PromptTokens == 270 && session.CompletionTokens == 40,
                        session.PromptTokens + "/" + session.CompletionTokens);

                    Check("история: user, assistant, tool, assistant",
                        session.Messages.Count == 4 && session.Messages[2].Role == "tool",
                        session.Messages.Count.ToString());

                    Check("сервер получил ровно два запроса", server.RequestCount == 2, server.RequestCount.ToString());
                    Check("запрос содержит схемы инструментов",
                        server.FirstBody.Contains("\"tools\"") && server.FirstBody.Contains("\"read\""), "");
                    Check("ключ ушёл в Authorization",
                        server.LastAuthorization == "Bearer test-key", server.LastAuthorization);
                    Check("во втором запросе есть tool-сообщение с tool_call_id",
                        server.LastBody.Contains("\"tool_call_id\":\"call_a1\""), server.LastBody);
                    Check("tool_choice auto", server.FirstBody.Contains("\"tool_choice\":\"auto\""), "");
                    Check("запрос стриминговый", server.FirstBody.Contains("\"stream\":true"), "");
                }
                finally
                {
                    session.Delete();
                }
            }
        }

        private static void AgentHttpErrorScenario(string root)
        {
            using (var server = new MockLlmServer(delegate (string body)
            {
                return new MockResponse
                {
                    Status = 401,
                    Reason = "Unauthorized",
                    Body = "{\"error\":{\"message\":\"invalid api key\"}}"
                };
            }))
            {
                Config config = MockConfig(server.Port, root);
                Session session = Session.Create(config.Model);
                var agent = new Agent(config, session);
                string error = null;
                agent.Event += delegate (AgentEvent e)
                {
                    if (e.Kind == AgentEventKind.Error) error = e.Text;
                };

                try
                {
                    agent.Run("привет");
                    Check("ошибка 401 доходит до UI с текстом сервера",
                        error != null && error.Contains("401") && error.Contains("invalid api key"), error ?? "нет ошибки");
                    Check("после ошибки агент свободен", !agent.IsBusy, "");
                }
                finally
                {
                    session.Delete();
                }
            }
        }

        private static void AgentStreamOptionsScenario(string root)
        {
            using (var server = new MockLlmServer(delegate (string body)
            {
                if (body.Contains("stream_options"))
                {
                    return new MockResponse
                    {
                        Status = 400,
                        Reason = "Bad Request",
                        Body = "{\"error\":{\"message\":\"Unrecognized request argument: stream_options\"}}"
                    };
                }
                return MockLlmServer.Sse(FinalChunks("ok"));
            }))
            {
                Config config = MockConfig(server.Port, root);
                Session session = Session.Create(config.Model);
                var agent = new Agent(config, session);
                string error = null;
                agent.Event += delegate (AgentEvent e)
                {
                    if (e.Kind == AgentEventKind.Error) error = e.Text;
                };

                try
                {
                    agent.Run("скажи ok");
                    Check("повтор без stream_options проходит", error == null, error ?? "");
                    Check("повторяющийся запрос ушёл без stream_options",
                        server.RequestCount == 2 && !server.LastBody.Contains("stream_options"),
                        server.RequestCount.ToString());
                }
                finally
                {
                    session.Delete();
                }
            }
        }

        /// <summary>
        /// Старый путь через HttpWebRequest (системный Schannel) должен оставаться рабочим:
        /// он нужен для локальных HTTP-серверов и для тех, кто выключил свой TLS.
        /// </summary>
        private static void SystemHttpScenario(string root)
        {
            using (var server = new MockLlmServer(delegate (string body)
            {
                return MockLlmServer.Sse(FinalChunks("системный путь"));
            }))
            {
                Config config = MockConfig(server.Port, root);
                config.OwnTls = false;

                var messages = new List<Dictionary<string, object>>();
                var user = Json.NewObject();
                user["role"] = "user";
                user["content"] = "ping";
                messages.Add(user);

                var client = new LlmClient(config);
                string content = "";
                string error = null;

                try { content = client.Chat(messages, null, null, null).Content; }
                catch (Exception ex) { error = ex.Message; }

                Check("системный путь (HttpWebRequest) работает", content == "системный путь", error ?? content);
                Check("системный путь передаёт ключ", server.LastAuthorization == "Bearer test-key", server.LastAuthorization);
            }
        }

        private static Config MockConfig(int port, string workspace)        {
            var config = new Config();
            config.BaseUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/v1";
            config.Model = "mock-model";
            config.ApiKey = "test-key";
            config.Workspace = workspace;
            config.MaxSteps = 6;
            config.MaxTokens = 512;
            config.Temperature = 0;
            return config;
        }

        private static List<string> ToolCallChunks()
        {
            string arguments = Json.Stringify(WriteArguments());

            var chunks = new List<string>();
            chunks.Add(Chunk(TextDelta("Сейчас запишу файл. "), null, 0, 0));
            chunks.Add(Chunk(ToolDelta(0, "call_a1", "write", arguments.Substring(0, 18)), null, 0, 0));
            chunks.Add(Chunk(ToolDelta(0, null, null, arguments.Substring(18)), null, 0, 0));
            chunks.Add(Chunk(EmptyDelta(), "tool_calls", 120, 30));
            return chunks;
        }

        private static Dictionary<string, object> WriteArguments()
        {
            var args = Json.NewObject();
            args["path"] = "note.txt";
            args["content"] = "привет из мока";
            return args;
        }

        private static List<string> FinalChunks(string text)
        {
            var chunks = new List<string>();
            chunks.Add(Chunk(TextDelta(text), null, 0, 0));
            chunks.Add(Chunk(EmptyDelta(), "stop", 150, 10));
            return chunks;
        }

        private static Dictionary<string, object> EmptyDelta()
        {
            return Json.NewObject();
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
            if (name != null) fn["name"] = name;
            if (arguments != null) fn["arguments"] = arguments;

            var call = Json.NewObject();
            call["index"] = index;
            if (id != null) call["id"] = id;
            if (name != null) call["type"] = "function";
            call["function"] = fn;

            var calls = new List<object>();
            calls.Add(call);

            var delta = Json.NewObject();
            delta["tool_calls"] = calls;
            return delta;
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
    }
}
