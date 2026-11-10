using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NovaCode.Core
{
    /// <summary>
    /// Минимальный JSON-парсер и сериализатор.
    /// Написан вручную, потому что на .NET 4.0 нет System.Text.Json,
    /// а тащить Newtonsoft.Json в одиночный EXE для XP не хочется.
    /// Значения: Dictionary&lt;string,object&gt;, List&lt;object&gt;, string, double, bool, null.
    /// </summary>
    public static class Json
    {
        // ------------------------------------------------------------ разбор

        public static object Parse(string text)
        {
            if (text == null) throw new JsonException("Пустой JSON");
            var p = new Parser(text);
            p.SkipWhitespace();
            object value = p.ParseValue();
            p.SkipWhitespace();
            return value;
        }

        /// <summary>Разбор с возвратом пустого объекта при ошибке — удобно для настроек.</summary>
        public static object TryParse(string text, object fallback)
        {
            try { return Parse(text); }
            catch { return fallback; }
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s) { _s = s; _i = 0; }

            public void SkipWhitespace()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') _i++;
                    else break;
                }
            }

            private char Peek()
            {
                if (_i >= _s.Length) throw new JsonException("Неожиданный конец JSON");
                return _s[_i];
            }

            public object ParseValue()
            {
                SkipWhitespace();
                char c = Peek();
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return ParseNumber();
                }
            }

            private void Expect(string word)
            {
                if (_i + word.Length > _s.Length || string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0)
                    throw new JsonException("Ожидалось '" + word + "' на позиции " + _i);
                _i += word.Length;
            }

            private Dictionary<string, object> ParseObject()
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                _i++; // {
                SkipWhitespace();
                if (Peek() == '}') { _i++; return result; }

                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"') throw new JsonException("Ожидался ключ объекта на позиции " + _i);
                    string key = ParseString();
                    SkipWhitespace();
                    if (Peek() != ':') throw new JsonException("Ожидалось ':' на позиции " + _i);
                    _i++;
                    result[key] = ParseValue();
                    SkipWhitespace();
                    char c = Peek();
                    if (c == ',') { _i++; continue; }
                    if (c == '}') { _i++; return result; }
                    throw new JsonException("Ожидалось ',' или '}' на позиции " + _i);
                }
            }

            private List<object> ParseArray()
            {
                var result = new List<object>();
                _i++; // [
                SkipWhitespace();
                if (Peek() == ']') { _i++; return result; }

                while (true)
                {
                    result.Add(ParseValue());
                    SkipWhitespace();
                    char c = Peek();
                    if (c == ',') { _i++; continue; }
                    if (c == ']') { _i++; return result; }
                    throw new JsonException("Ожидалось ',' или ']' на позиции " + _i);
                }
            }

            private string ParseString()
            {
                var sb = new StringBuilder();
                _i++; // "
                while (true)
                {
                    if (_i >= _s.Length) throw new JsonException("Незакрытая строка");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }

                    if (_i >= _s.Length) throw new JsonException("Незакрытый escape");
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw new JsonException("Битый \\u escape");
                            string hex = _s.Substring(_i, 4);
                            _i += 4;
                            sb.Append((char)ushort.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            break;
                        default: throw new JsonException("Неизвестный escape \\" + e);
                    }
                }
            }

            private double ParseNumber()
            {
                int start = _i;
                if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+')) _i++;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '-' || c == '+') _i++;
                    else break;
                }
                if (start == _i) throw new JsonException("Ожидалось число на позиции " + start);
                return double.Parse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }

        // ------------------------------------------------------------ запись

        public static string Stringify(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, false);
            return sb.ToString();
        }

        public static string StringifyIndented(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, true);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value, bool pretty)
        {
            Write(sb, value, pretty, 0);
        }

        private static void Write(StringBuilder sb, object value, bool pretty, int depth)
        {
            if (value == null) { sb.Append("null"); return; }

            if (value is string) { WriteString(sb, (string)value); return; }
            if (value is bool) { sb.Append(((bool)value) ? "true" : "false"); return; }

            if (value is double || value is float || value is int || value is long || value is decimal || value is short || value is byte)
            {
                double d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("0"); return; }
                if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
                    sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
                else
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            var dict = value as IDictionary<string, object>;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    if (pretty) { sb.Append('\n'); Indent(sb, depth + 1); }
                    WriteString(sb, kv.Key);
                    sb.Append(pretty ? ": " : ":");
                    Write(sb, kv.Value, pretty, depth + 1);
                }
                if (pretty && !first) { sb.Append('\n'); Indent(sb, depth); }
                sb.Append('}');
                return;
            }

            var list = value as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    if (pretty) { sb.Append('\n'); Indent(sb, depth + 1); }
                    Write(sb, item, pretty, depth + 1);
                }
                if (pretty && !first) { sb.Append('\n'); Indent(sb, depth); }
                sb.Append(']');
                return;
            }

            WriteString(sb, value.ToString());
        }

        private static void Indent(StringBuilder sb, int depth)
        {
            for (int i = 0; i < depth; i++) sb.Append("  ");
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ' || c == '\u2028' || c == '\u2029')
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------ доступ

        public static Dictionary<string, object> AsObject(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static List<object> AsArray(object value)
        {
            return value as List<object>;
        }

        public static string Str(Dictionary<string, object> d, string key, string fallback)
        {
            if (d == null) return fallback;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            if (v is string) return (string)v;
            if (v is bool) return ((bool)v) ? "true" : "false";
            if (v is double) return ((double)v).ToString(CultureInfo.InvariantCulture);
            return v.ToString();
        }

        public static double Num(Dictionary<string, object> d, string key, double fallback)
        {
            if (d == null) return fallback;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            if (v is double) return (double)v;
            double parsed;
            if (v is string && double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
            return fallback;
        }

        public static int Int(Dictionary<string, object> d, string key, int fallback)
        {
            return (int)Num(d, key, fallback);
        }

        public static bool Bool(Dictionary<string, object> d, string key, bool fallback)
        {
            if (d == null) return fallback;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            if (v is bool) return (bool)v;
            string s = v as string;
            if (s != null) return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
            if (v is double) return (double)v != 0;
            return fallback;
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            if (!d.TryGetValue(key, out v)) return null;
            return v as Dictionary<string, object>;
        }

        public static List<object> Arr(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            if (!d.TryGetValue(key, out v)) return null;
            return v as List<object>;
        }

        public static Dictionary<string, object> NewObject()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }
    }

    public sealed class JsonException : Exception
    {
        public JsonException(string message) : base(message) { }
    }
}
