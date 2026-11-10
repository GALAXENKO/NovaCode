using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace NovaCode.Core
{
    public sealed class ToolResult
    {
        public string Output = "";
        public bool IsError;
        public string Title = "";
        public string Subtitle = "";

        public static ToolResult Ok(string output, string title, string subtitle)
        {
            return new ToolResult { Output = output, Title = title, Subtitle = subtitle };
        }

        public static ToolResult Fail(string message, string title, string subtitle)
        {
            return new ToolResult { Output = message, IsError = true, Title = title, Subtitle = subtitle };
        }
    }

    /// <summary>
    /// Инструменты агента. Каждый работает внутри рабочей папки проекта.
    /// Схемы описаны в формате [OI] function calling.
    /// </summary>
    public static class Tools
    {
        private const int MaxOutputChars = 30000;

        // ------------------------------------------------------------ схемы

        public static List<Dictionary<string, object>> Schemas()
        {
            var list = new List<Dictionary<string, object>>();
            list.Add(Function("read",
                "Прочитать файл. Возвращает содержимое с номерами строк.",
                Prop("path", "string", "Путь к файлу относительно рабочей папки", true),
                Prop("offset", "integer", "С какой строки начать (с 1)", false),
                Prop("limit", "integer", "Сколько строк прочитать (по умолчанию 2000)", false)));

            list.Add(Function("write",
                "Создать файл или полностью перезаписать существующий.",
                Prop("path", "string", "Путь к файлу", true),
                Prop("content", "string", "Новое содержимое файла", true)));

            list.Add(Function("edit",
                "Заменить точный фрагмент текста в файле. oldString должен встречаться ровно один раз.",
                Prop("path", "string", "Путь к файлу", true),
                Prop("oldString", "string", "Точный текст, который нужно заменить", true),
                Prop("newString", "string", "Текст замены", true),
                Prop("replaceAll", "boolean", "Заменить все вхождения", false)));

            list.Add(Function("ls",
                "Показать содержимое каталога.",
                Prop("path", "string", "Каталог относительно рабочей папки (по умолчанию корень)", false)));

            list.Add(Function("glob",
                "Найти файлы по шаблону, например **/*.cs или src/**/*.ts.",
                Prop("pattern", "string", "Шаблон поиска", true),
                Prop("path", "string", "Где искать (по умолчанию корень проекта)", false)));

            list.Add(Function("grep",
                "Найти строки по регулярному выражению во всех текстовых файлах.",
                Prop("pattern", "string", "Регулярное выражение", true),
                Prop("path", "string", "Каталог поиска (по умолчанию корень проекта)", false),
                Prop("include", "string", "Маска файлов, например *.cs", false)));

            list.Add(Function("bash",
                "Выполнить команду в cmd.exe внутри рабочей папки. Выводит stdout и stderr.",
                Prop("command", "string", "Команда для cmd.exe", true),
                Prop("timeout", "integer", "Таймаут в миллисекундах (по умолчанию 60000)", false)));

            list.Add(Function("webfetch",
                "Скачать страницу или файл по HTTP(S) и вернуть текст без HTML-разметки.",
                Prop("url", "string", "Адрес", true)));

            return list;
        }

        private static Dictionary<string, object> Function(string name, string description,
            Dictionary<string, object> p1,
            Dictionary<string, object> p2 = null,
            Dictionary<string, object> p3 = null,
            Dictionary<string, object> p4 = null)
        {
            var props = Json.NewObject();
            var required = new List<object>();
            AddProp(props, required, p1);
            AddProp(props, required, p2);
            AddProp(props, required, p3);
            AddProp(props, required, p4);

            var parameters = Json.NewObject();
            parameters["type"] = "object";
            parameters["properties"] = props;
            parameters["required"] = required;

            var fn = Json.NewObject();
            fn["name"] = name;
            fn["description"] = description;
            fn["parameters"] = parameters;

            var tool = Json.NewObject();
            tool["type"] = "function";
            tool["function"] = fn;
            return tool;
        }

        private static void AddProp(Dictionary<string, object> props, List<object> required, Dictionary<string, object> prop)
        {
            if (prop == null) return;
            string name = (string)prop["__name"];
            prop.Remove("__name");
            bool isRequired = (bool)prop["__required"];
            prop.Remove("__required");
            props[name] = prop;
            if (isRequired) required.Add(name);
        }

        private static Dictionary<string, object> Prop(string name, string type, string description, bool required)
        {
            var d = Json.NewObject();
            d["__name"] = name;
            d["__required"] = required;
            d["type"] = type;
            d["description"] = description;
            return d;
        }

        // ------------------------------------------------------------ запуск

        public static ToolResult Run(string name, string argumentsJson, string root, Func<bool> isCancelled)
        {
            Dictionary<string, object> args;
            try
            {
                args = Json.AsObject(Json.Parse(string.IsNullOrEmpty(argumentsJson) ? "{}" : argumentsJson));
                if (args == null) args = Json.NewObject();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail("Не удалось разобрать аргументы инструмента: " + ex.Message, name, "");
            }

            try
            {
                switch (name)
                {
                    case "read": return ReadFile(root, args);
                    case "write": return WriteFile(root, args);
                    case "edit": return EditFile(root, args);
                    case "ls": return ListDir(root, args);
                    case "glob": return Glob(root, args);
                    case "grep": return Grep(root, args, isCancelled);
                    case "bash": return Bash(root, args, isCancelled);
                    case "webfetch": return WebFetch(args);
                    default: return ToolResult.Fail("Неизвестный инструмент: " + name, name, "");
                }
            }
            catch (ToolException ex)
            {
                return ToolResult.Fail(ex.Message, name, Subtitle(name, args));
            }
            catch (Exception ex)
            {
                Log.Error("Инструмент " + name, ex);
                return ToolResult.Fail(ex.GetType().Name + ": " + ex.Message, name, Subtitle(name, args));
            }
        }

        private static string Subtitle(string name, Dictionary<string, object> args)
        {
            switch (name)
            {
                case "read": case "write": case "ls":
                    return Json.Str(args, "path", ".");
                case "edit":
                    return Json.Str(args, "path", ".");
                case "glob": case "grep":
                    return Json.Str(args, "pattern", "");
                case "bash":
                    return Util.Quote(Json.Str(args, "command", ""), 80);
                case "webfetch":
                    return Json.Str(args, "url", "");
                default:
                    return "";
            }
        }

        // ------------------------------------------------------------ read

        private static ToolResult ReadFile(string root, Dictionary<string, object> args)
        {
            string path = Json.Str(args, "path", "");
            string full = Util.ResolveInside(root, path);
            if (!File.Exists(full)) return ToolResult.Fail("Файл не найден: " + path, "read", path);
            if (!Util.IsProbablyText(full)) return ToolResult.Fail("Файл двоичный: " + path, "read", path);

            string content = File.ReadAllText(full, Encoding.UTF8);
            string[] lines = Util.NormalizeNewlines(content).Split('\n');
            int total = lines.Length;

            int offset = Json.Int(args, "offset", 1);
            if (offset < 1) offset = 1;
            int limit = Json.Int(args, "limit", 2000);
            if (limit <= 0) limit = 2000;

            int start = offset - 1;
            int count = Math.Min(limit, Math.Max(0, total - start));
            var sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                sb.Append((start + i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(5));
                sb.Append("| ");
                sb.AppendLine(lines[start + i]);
            }
            if (start + count < total)
                sb.AppendLine("… (осталось " + (total - start - count) + " строк, используйте offset/limit)");

            return ToolResult.Ok(sb.ToString(), "read", path);
        }

        // ------------------------------------------------------------ write

        private static ToolResult WriteFile(string root, Dictionary<string, object> args)
        {
            string path = Json.Str(args, "path", "");
            string content = Json.Str(args, "content", "");
            string full = Util.ResolveInside(root, path);

            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            bool existed = File.Exists(full);
            long oldSize = existed ? new FileInfo(full).Length : 0;

            File.WriteAllText(full, content, new UTF8Encoding(false));

            string summary = (existed ? "Перезаписан " : "Создан ") + Util.Relative(root, full)
                + " (" + content.Length + " символов, было " + Util.HumanSize(oldSize) + ")";
            return ToolResult.Ok(summary, "write", Util.Relative(root, full));
        }

        // ------------------------------------------------------------ edit

        private static ToolResult EditFile(string root, Dictionary<string, object> args)
        {
            string path = Json.Str(args, "path", "");
            string oldString = Json.Str(args, "oldString", "");
            string newString = Json.Str(args, "newString", "");
            bool replaceAll = Json.Bool(args, "replaceAll", false);

            string full = Util.ResolveInside(root, path);
            if (!File.Exists(full)) return ToolResult.Fail("Файл не найден: " + path, "edit", path);
            if (oldString.Length == 0) return ToolResult.Fail("oldString пуст", "edit", path);

            string content = File.ReadAllText(full, Encoding.UTF8);
            int occurrences = Util.CountOccurrences(content, oldString);

            if (occurrences == 0)
            {
                string normalized = Util.NormalizeNewlines(content);
                string normalizedOld = Util.NormalizeNewlines(oldString);
                if (normalized.IndexOf(normalizedOld, StringComparison.Ordinal) >= 0)
                    return ToolResult.Fail(
                        "Фрагмент найден только после нормализации переводов строк. " +
                        "Файл использует CRLF/LF иначе — повторите замену с точным текстом файла.",
                        "edit", path);

                return ToolResult.Fail("Фрагмент не найден в файле. Проверьте текст и отступы.", "edit", path);
            }

            if (occurrences > 1 && !replaceAll)
                return ToolResult.Fail(
                    "Фрагмент встречается " + occurrences + " раз(а). Уточните oldString или укажите replaceAll=true.",
                    "edit", path);

            string updated = replaceAll
                ? content.Replace(oldString, newString)
                : ReplaceFirst(content, oldString, newString);

            File.WriteAllText(full, updated, new UTF8Encoding(false));

            int delta = updated.Length - content.Length;
            string summary = "Заменено вхождений: " + (replaceAll ? occurrences : 1)
                + " в " + Util.Relative(root, full)
                + " (" + (delta >= 0 ? "+" : "") + delta + " символов)";
            return ToolResult.Ok(summary, "edit", Util.Relative(root, full));
        }

        private static string ReplaceFirst(string content, string oldString, string newString)
        {
            int index = content.IndexOf(oldString, StringComparison.Ordinal);
            return content.Substring(0, index) + newString + content.Substring(index + oldString.Length);
        }

        // ------------------------------------------------------------ ls

        private static ToolResult ListDir(string root, Dictionary<string, object> args)
        {
            string path = Json.Str(args, "path", ".");
            string full = Util.ResolveInside(root, path);
            if (!Directory.Exists(full)) return ToolResult.Fail("Каталог не найден: " + path, "ls", path);

            var sb = new StringBuilder();
            var dirs = Directory.GetDirectories(full);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (string d in dirs)
                sb.AppendLine("<dir>  " + Path.GetFileName(d) + "\\");

            var files = Directory.GetFiles(full);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                long size;
                try { size = new FileInfo(f).Length; } catch { size = 0; }
                sb.AppendLine(size.ToString().PadLeft(10) + "  " + Path.GetFileName(f));
            }

            if (sb.Length == 0) sb.AppendLine("(пусто)");
            return ToolResult.Ok(sb.ToString(), "ls", path);
        }

        // ------------------------------------------------------------ glob

        private static ToolResult Glob(string root, Dictionary<string, object> args)
        {
            string pattern = Json.Str(args, "pattern", "*");
            string path = Json.Str(args, "path", ".");
            string start = Util.ResolveInside(root, path);
            if (!Directory.Exists(start)) return ToolResult.Fail("Каталог не найден: " + path, "glob", pattern);

            Regex re = Util.GlobToRegex(pattern);
            var matches = new List<string>();

            foreach (string file in Util.EnumerateFilesSafe(start, 60000))
            {
                string rel = Util.Relative(root, file);
                if (re.IsMatch(rel) || re.IsMatch(Path.GetFileName(file)))
                {
                    matches.Add(rel);
                    if (matches.Count >= 500) break;
                }
            }

            matches.Sort(StringComparer.OrdinalIgnoreCase);
            if (matches.Count == 0) return ToolResult.Ok("Ничего не найдено по шаблону " + pattern, "glob", pattern);

            var sb = new StringBuilder();
            foreach (string m in matches) sb.AppendLine(m);
            if (matches.Count >= 500) sb.AppendLine("… показаны первые 500 совпадений");
            return ToolResult.Ok(sb.ToString(), "glob", pattern);
        }

        // ------------------------------------------------------------ grep

        private static ToolResult Grep(string root, Dictionary<string, object> args, Func<bool> isCancelled)
        {
            string pattern = Json.Str(args, "pattern", "");
            string path = Json.Str(args, "path", ".");
            string include = Json.Str(args, "include", "");
            if (pattern.Length == 0) return ToolResult.Fail("Пустой pattern", "grep", "");

            Regex re;
            try { re = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled); }
            catch (Exception ex) { return ToolResult.Fail("Плохое регулярное выражение: " + ex.Message, "grep", pattern); }

            string start = Util.ResolveInside(root, path);
            if (!Directory.Exists(start)) return ToolResult.Fail("Каталог не найден: " + path, "grep", pattern);

            Regex includeRe = string.IsNullOrEmpty(include) ? null : Util.GlobToRegex(include);
            var sb = new StringBuilder();
            int hits = 0;

            foreach (string file in Util.EnumerateFilesSafe(start, 40000))
            {
                if (isCancelled != null && isCancelled()) break;
                if (includeRe != null && !includeRe.IsMatch(Path.GetFileName(file))) continue;
                if (!Util.IsProbablyText(file)) continue;

                long size;
                try { size = new FileInfo(file).Length; } catch { continue; }
                if (size > 2 * 1024 * 1024) continue;

                string[] lines;
                try { lines = File.ReadAllLines(file, Encoding.UTF8); }
                catch { continue; }

                for (int i = 0; i < lines.Length; i++)
                {
                    if (!re.IsMatch(lines[i])) continue;
                    sb.Append(Util.Relative(root, file)).Append(':').Append(i + 1).Append(": ")
                      .AppendLine(lines[i].Length > 240 ? lines[i].Substring(0, 240) + "…" : lines[i]);
                    hits++;
                    if (hits >= 400)
                    {
                        sb.AppendLine("… показаны первые 400 совпадений");
                        goto done;
                    }
                }
            }

        done:
            if (hits == 0) return ToolResult.Ok("Совпадений нет", "grep", pattern);
            return ToolResult.Ok(sb.ToString(), "grep", pattern);
        }

        // ------------------------------------------------------------ bash

        [DllImport("kernel32.dll")]
        private static extern int GetOEMCP();

        private static ToolResult Bash(string root, Dictionary<string, object> args, Func<bool> isCancelled)
        {
            string command = Json.Str(args, "command", "");
            if (command.Trim().Length == 0) return ToolResult.Fail("Пустая команда", "bash", "");

            int timeout = Json.Int(args, "timeout", 60000);
            if (timeout <= 0 || timeout > 600000) timeout = 60000;

            var psi = new ProcessStartInfo();
            psi.FileName = "cmd.exe";
            psi.Arguments = "/c " + command;
            psi.WorkingDirectory = root;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            var stdout = new MemoryStream();
            var stderr = new MemoryStream();

            using (var process = new Process())
            {
                process.StartInfo = psi;
                process.Start();

                var t1 = new Thread(delegate () { CopyStream(process.StandardOutput.BaseStream, stdout); });
                var t2 = new Thread(delegate () { CopyStream(process.StandardError.BaseStream, stderr); });
                t1.IsBackground = true; t2.IsBackground = true;
                t1.Start(); t2.Start();

                var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
                bool killed = false;
                while (!process.HasExited)
                {
                    if (DateTime.UtcNow > deadline || (isCancelled != null && isCancelled()))
                    {
                        try { process.Kill(); } catch { }
                        killed = true;
                        break;
                    }
                    Thread.Sleep(40);
                }

                t1.Join(2000); t2.Join(2000);

                string outText = Decode(stdout);
                string errText = Decode(stderr);
                int exitCode = killed ? -1 : process.ExitCode;

                var sb = new StringBuilder();
                if (outText.Length > 0) sb.Append(outText);
                if (errText.Length > 0)
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine("[stderr]");
                    sb.Append(errText);
                }
                if (sb.Length == 0) sb.AppendLine("(нет вывода)");
                if (killed) sb.AppendLine().AppendLine("[прервано: таймаут или отмена]");
                sb.AppendLine().Append("[exit " + exitCode + "]");

                string result = Util.Truncate(sb.ToString(), MaxOutputChars);
                var res = ToolResult.Ok(result, "bash", Util.Quote(command, 80));
                res.IsError = exitCode != 0;
                return res;
            }
        }

        private static void CopyStream(Stream from, MemoryStream to)
        {
            try
            {
                var buf = new byte[8192];
                int read;
                while ((read = from.Read(buf, 0, buf.Length)) > 0)
                {
                    lock (to) to.Write(buf, 0, read);
                }
            }
            catch { }
        }

        private static string Decode(MemoryStream ms)
        {
            lock (ms)
            {
                byte[] bytes = ms.ToArray();
                if (bytes.Length == 0) return "";
                Encoding enc;
                try { enc = Encoding.GetEncoding(GetOEMCP()); }
                catch { enc = Encoding.Default; }
                return enc.GetString(bytes).TrimEnd('\r', '\n');
            }
        }

        // ------------------------------------------------------------ webfetch

        private static ToolResult WebFetch(Dictionary<string, object> args)
        {
            string url = Json.Str(args, "url", "");
            if (url.Length == 0) return ToolResult.Fail("Пустой url", "webfetch", "");

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "NovaCode/1.0 (Windows XP)";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.AllowAutoRedirect = true;

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string body = reader.ReadToEnd();
                string contentType = response.ContentType ?? "";

                if (contentType.IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0)
                    body = HtmlToText(body);

                return ToolResult.Ok(Util.Truncate(body, MaxOutputChars), "webfetch", url);
            }
        }

        /// <summary>Очень простой HTML→текст: без парсера, но для документации хватает.</summary>
        public static string HtmlToText(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";

            html = Regex.Replace(html, @"<script[\s\S]*?</script>", " ", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<style[\s\S]*?</style>", " ", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<!--[\s\S]*?-->", " ");
            html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"</(p|div|li|tr|h[1-6]|pre)>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<[^>]+>", " ");
            html = WebUtility_HtmlDecode(html);
            html = Regex.Replace(html, @"[ \t]+", " ");
            html = Regex.Replace(html, @"\n{3,}", "\n\n");
            return html.Trim();
        }

        private static string WebUtility_HtmlDecode(string text)
        {
            return text.Replace("&nbsp;", " ")
                       .Replace("&amp;", "&")
                       .Replace("&lt;", "<")
                       .Replace("&gt;", ">")
                       .Replace("&quot;", "\"")
                       .Replace("&#39;", "'");
        }
    }
}
