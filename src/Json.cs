using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ChromaPanel
{
    // Minimal, dependency-free JSON reader/writer.
    // Values map to: Dictionary<string,object>, List<object>, string, double, bool, null.
    internal static class Json
    {
        // ---------------- write ----------------
        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        static void Indent(StringBuilder sb, int depth)
        {
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        static void WriteValue(StringBuilder sb, object v, int depth)
        {
            if (v == null) { sb.Append("null"); return; }

            var dict = v as IDictionary;
            if (dict != null)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Indent(sb, depth + 1);
                    WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(": ");
                    WriteValue(sb, e.Value, depth + 1);
                }
                Indent(sb, depth);
                sb.Append('}');
                return;
            }

            var list = v as IList;
            if (list != null)
            {
                if (list.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Indent(sb, depth + 1);
                    WriteValue(sb, list[i], depth + 1);
                }
                Indent(sb, depth);
                sb.Append(']');
                return;
            }

            if (v is string) { WriteString(sb, (string)v); return; }
            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }

            if (v is double || v is float || v is decimal)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append('0'); return; }
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (v is int || v is long || v is short || v is byte || v is uint || v is ulong)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }

            WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
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
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------- read ----------------
        public static object Read(string text)
        {
            int i = 0;
            return ParseValue(text, ref i);
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end of JSON");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (Match(s, ref i, "true")) return true;
            if (Match(s, ref i, "false")) return false;
            if (Match(s, ref i, "null")) return null;
            return ParseNumber(s, ref i);
        }

        static bool Match(string s, ref int i, string lit)
        {
            if (i + lit.Length > s.Length) return false;
            if (string.CompareOrdinal(s, i, lit, 0, lit.Length) != 0) return false;
            i += lit.Length;
            return true;
        }

        static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            i++; // consume {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':'");
                i++;
                d[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("expected ',' or '}'");
            }
        }

        static List<object> ParseArray(string s, ref int i)
        {
            var l = new List<object>();
            i++; // consume [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return l; }
                throw new FormatException("expected ',' or ']'");
            }
        }

        static string ParseString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new FormatException("expected string");
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
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
                        if (i + 4 <= s.Length)
                        {
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("unterminated string");
        }

        static double ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && ((s[i] >= '0' && s[i] <= '9') || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '-' || s[i] == '+')) i++;
            if (i == start) throw new FormatException("invalid number");
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        // ---------------- helpers ----------------
        public static object Get(object v, string key)
        {
            var d = v as Dictionary<string, object>;
            if (d == null) return null;
            object r;
            return d.TryGetValue(key, out r) ? r : null;
        }

        public static double Num(object v, double fallback)
        {
            if (v == null) return fallback;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static int Int(object v, int fallback)
        {
            if (v == null) return fallback;
            try { return (int)Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture)); }
            catch { return fallback; }
        }

        public static bool Bool(object v, bool fallback)
        {
            if (v is bool) return (bool)v;
            if (v == null) return fallback;
            try { return Convert.ToBoolean(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static string Str(object v, string fallback)
        {
            var s = v as string;
            return string.IsNullOrEmpty(s) ? fallback : s;
        }
    }
}
