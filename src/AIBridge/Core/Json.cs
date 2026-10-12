using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace AIBridge
{
    /// <summary>
    /// Minimal JSON reader/writer. KSP ships no JSON library that handles free-form objects,
    /// so requests are parsed into Dictionary / List / string / double / bool / null.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            var p = new Parser(text);
            object v = p.Value();
            p.SkipWs();
            if (!p.AtEnd) throw new FormatException("Unexpected trailing characters in JSON at offset " + p.Pos);
            return v;
        }

        public static string Write(object value)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object v, int depth)
        {
            if (depth > 48) { sb.Append("null"); return; }
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case double d: WriteNumber(sb, d); return;
                case float f: WriteNumber(sb, f); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case uint u: sb.Append(u.ToString(CultureInfo.InvariantCulture)); return;
                case ulong ul: sb.Append(ul.ToString(CultureInfo.InvariantCulture)); return;
                case short sh: sb.Append(sh.ToString(CultureInfo.InvariantCulture)); return;
                case byte by: sb.Append(by.ToString(CultureInfo.InvariantCulture)); return;
                case Enum e: WriteString(sb, e.ToString()); return;
                case Vector3d vd: WriteVec(sb, vd.x, vd.y, vd.z); return;
                case Vector3 vf: WriteVec(sb, vf.x, vf.y, vf.z); return;
                case RawJson raw: sb.Append(raw.Text); return;
                case IDictionary dict:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (DictionaryEntry kv in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                        sb.Append(':');
                        WriteValue(sb, kv.Value, depth + 1);
                    }
                    sb.Append('}');
                    return;
                }
                case IEnumerable seq:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (object item in seq)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteValue(sb, item, depth + 1);
                    }
                    sb.Append(']');
                    return;
                }
                default: WriteString(sb, v.ToString()); return;
            }
        }

        static void WriteVec(StringBuilder sb, double x, double y, double z)
        {
            sb.Append('['); WriteNumber(sb, x); sb.Append(','); WriteNumber(sb, y); sb.Append(','); WriteNumber(sb, z); sb.Append(']');
        }

        static void WriteNumber(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
            if (d == Math.Floor(d) && Math.Abs(d) < 1e15) { sb.Append(((long)d).ToString(CultureInfo.InvariantCulture)); return; }
            sb.Append(d.ToString("G10", CultureInfo.InvariantCulture));
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
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        sealed class Parser
        {
            readonly string s;
            int i;
            public Parser(string text) { s = text ?? ""; }
            public int Pos => i;
            public bool AtEnd => i >= s.Length;

            public void SkipWs() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

            public object Value()
            {
                SkipWs();
                if (AtEnd) throw new FormatException("Unexpected end of JSON");
                char c = s[i];
                if (c == '{') return Obj();
                if (c == '[') return Arr();
                if (c == '"') return Str();
                if (c == 't') { Expect("true"); return true; }
                if (c == 'f') { Expect("false"); return false; }
                if (c == 'n') { Expect("null"); return null; }
                return Num();
            }

            void Expect(string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException("Invalid JSON token at offset " + i);
                i += word.Length;
            }

            Dictionary<string, object> Obj()
            {
                var d = new Dictionary<string, object>();
                i++;
                SkipWs();
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    SkipWs();
                    if (AtEnd || s[i] != '"') throw new FormatException("Expected object key at offset " + i);
                    string key = Str();
                    SkipWs();
                    if (AtEnd || s[i] != ':') throw new FormatException("Expected ':' at offset " + i);
                    i++;
                    d[key] = Value();
                    SkipWs();
                    if (AtEnd) throw new FormatException("Unterminated object");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new FormatException("Expected ',' or '}' at offset " + i);
                }
            }

            List<object> Arr()
            {
                var list = new List<object>();
                i++;
                SkipWs();
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(Value());
                    SkipWs();
                    if (AtEnd) throw new FormatException("Unterminated array");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return list; }
                    throw new FormatException("Expected ',' or ']' at offset " + i);
                }
            }

            string Str()
            {
                var sb = new StringBuilder();
                i++;
                while (true)
                {
                    if (AtEnd) throw new FormatException("Unterminated string");
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (AtEnd) throw new FormatException("Unterminated escape");
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new FormatException("Bad \\u escape");
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: sb.Append(e); break;
                    }
                }
            }

            object Num()
            {
                int start = i;
                while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                if (i == start) throw new FormatException("Invalid JSON value at offset " + i);
                return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Pre-serialised JSON that the writer emits verbatim.</summary>
    public sealed class RawJson
    {
        public readonly string Text;
        public RawJson(string text) { Text = text; }
    }

    /// <summary>Thrown by API handlers for problems the caller should see as a normal error message.</summary>
    public sealed class ApiException : Exception
    {
        public ApiException(string message) : base(message) { }
    }

    /// <summary>Typed access to a request's parameter object.</summary>
    public sealed class Args
    {
        public readonly Dictionary<string, object> Map;

        public Args(object source)
        {
            Map = source as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        public bool Has(string key) => Map.TryGetValue(key, out object v) && v != null;

        public object Raw(string key) => Map.TryGetValue(key, out object v) ? v : null;

        public string Str(string key, string fallback = null)
        {
            object v = Raw(key);
            if (v == null) return fallback;
            if (v is string s) return s;
            if (v is double d) return d.ToString(CultureInfo.InvariantCulture);
            if (v is bool b) return b ? "true" : "false";
            return fallback;
        }

        public string Require(string key)
        {
            string s = Str(key);
            if (string.IsNullOrEmpty(s)) throw new ApiException("Missing required parameter '" + key + "'");
            return s;
        }

        public double Num(string key, double fallback = 0)
        {
            object v = Raw(key);
            if (v is double d) return d;
            if (v is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) return parsed;
            if (v is bool b) return b ? 1 : 0;
            return fallback;
        }

        public double RequireNum(string key)
        {
            if (!Has(key)) throw new ApiException("Missing required parameter '" + key + "'");
            double d = Num(key, double.NaN);
            if (double.IsNaN(d)) throw new ApiException("Parameter '" + key + "' must be a number");
            return d;
        }

        public int Int(string key, int fallback = 0) => (int)Math.Round(Num(key, fallback));

        public bool Bool(string key, bool fallback = false)
        {
            object v = Raw(key);
            if (v is bool b) return b;
            if (v is double d) return d != 0;
            if (v is string s)
            {
                if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("on", StringComparison.OrdinalIgnoreCase)) return true;
                if (s.Equals("false", StringComparison.OrdinalIgnoreCase) || s == "0" || s.Equals("off", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return fallback;
        }

        public Args Obj(string key) => Raw(key) is Dictionary<string, object> d ? new Args(d) : null;

        public List<object> List(string key) => Raw(key) as List<object>;

        public List<string> Strings(string key)
        {
            var result = new List<string>();
            object v = Raw(key);
            if (v is string single) { result.Add(single); return result; }
            if (v is List<object> list)
                foreach (object o in list)
                    if (o != null) result.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
            return result;
        }
    }

    /// <summary>Insertion-ordered dictionary builder so responses read naturally.</summary>
    public sealed class JObj : IDictionary
    {
        readonly List<DictionaryEntry> items = new List<DictionaryEntry>();

        public JObj Add(string key, object value) { items.Add(new DictionaryEntry(key, value)); return this; }

        public JObj AddIf(bool condition, string key, object value) { if (condition) Add(key, value); return this; }

        public object this[string key]
        {
            get { foreach (var e in items) if ((string)e.Key == key) return e.Value; return null; }
            set
            {
                for (int n = 0; n < items.Count; n++)
                    if ((string)items[n].Key == key) { items[n] = new DictionaryEntry(key, value); return; }
                items.Add(new DictionaryEntry(key, value));
            }
        }

        // IDictionary plumbing: only enumeration is used by the writer.
        public IDictionaryEnumerator GetEnumerator() => new Enumerator(items);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public int Count => items.Count;
        public bool IsFixedSize => false;
        public bool IsReadOnly => false;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public ICollection Keys { get { var k = new List<object>(); foreach (var e in items) k.Add(e.Key); return k; } }
        public ICollection Values { get { var v = new List<object>(); foreach (var e in items) v.Add(e.Value); return v; } }
        object IDictionary.this[object key] { get => this[(string)key]; set => this[(string)key] = value; }
        void IDictionary.Add(object key, object value) => Add((string)key, value);
        public void Clear() => items.Clear();
        public bool Contains(object key) { foreach (var e in items) if (Equals(e.Key, key)) return true; return false; }
        public void Remove(object key) => items.RemoveAll(e => Equals(e.Key, key));
        public void CopyTo(Array array, int index) { foreach (var e in items) array.SetValue(e, index++); }

        sealed class Enumerator : IDictionaryEnumerator
        {
            readonly List<DictionaryEntry> list;
            int pos = -1;
            public Enumerator(List<DictionaryEntry> l) { list = l; }
            public DictionaryEntry Entry => list[pos];
            public object Key => list[pos].Key;
            public object Value => list[pos].Value;
            public object Current => list[pos];
            public bool MoveNext() => ++pos < list.Count;
            public void Reset() => pos = -1;
        }
    }
}
