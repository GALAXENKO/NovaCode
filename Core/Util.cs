using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NovaCode.Core
{
    public static class Util
    {
        public static string Truncate(string text, int maxChars)
        {
            if (text == null) return "";
            if (text.Length <= maxChars) return text;
            return text.Substring(0, maxChars) + "\n… (обрезано, всего " + text.Length.ToString("N0", CultureInfo.InvariantCulture) + " символов)";
        }

        /// <summary>Оставляет первые и последние строки — как это делает OpenCode для длинных выводов.</summary>
        public static string TruncateMiddle(string text, int maxLines)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length <= maxLines) return text;

            int head = maxLines / 2;
            int tail = maxLines - head;
            var sb = new StringBuilder();
            for (int i = 0; i < head; i++) sb.AppendLine(lines[i]);
            sb.AppendLine("… (" + (lines.Length - maxLines).ToString("N0", CultureInfo.InvariantCulture) + " строк скрыто) …");
            for (int i = lines.Length - tail; i < lines.Length; i++) sb.AppendLine(lines[i]);
            return sb.ToString();
        }

        public static string HumanSize(long bytes)
        {
            if (bytes < 1024) return bytes + " Б";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " КБ";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " МБ";
            return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " ГБ";
        }

        public static string NowStamp()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        public static string ShortTime()
        {
            return DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Безопасное имя файла (для сессий). Разделители пути и запрещённые в NTFS символы
        /// заменяются на подчёркивание, чтобы имя сессии никогда не вышло за пределы папки.
        /// </summary>
        public static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "session";

            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '.') sb.Append(c);
                else sb.Append('_');
            }

            // Схлопываем повторы: "a  //  b" → "a_b", а не "a____b".
            var clean = new StringBuilder(sb.Length);
            foreach (char c in sb.ToString())
            {
                if (c == '_' && clean.Length > 0 && clean[clean.Length - 1] == '_') continue;
                clean.Append(c);
            }

            string s = clean.ToString().Trim('_', '.', ' ');
            if (s.Length > 48) s = s.Substring(0, 48).TrimEnd('_', '.', ' ');
            return s.Length == 0 ? "session" : s;
        }

        /// <summary>
        /// Приводит путь к абсолютному внутри рабочей папки.
        /// Абсолютные пути вне папки проекта запрещены — агент не должен лазить по всей системе.
        /// </summary>
        public static string ResolveInside(string root, string path)
        {
            if (string.IsNullOrEmpty(path)) path = ".";
            path = path.Replace('/', '\\');

            string full;
            if (Path.IsPathRooted(path)) full = Path.GetFullPath(path);
            else full = Path.GetFullPath(Path.Combine(root, path));

            string rootFull = Path.GetFullPath(root).TrimEnd('\\');
            if (!full.Equals(rootFull, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException("Путь вне рабочей папки: " + path);
            }
            return full;
        }

        public static string Relative(string root, string full)
        {
            try
            {
                string rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
                string f = Path.GetFullPath(full);
                if (f.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return f.Substring(rootFull.Length);
                return f;
            }
            catch { return full; }
        }

        /// <summary>Шаблон glob → регулярное выражение. Поддерживает **, * и ?.</summary>
        public static Regex GlobToRegex(string pattern)
        {
            pattern = pattern.Replace('/', '\\');
            var sb = new StringBuilder("^");
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c == '*')
                {
                    bool doubleStar = i + 1 < pattern.Length && pattern[i + 1] == '*';
                    if (doubleStar)
                    {
                        i++;
                        if (i + 1 < pattern.Length && pattern[i + 1] == '\\') { i++; sb.Append("(?:.*\\\\)?"); }
                        else sb.Append(".*");
                    }
                    else sb.Append("[^\\\\]*");
                }
                else if (c == '?') sb.Append("[^\\\\]");
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append("$");
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        /// <summary>Грубая эвристика: файл двоичный (есть нулевые байты в первых 8 КБ).</summary>
        public static bool LooksBinary(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var buf = new byte[8192];
                    int read = fs.Read(buf, 0, buf.Length);
                    for (int i = 0; i < read; i++) if (buf[i] == 0) return true;
                    return false;
                }
            }
            catch { return true; }
        }

        public static bool IsProbablyText(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".exe": case ".dll": case ".obj": case ".lib": case ".pdb":
                case ".png": case ".jpg": case ".jpeg": case ".gif": case ".bmp": case ".ico":
                case ".zip": case ".gz": case ".7z": case ".rar": case ".cab":
                case ".mp3": case ".mp4": case ".avi": case ".wav": case ".ogg":
                case ".pdf": case ".doc": case ".xls": case ".ppt":
                case ".db": case ".mdb": case ".pdbx": case ".chm":
                    return false;
                default:
                    return !LooksBinary(path);
            }
        }

        public static string NormalizeNewlines(string s)
        {
            if (s == null) return "";
            return s.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        /// <summary>Номера строк как в OpenCode: "   1| текст".</summary>
        public static string WithLineNumbers(string content, int startLine)
        {
            string[] lines = NormalizeNewlines(content).Split('\n');
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                sb.Append((startLine + i).ToString(CultureInfo.InvariantCulture).PadLeft(5));
                sb.Append("| ");
                sb.AppendLine(lines[i]);
            }
            return sb.ToString();
        }

        public static string Quote(string text, int max)
        {
            if (text == null) return "\"\"";
            text = text.Replace("\n", "\\n").Replace("\r", "");
            if (text.Length > max) text = text.Substring(0, max) + "…";
            return "\"" + text + "\"";
        }

        public static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
            int count = 0, index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        public static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int nl = text.IndexOfAny(new[] { '\r', '\n' });
            string line = nl < 0 ? text : text.Substring(0, nl);
            return line.Length > 90 ? line.Substring(0, 90) + "…" : line;
        }

        public static IEnumerable<string> EnumerateFilesSafe(string root, int limit)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            int produced = 0;

            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git", "node_modules", "bin", "obj", ".vs", "packages", ".svn", ".hg", "__pycache__"
            };

            while (stack.Count > 0 && produced < limit)
            {
                string dir = stack.Pop();
                string[] files;
                string[] dirs;
                try
                {
                    files = Directory.GetFiles(dir);
                    dirs = Directory.GetDirectories(dir);
                }
                catch { continue; }

                foreach (string f in files)
                {
                    if (produced >= limit) break;
                    produced++;
                    yield return f;
                }
                foreach (string d in dirs)
                {
                    string name = Path.GetFileName(d);
                    if (skip.Contains(name)) continue;
                    try
                    {
                        var attrs = File.GetAttributes(d);
                        if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    stack.Push(d);
                }
            }
        }
    }

    /// <summary>Ошибка инструмента — показывается модели как результат, а не как падение приложения.</summary>
    public sealed class ToolException : Exception
    {
        public ToolException(string message) : base(message) { }
    }
}
