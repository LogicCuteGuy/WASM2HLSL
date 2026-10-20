using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LogicCuteGuy.Wasm2Hlsl
{
    /// <summary>An export as written in source: parameter names, the comment above it and its @size / @traps tags.</summary>
    public sealed class SourceFn
    {
        public string[] Params;
        public string Doc;
        /// <summary>From "@size 1024x512" (width x height) or "@size 100000" (a call count; 0 = not given).</summary>
        public int Width, Height, Count;
        /// <summary>From "@traps".</summary>
        public bool Traps;
        /// <summary>File the export was found in (set by Find).</summary>
        public string File;
    }

    /// <summary>
    /// Release wasm keeps function names but drops parameter names, so they are read back from the source next to the .wasm.
    /// Rust (extern "C" fn), Zig (export fn), AssemblyScript (export function), TinyGo (//export, //go:wasmexport)
    /// and C/C++ (function definitions; export_name attributes honoured).
    /// </summary>
    public static class SourceNames
    {
        enum Style { Colon, CLike, Go }

        sealed class Lang
        {
            public string[] Ext; public Regex Def; public Style Style; public bool NeedsExportComment;
        }

        // ponytail: regexes, not parsers; fine for hand-written exports, add a real parser if macros generate them.
        const string P = @"\s*\((?<params>[^)]*)\)";
        static readonly Lang[] Langs =
        {
            new Lang { Ext = new[] { ".rs" }, Def = new Regex(@"\bextern\s+""C""\s+fn\s+(?<name>\w+)" + P), Style = Style.Colon },
            new Lang { Ext = new[] { ".zig" }, Def = new Regex(@"\bexport\s+fn\s+(?<name>\w+)" + P), Style = Style.Colon },
            new Lang { Ext = new[] { ".ts" }, Def = new Regex(@"\bexport\s+function\s+(?<name>\w+)" + P), Style = Style.Colon },
            new Lang { Ext = new[] { ".go" }, Def = new Regex(@"\bfunc\s+(?<name>\w+)" + P), Style = Style.Go, NeedsExportComment = true },
            new Lang { Ext = new[] { ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp" },
                Def = new Regex(@"^[ \t]*(?:[\w*]+[ \t*]+)+(?<name>\w+)\s*\((?<params>[^;{)]*)\)\s*\{", RegexOptions.Multiline), Style = Style.CLike },
        };

        static readonly Regex SizeTag = new Regex(@"@size\s+(\d+)(?:\s*[x×*]\s*(\d+))?");
        static readonly Regex TrapsTag = new Regex(@"@traps\b");
        static readonly string[] CKeywords = { "if", "while", "for", "switch", "return", "sizeof", "else", "do" };
        static readonly Regex ExportName = new Regex(@"export_name\s*(?:=|\()\s*""(?<n>[^""]+)""|//\s*(?:go:wasm)?export\s+(?<n>\w+)");

        /// <summary>Exports found for the .wasm at <paramref name="wasmPath"/>. <paramref name="source"/>: a source file or folder (empty = the .wasm's folder; its src/ is searched too).</summary>
        public static Dictionary<string, SourceFn> Find(string wasmPath, string source)
        {
            var result = new Dictionary<string, SourceFn>();
            string dir = string.IsNullOrEmpty(source) ? Path.GetDirectoryName(wasmPath) : source;
            IEnumerable<string> files;
            if (File.Exists(dir)) files = new[] { dir };
            else if (Directory.Exists(dir))
            {
                files = Directory.GetFiles(dir);
                string src = Path.Combine(dir, "src");
                if (Directory.Exists(src)) files = files.Concat(Directory.GetFiles(src, "*", SearchOption.AllDirectories));
            }
            else return result;

            foreach (string f in files)
            {
                var lang = Langs.FirstOrDefault(l => l.Ext.Contains(Path.GetExtension(f).ToLowerInvariant()));
                if (lang == null) continue;
                foreach (var kv in Parse(File.ReadAllText(f), lang)) { kv.Value.File = f; result[kv.Key] = kv.Value; }
            }
            return result;
        }

        public static bool IsSource(string path) => Langs.Any(l => l.Ext.Contains(Path.GetExtension(path).ToLowerInvariant()));

        /// <summary>Parses one source text; <paramref name="extension"/> picks the language (".rs", ".c", ...).</summary>
        public static Dictionary<string, SourceFn> Parse(string text, string extension) =>
            Parse(text, Langs.First(l => l.Ext.Contains(extension)));

        static Dictionary<string, SourceFn> Parse(string text, Lang lang)
        {
            var result = new Dictionary<string, SourceFn>();
            text = text.Replace("\r\n", "\n");
            foreach (Match m in lang.Def.Matches(text))
            {
                string name = m.Groups["name"].Value;
                if (lang.Style == Style.CLike && CKeywords.Contains(name)) continue;

                // Lines right above the definition: comments become the doc, attributes / export comments may rename it.
                var above = new List<string>();
                var lines = text.Substring(0, m.Index).Split('\n');
                for (int i = lines.Length - 2; i >= 0; i--)
                {
                    string l = lines[i].Trim();
                    if (l.StartsWith("//") || l.StartsWith("/*") || l.StartsWith("*") || l.StartsWith("#[") || l.StartsWith("@")
                        || l.StartsWith("__attribute__") || l.StartsWith("[[")) above.Insert(0, l);
                    else break;
                }
                string prefix = string.Join("\n", above) + "\n" + lines[lines.Length - 1];
                var export = ExportName.Match(prefix);
                if (export.Success) name = export.Groups["n"].Value;
                else if (lang.NeedsExportComment) continue;

                string ps = Regex.Replace(m.Groups["params"].Value, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
                var fn = new SourceFn { Params = ParamNames(ps, lang.Style) };
                var docLines = above
                    .Where(l => (l.StartsWith("//") || l.StartsWith("/*") || l.StartsWith("*")) && !ExportName.IsMatch(l))
                    .Select(l => l.TrimStart('/', '*', '!').TrimEnd('/', '*').Trim())
                    .Where(l => l.Length > 0).ToList();
                var kept = new List<string>();
                foreach (string l in docLines)
                {
                    var size = SizeTag.Match(l);
                    if (size.Success)
                    {
                        if (size.Groups[2].Success) { fn.Width = int.Parse(size.Groups[1].Value); fn.Height = int.Parse(size.Groups[2].Value); }
                        else fn.Count = int.Parse(size.Groups[1].Value);
                    }
                    if (TrapsTag.IsMatch(l)) fn.Traps = true;
                    string rest = TrapsTag.Replace(SizeTag.Replace(l, ""), "").Trim();
                    if (rest.Length > 0) kept.Add(rest);
                }
                fn.Doc = string.Join("\n", kept);
                result[name] = fn;
            }
            return result;
        }

        static string[] ParamNames(string ps, Style style)
        {
            var parts = ps.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0 && a != "void" && a != "...");
            switch (style)
            {
                case Style.Colon: // name: Type (Rust `mut x`, Zig `comptime x`)
                    return parts.Select(a => Regex.Replace(a.Split(':')[0].Trim(), @"^(?:mut|comptime|noalias)\s+", "")).ToArray();
                case Style.Go: // a, b int32 / x float32
                    return parts.Select(a => a.Split(' ')[0]).ToArray();
                default: // C: int32_t x, float *p, int arr[4]
                    return parts.Select(a => Regex.Match(Regex.Replace(a, @"\[[^\]]*\]", ""), @"(\w+)\s*$").Groups[1].Value).ToArray();
            }
        }

        /// <summary>Parameter name from source, or "argN" when unknown or the counts disagree (stale source).</summary>
        public static string ParamName(SourceFn fn, int i, int count) =>
            fn != null && fn.Params.Length == count && fn.Params[i].Length > 0 ? fn.Params[i] : "arg" + i;
    }
}
