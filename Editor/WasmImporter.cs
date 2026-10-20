using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace LogicCuteGuy.Wasm2Hlsl
{
    /// <summary>
    /// Imports a .wasm as one asset. Per exported function it contains, as sub-assets:
    /// a Blit shader "WASM/Name/export", a material and an R32_SINT render texture (all named after the export).
    /// No files are written next to the .wasm.
    /// </summary>
    [ScriptedImporter(8, "wasm")]
    public class WasmImporter : ScriptedImporter
    {
        [Tooltip("Calls per Run() = width x height, for functions whose source has no @size tag.")]
        public int width = 256;
        public int height = 256;
        [Tooltip("Read back the trap flag (unreachable, divide by zero, runaway loop) for every function; @traps in source turns it on per function. Costs one more set of rows.")]
        public bool detectTraps;
        [Tooltip("Source file or folder (Rust, C/C++, Zig, AssemblyScript, TinyGo) to read argument names and docs from. Empty = the .wasm's folder and its src/.")]
        public string sourcePath = "";

        const string LibMarker = "__WASM_LIBRARY__";
        const string IconPath = "Packages/com.logiccuteguy.wasm2hlsl/Editor/Icons/WasmIcon.png";

        public override void OnImportAsset(AssetImportContext ctx)
        {
            string name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            var summary = new System.Text.StringBuilder();
            try
            {
                var module = WasmModule.Parse(File.ReadAllBytes(ctx.assetPath));
                string lib = WasmToHlsl.TranslateLibrary(module, name + ".wasm");
                var names = SourceNames.Find(ctx.assetPath, sourcePath);
                DependOnSources(ctx);
                foreach (var export in module.FuncExports.Keys)
                {
                    var ft = module.TypeOfFunc(module.FuncExports[export]);
                    names.TryGetValue(export, out var fn);
                    var paramNames = ft.Params.Select((_, i) => SourceNames.ParamName(fn, i, ft.Params.Length)).ToArray();
                    string src;
                    try { src = WasmToHlsl.GenerateShader(module, export, $"WASM/{name}/{export}", LibMarker, paramNames).Replace($"#include \"{LibMarker}\"", lib); }
                    catch (WasmTranslateException e) { ctx.LogImportWarning($"[WASM2HLSL] {ctx.assetPath}: skipped {e.Message}"); continue; } // e.g. v128 in the signature
                    var shader = ShaderUtil.CreateShaderAsset(ctx, src, false);
                    shader.name = $"WASM/{name}/{export}";
                    ctx.AddObjectToAsset(export + ".shader", shader);

                    // R32 is the only int format AsyncGPUReadback reads, so hi / trap go in extra planes of rows below lo.
                    var (w, h, traps, _) = Shape(fn);
                    int hiPlane = ft.Results.Length == 1 && ft.Results[0] == WasmType.I64 ? 1 : 0;
                    int trapPlane = traps ? 1 + hiPlane : 0;
                    int planes = 1 + hiPlane + (traps ? 1 : 0);
                    int rows = Mathf.Clamp(h, 1, 16384 / planes);
                    if (rows < h) ctx.LogImportWarning($"[WASM2HLSL] {export}: {w}x{h} calls do not fit a 16384-row texture with {planes} planes; using {w}x{rows}.");

                    var mat = new Material(shader) { name = export };
                    mat.SetInteger("_WasmPlaneRows", rows);
                    mat.SetInteger("_WasmHiPlane", hiPlane);
                    mat.SetInteger("_WasmTrapPlane", trapPlane);
                    ctx.AddObjectToAsset(export + ".mat", mat);

                    var rt = new RenderTexture(w, rows * planes, 0, GraphicsFormat.R32_SInt)
                        { name = export, filterMode = FilterMode.Point };
                    ctx.AddObjectToAsset(export + ".rt", rt);

                    if (!string.IsNullOrEmpty(fn?.Doc)) summary.AppendLine("// " + fn.Doc.Replace("\n", "\n// "));
                    summary.AppendLine($"{export}({string.Join(", ", ft.Params.Select((t, i) => paramNames[i] + ": " + t))}) -> {(ft.Results.Length == 0 ? "void" : ft.Results[0].ToString())}"
                        + $"  [{w}x{rows}{(traps ? ", traps" : "")}]");
                }
                if (module.FuncExports.Count == 0) ctx.LogImportWarning($"{ctx.assetPath} exports no functions.");
                summary.AppendLine($"GPU memory per call: {WasmToHlsl.MemoryBytes(module)} bytes");
            }
            catch (Exception e)
            {
                ctx.LogImportError($"[WASM2HLSL] {ctx.assetPath}: {e.Message}");
                summary.AppendLine("Import failed: " + e.Message);
            }

            // Main object is a text summary: Unity renames the main object to the file name, the export assets keep theirs.
            var main = new TextAsset(summary.ToString()) { name = name };
            ctx.DependsOnSourceAsset(IconPath);
            ctx.AddObjectToAsset("summary", main, AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath));
            ctx.SetMainObject(main);
        }

        /// <summary>
        /// Calls per Run() and trap check of one export: "@size WxH" or "@size N" (N calls, wrapped at 16384 per row)
        /// and "@traps" in the source comment win over the importer's defaults.
        /// </summary>
        public (int width, int height, bool traps, bool sizeFromSource) Shape(SourceFn fn)
        {
            bool traps = detectTraps || (fn?.Traps ?? false);
            if (fn != null && fn.Width > 0 && fn.Height > 0) return (Mathf.Min(fn.Width, 16384), fn.Height, traps, true);
            if (fn != null && fn.Count > 0)
            {
                int w = Mathf.Min(fn.Count, 16384);
                return (w, (fn.Count + w - 1) / w, traps, true);
            }
            return (Mathf.Clamp(width, 1, 16384), Mathf.Max(height, 1), traps, false);
        }

        /// <summary>Reimport when a source file inside the project changes (names are read from it).</summary>
        void DependOnSources(AssetImportContext ctx)
        {
            string dir = string.IsNullOrEmpty(sourcePath) ? Path.GetDirectoryName(ctx.assetPath) : sourcePath;
            var files = File.Exists(dir) ? new[] { dir }
                : Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories) : new string[0];
            foreach (string f in files.Select(f => f.Replace('\\', '/')))
                // Only project assets can be dependencies; a source outside the project needs a manual Reimport.
                if (SourceNames.IsSource(f) && !f.Contains("/target/") && (f.StartsWith("Assets/") || f.StartsWith("Packages/")))
                    ctx.DependsOnSourceAsset(f);
        }
    }

    [CustomEditor(typeof(WasmImporter))]
    public class WasmImporterEditor : ScriptedImporterEditor
    {
        public override void OnInspectorGUI()
        {
            var importer = (WasmImporter)target;
            WasmEditorLook.Header(Path.GetFileName(importer.assetPath), "WebAssembly \u2192 HLSL  \u00B7  drag into the Hierarchy to run it", WasmEditorLook.WasmIcon);

            serializedObject.Update();
            WasmEditorLook.Section("Defaults (functions without @size / @traps)");
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(WasmImporter.width)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(WasmImporter.height)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(WasmImporter.detectTraps)), new GUIContent("Trap Check"));
            WasmEditorLook.Section("Source");
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(WasmImporter.sourcePath)));
            serializedObject.ApplyModifiedProperties();
            ApplyRevertGUI();

            WasmEditorLook.Footer();
        }
    }
}
