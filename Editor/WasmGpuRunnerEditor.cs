using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace LogicCuteGuy.Wasm2Hlsl
{
    /// <summary>
    /// Before a VRChat Build &amp; Test or upload: re-sync stale runners, like entering Play mode does. Otherwise a runner whose
    /// .wasm gained an argument ships with its old table, and the missing argument silently stays 0 in game.
    /// </summary>
    internal class WasmRunnerBuildRefresh : VRC.SDKBase.Editor.BuildPipeline.IVRCSDKBuildRequestedCallback
    {
        public int callbackOrder => 90; // before UdonSharp compiles (100)

        public bool OnBuildRequested(VRC.SDKBase.Editor.BuildPipeline.VRCSDKRequestedBuildType type, BuildTarget target)
        {
            if (type == VRC.SDKBase.Editor.BuildPipeline.VRCSDKRequestedBuildType.Scene) WasmRunnerSetup.RefreshAll();
            return true;
        }
    }

    /// <summary>Drag a .wasm into the Hierarchy or Scene view to get a ready-to-run <see cref="WasmGpuRunner"/>.</summary>
    public static class WasmRunnerSetup
    {
        [InitializeOnLoadMethod]
        static void HookDragAndDrop()
        {
            DragAndDrop.AddDropHandler(OnHierarchyDrop);
            DragAndDrop.AddDropHandler(OnSceneDrop);
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpened += (_, __) => RefreshAll();
            EditorApplication.playModeStateChanged += s => { if (s == PlayModeStateChange.ExitingEditMode) { RefreshAll(); WarmShaders(); } };
        }

        /// <summary>
        /// Compiles every runner's shader for the GPU before Play. Otherwise the first VRCGraphics.Blit compiles it inside Udon,
        /// and a big kernel (soft-float f64 takes tens of seconds) trips Udon's 10 s limit and halts the behaviour.
        /// Compiled variants are cached in Library/ShaderCache, so this is only slow the first time after an import.
        /// </summary>
        static void WarmShaders()
        {
            var mats = Object.FindObjectsOfType<WasmGpuRunner>(true).SelectMany(r => r.functionMaterials).Where(m => m).Distinct().ToArray();
            try
            {
                for (int i = 0; i < mats.Length; i++)
                {
                    EditorUtility.DisplayProgressBar("WASM2HLSL", "Compiling " + mats[i].shader.name, (float)i / mats.Length);
                    ShaderUtil.CompilePass(mats[i], 0, true);
                }
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        /// <summary>Re-syncs every runner in the open scenes whose .wasm (or its source names) changed, or that predates multi-function runners.</summary>
        internal static void RefreshAll()
        {
            foreach (var runner in Object.FindObjectsOfType<WasmGpuRunner>(true))
            {
                string path = WasmPath(runner);
                if (path == null) continue;
                var exports = Exports(path);
                if (exports.Length > 0 && IsStale(runner, exports)) Assign(runner, path, runner.wasmMaterial.name);
            }
        }

        public static bool IsWasm(string path) => path != null && path.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase);

        static string DraggedWasm() => DragAndDrop.paths?.FirstOrDefault(IsWasm);

        static DragAndDropVisualMode OnHierarchyDrop(int dropTargetInstanceID, HierarchyDropFlags flags, Transform parent, bool perform)
        {
            string wasm = DraggedWasm();
            if (wasm == null) return DragAndDropVisualMode.None;
            if (perform)
            {
                var target = EditorUtility.InstanceIDToObject(dropTargetInstanceID) as GameObject;
                CreateRunner(wasm, (flags & HierarchyDropFlags.DropUpon) != 0 && target ? target.transform : parent);
            }
            return DragAndDropVisualMode.Copy;
        }

        static DragAndDropVisualMode OnSceneDrop(Object dropUpon, Vector3 worldPosition, Vector2 viewportPosition, Transform parent, bool perform)
        {
            string wasm = DraggedWasm();
            if (wasm == null) return DragAndDropVisualMode.None;
            if (perform)
            {
                var runner = CreateRunner(wasm, parent);
                if (runner) runner.transform.position = worldPosition;
            }
            return DragAndDropVisualMode.Copy;
        }

        public static WasmGpuRunner CreateRunner(string wasmPath, Transform parent = null)
        {
            var go = new GameObject(Path.GetFileNameWithoutExtension(wasmPath));
            Undo.RegisterCreatedObjectUndo(go, "Create WASM Runner");
            if (parent) go.transform.SetParent(parent, false);
            var runner = UdonSharpUndo.AddComponent<WasmGpuRunner>(go);
            Assign(runner, wasmPath, null);
            Selection.activeGameObject = go;
            return runner;
        }

        /// <summary>One exported function: its material, target, signature and names from source.</summary>
        public sealed class Export
        {
            public string Name;
            public Material Material;
            public RenderTexture Target;
            public FuncType Type;
            public string[] ParamNames;
            public string Doc;
            public SourceFn Source; // null when no source was found
        }

        /// <summary>The .wasm's exports in sub-asset order, named from the source next to it.</summary>
        public static Export[] Exports(string wasmPath)
        {
            var subs = AssetDatabase.LoadAllAssetsAtPath(wasmPath);
            var module = WasmModule.Parse(File.ReadAllBytes(wasmPath));
            var importer = AssetImporter.GetAtPath(wasmPath) as WasmImporter;
            var names = SourceNames.Find(wasmPath, importer ? importer.sourcePath : null);
            return subs.OfType<Material>().Where(m => module.FuncExports.ContainsKey(m.name)).Select(m =>
            {
                var ft = module.TypeOfFunc(module.FuncExports[m.name]);
                names.TryGetValue(m.name, out var fn);
                return new Export
                {
                    Name = m.name, Material = m, Type = ft, Doc = fn?.Doc ?? "", Source = fn,
                    Target = subs.OfType<RenderTexture>().FirstOrDefault(t => t.name == m.name),
                    ParamNames = ft.Params.Select((_, i) => SourceNames.ParamName(fn, i, ft.Params.Length)).ToArray(),
                };
            }).ToArray();
        }

        /// <summary>
        /// Gives the runner every export of the .wasm and makes <paramref name="current"/> (null = "main" or the first one)
        /// the function Run() calls. Argument values already set are kept by function and argument name.
        /// </summary>
        public static void Assign(WasmGpuRunner runner, string wasmPath, string current)
        {
            var exports = Exports(wasmPath);
            if (exports.Length == 0) { Debug.LogError($"[WASM2HLSL] {wasmPath} has no exported functions (see its import errors).", AssetDatabase.LoadMainAssetAtPath(wasmPath)); return; }

            var old = new System.Collections.Generic.Dictionary<string, (int, float)>();
            for (int f = 0; f < runner.functionNames.Length && f < runner.functionArgStart.Length; f++)
                for (int i = 0; i < runner.functionArgCount[f]; i++)
                {
                    int k = runner.functionArgStart[f] + i;
                    if (k < runner.argNames.Length && k < runner.argInts.Length)
                        old[runner.functionNames[f] + "/" + runner.argNames[k]] = (runner.argInts[k], runner.argFloats[k]);
                }
            if (runner.functionNames.Length == 0 && runner.wasmMaterial) // runner from before multi-function support: one function, values by index
                for (int i = 0; i < runner.argInts.Length && i < runner.argFloats.Length; i++)
                    old[runner.wasmMaterial.name + "/#" + i] = (runner.argInts[i], runner.argFloats[i]);

            Undo.RecordObject(runner, "Assign WASM");
            int total = exports.Sum(e => e.Type.Params.Length);
            runner.functionNames = exports.Select(e => e.Name).ToArray();
            runner.functionMaterials = exports.Select(e => e.Material).ToArray();
            runner.functionTargets = exports.Select(e => e.Target).ToArray();
            runner.functionArgCount = exports.Select(e => e.Type.Params.Length).ToArray();
            runner.functionArgStart = new int[exports.Length];
            runner.functionResultFloat = exports.Select(e => e.Type.Results.Length == 1 && IsFloat(e.Type.Results[0])).ToArray();
            runner.argInts = new int[total];
            runner.argFloats = new float[total];
            runner.argIsFloat = new bool[total];
            runner.argNames = new string[total];
            for (int f = 0, k = 0; f < exports.Length; f++)
            {
                runner.functionArgStart[f] = k;
                for (int i = 0; i < exports[f].Type.Params.Length; i++, k++)
                {
                    runner.argIsFloat[k] = IsFloat(exports[f].Type.Params[i]);
                    runner.argNames[k] = exports[f].ParamNames[i];
                    if (old.TryGetValue(exports[f].Name + "/" + runner.argNames[k], out var v) || old.TryGetValue(exports[f].Name + "/#" + i, out v))
                        (runner.argInts[k], runner.argFloats[k]) = v;
                }
            }

            int cur = Array.IndexOf(runner.functionNames, current);
            if (cur < 0) cur = Array.IndexOf(runner.functionNames, "main");
            runner.function = Math.Max(cur, 0);
            runner.wasmMaterial = runner.functionMaterials[runner.function];
            runner.target = runner.functionTargets[runner.function];
            runner.resultIsFloat = runner.functionResultFloat[runner.function];

            UdonSharpEditorUtility.CopyProxyToUdon(runner);
            EditorUtility.SetDirty(runner);
        }

        /// <summary>True when the runner's function tables no longer match the .wasm (reimported, source renamed, old runner).</summary>
        public static bool IsStale(WasmGpuRunner runner, Export[] exports) =>
            !runner.functionNames.SequenceEqual(exports.Select(e => e.Name))
            || !runner.functionMaterials.SequenceEqual(exports.Select(e => e.Material))
            || !runner.argNames.SequenceEqual(exports.SelectMany(e => e.ParamNames))
            || !runner.argIsFloat.SequenceEqual(exports.SelectMany(e => e.Type.Params.Select(IsFloat)));

        /// <summary>The runner's .wasm path, or null.</summary>
        public static string WasmPath(WasmGpuRunner runner)
        {
            string p = runner.wasmMaterial ? AssetDatabase.GetAssetPath(runner.wasmMaterial) : null;
            return IsWasm(p) ? p : null;
        }

        public static bool IsFloat(WasmType t) => t == WasmType.F32 || t == WasmType.F64;
    }

    [CustomEditor(typeof(WasmGpuRunner))]
    public class WasmGpuRunnerEditor : Editor
    {
        string _test;
        WasmRunnerSetup.Export[] _exports;
        Hash128 _exportsHash;

        public override void OnInspectorGUI()
        {
            if (UdonSharpGUI.DrawProgramSource(target, false)) return;
            var runner = (WasmGpuRunner)target;
            string path = WasmRunnerSetup.WasmPath(runner);

            var current = path != null ? AssetDatabase.LoadMainAssetAtPath(path) : null;
            int count = runner.functionNames.Length;
            WasmEditorLook.Header("WASM GPU Runner", path == null ? "No .wasm assigned"
                : $"{Path.GetFileName(path)}  ·  {count} function{(count == 1 ? "" : "s")}{(runner.busy ? "  ·  running" : "")}", WasmEditorLook.RunnerIcon);

            WasmEditorLook.Section("Module");
            var picked = EditorGUILayout.ObjectField("WASM", current, typeof(Object), false);
            if (picked != current && picked != null)
            {
                string p = AssetDatabase.GetAssetPath(picked);
                if (WasmRunnerSetup.IsWasm(p)) { WasmRunnerSetup.Assign(runner, p, null); GUIUtility.ExitGUI(); }
                else Debug.LogWarning($"[WASM2HLSL] {p} is not a .wasm file.");
            }
            if (path == null)
            {
                EditorGUILayout.HelpBox("Drag a .wasm here, or drag it straight into the Hierarchy.", MessageType.Info);
                WasmEditorLook.Footer();
                return;
            }

            // Re-read exports and source names only when the .wasm was reimported (its source changes trigger that too).
            var hash = AssetDatabase.GetAssetDependencyHash(path);
            if (_exports == null || hash != _exportsHash) { _exports = WasmRunnerSetup.Exports(path); _exportsHash = hash; }
            if (_exports.Length == 0)
            {
                EditorGUILayout.HelpBox("This .wasm has no exported functions (see its import errors).", MessageType.Error);
                WasmEditorLook.Footer();
                return;
            }
            if (WasmRunnerSetup.IsStale(runner, _exports)) WasmRunnerSetup.Assign(runner, path, runner.wasmMaterial.name);
            var export = _exports[Mathf.Clamp(runner.function, 0, _exports.Length - 1)];

            WasmEditorLook.Section("Function");
            DrawFunctionPicker(runner, path, export);
            var ps = export.Type.Params;
            if (ps.Length > (ps.Length > 0 && ps[0] == WasmType.I32 ? 1 : 0)) // param 0 (i32) is the pixel index, not shown
            {
                WasmEditorLook.Section("Arguments");
                DrawArgs(runner, export);
            }
            WasmEditorLook.Section("Dispatch");
            DrawSize(path, export);

            GUILayout.Space(8);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlaying))
                if (WasmEditorLook.BigButton("\u25B6  Test " + export.Name + " on the GPU")) _test = TestRun(runner, export.Type);
            if (_test != null) WasmEditorLook.Result(_test, _test.StartsWith("TRAPPED") || _test.StartsWith("GPU"));
            WasmEditorLook.Footer();
        }

        void DrawFunctionPicker(WasmGpuRunner runner, string path, WasmRunnerSetup.Export export)
        {
            if (_exports.Length > 1)
            {
                var options = _exports.Select(e => new GUIContent(Signature(e), e.Doc)).ToArray();
                int ni = EditorGUILayout.Popup(new GUIContent("Function", "Run() calls this one. Switch at runtime with SetFunction(\"name\") or RunFunction(\"name\")."),
                    runner.function, options);
                if (ni != runner.function) { WasmRunnerSetup.Assign(runner, path, _exports[ni].Name); _test = null; GUIUtility.ExitGUI(); }
            }
            else EditorGUILayout.LabelField("Function", Signature(export));
            if (export.Doc.Length > 0) EditorGUILayout.HelpBox(export.Doc, MessageType.None);
        }

        static string Signature(WasmRunnerSetup.Export e) =>
            $"{e.Name}({string.Join(", ", e.ParamNames)})" + (e.Type.Results.Length == 0 ? "" : " -> " + e.Type.Results[0].ToString().ToLowerInvariant());

        static void DrawArgs(WasmGpuRunner runner, WasmRunnerSetup.Export export)
        {
            int start = runner.functionArgStart[runner.function];
            for (int i = 0; i < export.Type.Params.Length; i++)
            {
                if (i == 0 && export.Type.Params[i] == WasmType.I32) continue; // pixel index, filled by the shader
                int k = start + i;
                var label = new GUIContent(ObjectNames.NicifyVariableName(export.ParamNames[i]),
                    $"{export.ParamNames[i]}: {export.Type.Params[i].ToString().ToLowerInvariant()} (wasm arg {i})\nSetNamedArg{(runner.argIsFloat[k] ? "Float" : "Int")}(\"{export.ParamNames[i]}\", v) or SetArg{(runner.argIsFloat[k] ? "Float" : "Int")}({i}, v)");
                EditorGUI.BeginChangeCheck();
                if (runner.argIsFloat[k])
                {
                    float v = EditorGUILayout.FloatField(label, runner.argFloats[k]);
                    if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(runner, "Set WASM Arg"); runner.argFloats[k] = v; Save(runner); }
                }
                else
                {
                    int v = EditorGUILayout.IntField(label, runner.argInts[k]);
                    if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(runner, "Set WASM Arg"); runner.argInts[k] = v; Save(runner); }
                }
            }
        }

        /// <summary>Size / Trap Check of the current function: read-only when the source sets them (@size, @traps), else the .wasm's defaults.</summary>
        static void DrawSize(string path, WasmRunnerSetup.Export export)
        {
            var importer = (WasmImporter)AssetImporter.GetAtPath(path);
            var (w, h, _, fromSource) = importer.Shape(export.Source);
            bool tagTraps = export.Source?.Traps ?? false;
            EditorGUI.BeginChangeCheck();
            Vector2Int size;
            using (new EditorGUI.DisabledScope(fromSource))
                size = EditorGUILayout.Vector2IntField(fromSource
                    ? new GUIContent("Size (@size)", "Set by the @size tag above this function in source. Edit the source to change it.")
                    : new GUIContent("Size", "Calls per Run() = width x height, for every function without an @size tag in source. Add \"@size 1024x512\" or \"@size 100000\" to the comment above a function to set it there."),
                    fromSource ? new Vector2Int(w, h) : new Vector2Int(importer.width, importer.height));
            bool traps;
            using (new EditorGUI.DisabledScope(tagTraps))
                traps = EditorGUILayout.Toggle(tagTraps
                    ? new GUIContent("Trap Check (@traps)", "Turned on by the @traps tag above this function in source.")
                    : new GUIContent("Trap Check", "Detect unreachable / divide by zero / runaway loops (sets runner.trapped) for every function of this .wasm. Or add @traps to one function's comment in source."),
                    tagTraps || importer.detectTraps);
            if (tagTraps) traps = importer.detectTraps;
            if (fromSource) size = new Vector2Int(importer.width, importer.height);
            if (EditorGUI.EndChangeCheck() && size.x > 0 && size.y > 0)
            {
                importer.width = size.x;
                importer.height = size.y;
                importer.detectTraps = traps;
                EditorUtility.SetDirty(importer);
                importer.SaveAndReimport();
            }
            DrawSourceHint(export, importer, fromSource, tagTraps);
        }

        /// <summary>Says how to move Size / Trap Check into source, with the exact line to paste.</summary>
        static void DrawSourceHint(WasmRunnerSetup.Export export, WasmImporter importer, bool sizeTagged, bool trapsTagged)
        {
            if (sizeTagged && (trapsTagged || !importer.detectTraps)) return;
            var src = export.Source;
            if (src == null)
            {
                EditorGUILayout.HelpBox(
                    "No source found for this function, so arguments are argN and Size / Trap Check are set by hand here.\n" +
                    "Put the source (.rs .c .cpp .zig .ts .go) next to the .wasm, or set Source Path in the .wasm's import settings, " +
                    "to get real argument names, docs and the @size / @traps tags.", MessageType.Info);
                return;
            }
            string ext = Path.GetExtension(src.File).ToLowerInvariant();
            string comment = ext == ".rs" || ext == ".zig" ? "///" : "//";
            var tags = new System.Collections.Generic.List<string>();
            if (!sizeTagged) tags.Add($"@size {importer.width}x{importer.height}");
            if (importer.detectTraps && !trapsTagged) tags.Add("@traps");
            string line = $"{comment} {string.Join(" ", tags)}";
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.HelpBox(
                    $"Keep this setting with the code: add\n    {line}\nto the comment above '{export.Name}' in {Path.GetFileName(src.File)}. " +
                    "Then Size / Trap Check follow the source for this function only.", MessageType.Info);
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(60)))
                {
                    if (GUILayout.Button("Copy")) EditorGUIUtility.systemCopyBuffer = line;
                    var asset = AssetDatabase.LoadMainAssetAtPath(src.File.Replace('\\', '/'));
                    using (new EditorGUI.DisabledScope(asset == null))
                        if (GUILayout.Button("Open")) AssetDatabase.OpenAsset(asset);
                }
            }
        }

        static void Save(WasmGpuRunner runner)
        {
            UdonSharpEditorUtility.CopyProxyToUdon(runner);
            EditorUtility.SetDirty(runner);
        }

        /// <summary>One Blit + synchronous readback with the inspector's arguments. No Udon, no Play Mode.</summary>
        static string TestRun(WasmGpuRunner r, FuncType ft)
        {
            var rt = r.target;
            var mat = r.wasmMaterial;
            int start = r.functionArgStart[r.function];
            for (int i = 0; i < ft.Params.Length; i++)
            {
                if (r.argIsFloat[start + i]) mat.SetFloat("_WasmArg" + i, r.argFloats[start + i]);
                else mat.SetInteger("_WasmArg" + i, r.argInts[start + i]);
            }
            mat.SetInteger("_WasmWidth", rt.width);

            var sw = Stopwatch.StartNew();
            Graphics.Blit(null, rt, mat);
            var req = AsyncGPUReadback.Request(rt);
            req.WaitForCompletion();
            sw.Stop();
            if (req.hasError) return "GPU readback failed.";

            var data = req.GetData<int>();
            int hiPlane = mat.GetInteger("_WasmHiPlane"), trapPlane = mat.GetInteger("_WasmTrapPlane");
            int n = rt.width * Math.Min(rt.height, mat.GetInteger("_WasmPlaneRows"));
            bool trapped = false;
            if (trapPlane > 0) for (int i = 0; i < n && !trapped; i++) trapped = data[trapPlane * n + i] != 0;
            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                int lo = data[i], hi = hiPlane > 0 ? data[hiPlane * n + i] : 0;
                double v = ft.Results.Length == 0 ? 0
                    : WasmRunnerSetup.IsFloat(ft.Results[0]) ? BitConverter.Int32BitsToSingle(lo)
                    : ft.Results[0] == WasmType.I64 ? ((long)hi << 32) | (uint)lo
                    : lo;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            string range = ft.Results.Length == 0 ? "" : $", results {min:G6} to {max:G6}";
            return $"{(trapped ? "TRAPPED: " : "")}{n:N0} calls in {sw.ElapsedMilliseconds} ms{range}{(trapPlane > 0 && !trapped ? ", no traps" : "")}";
        }
    }

    /// <summary>Shared look of the package's inspectors: header with icon, section titles, footer with author and version.</summary>
    public static class WasmEditorLook
    {
        const string Icons = "Packages/com.logiccuteguy.wasm2hlsl/Editor/Icons/";
        public static readonly Color Accent = new Color32(101, 79, 240, 255);
        static Color Panel => EditorGUIUtility.isProSkin ? new Color(0.17f, 0.16f, 0.22f) : new Color(0.86f, 0.85f, 0.94f);
        static Color Muted => EditorGUIUtility.isProSkin ? new Color(0.62f, 0.62f, 0.68f) : new Color(0.36f, 0.36f, 0.42f);
        static Color Rule => EditorGUIUtility.isProSkin ? new Color(1f, 1f, 1f, 0.08f) : new Color(0f, 0f, 0f, 0.12f);

        static GUIStyle _title, _sub, _section, _footer, _result;
        static GUIStyle Title => _title ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 };
        static GUIStyle Sub => _sub ??= new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Muted } };
        static GUIStyle SectionStyle => _section ??= new GUIStyle(EditorStyles.miniBoldLabel) { normal = { textColor = EditorGUIUtility.isProSkin ? new Color32(160, 145, 255, 255) : Accent } };
        static GUIStyle FooterStyle => _footer ??= new GUIStyle(EditorStyles.centeredGreyMiniLabel) { normal = { textColor = Muted } };
        static GUIStyle ResultStyle => _result ??= new GUIStyle(EditorStyles.wordWrappedMiniLabel) { richText = true };

        static Texture2D _runnerIcon, _wasmIcon;
        public static Texture2D RunnerIcon => _runnerIcon ? _runnerIcon : _runnerIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(Icons + "WasmGpuRunnerIcon.png");
        public static Texture2D WasmIcon => _wasmIcon ? _wasmIcon : _wasmIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(Icons + "WasmIcon.png");
        static string _version;
        public static string Version => _version ??= UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(WasmEditorLook).Assembly)?.version ?? "?";

        public static void Header(string title, string subtitle, Texture2D icon)
        {
            var r = GUILayoutUtility.GetRect(0, 46, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, Panel);
            EditorGUI.DrawRect(new Rect(r.x, r.y, 3, r.height), Accent);
            if (icon) GUI.DrawTexture(new Rect(r.x + 10, r.y + 7, 32, 32), icon);
            GUI.Label(new Rect(r.x + 50, r.y + 6, r.width - 56, 20), title, Title);
            GUI.Label(new Rect(r.x + 50, r.y + 26, r.width - 56, 16), subtitle, Sub);
        }

        public static void Section(string label)
        {
            GUILayout.Space(8);
            var r = GUILayoutUtility.GetRect(0, 16, GUILayout.ExpandWidth(true));
            GUI.Label(r, label.ToUpperInvariant(), SectionStyle);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax, r.width, 1), Rule);
            GUILayout.Space(4);
        }

        public static bool BigButton(string label)
        {
            var bg = GUI.backgroundColor;
            GUI.backgroundColor = Color.Lerp(Accent, Color.white, EditorGUIUtility.isProSkin ? 0.25f : 0.45f);
            bool clicked = GUILayout.Button(label, GUILayout.Height(26));
            GUI.backgroundColor = bg;
            return clicked;
        }

        public static void Result(string text, bool bad)
        {
            string color = bad ? "#E0605A" : EditorGUIUtility.isProSkin ? "#7FD28B" : "#2E8B3E";
            EditorGUILayout.LabelField($"<color={color}>\u25CF</color> {text}", ResultStyle);
        }

        public static void Footer()
        {
            GUILayout.Space(12);
            EditorGUI.DrawRect(GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true)), Rule);
            GUILayout.Label($"WASM2HLSL v{Version}  \u00B7  by LogicCuteGuy", FooterStyle);
        }
    }
}
