using UdonSharp;
using UnityEngine;
using VRC.SDK3.Rendering;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

namespace LogicCuteGuy.Wasm2Hlsl
{
    /// <summary>
    /// Runs the exported functions of one .wasm: one pixel of <see cref="target"/> = one call of the current function.
    /// SetFunction("name") (or keep the default), SetArg*/SetInput, then Run(); results land in resultLo/resultHi/resultFloat
    /// and callbackEvent fires with <see cref="resultFunction"/> naming the function that produced them.
    /// Argument and function names come from the Rust source next to the .wasm.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class WasmGpuRunner : UdonSharpBehaviour
    {
        [Tooltip("Material of the current function (from the .wasm asset).")]
        public Material wasmMaterial;

        [Tooltip("R32_SINT render texture of the current function (from the .wasm asset).")]
        public RenderTexture target;

        [Tooltip("Optional behaviour that receives callbackEvent when results are ready.")]
        public UdonSharpBehaviour callbackTarget;

        public string callbackEvent = "OnWasmComplete";

        [Tooltip("Copy results back into resultLo/resultHi (a per-pixel Udon loop). Turn off when only a material reads target.")]
        public bool readback = true;

        [Tooltip("Split the target into this many horizontal bands; each Run computes only band 'band' (the rest keep their pixels). " +
                 "Spreads an expensive kernel over several frames. Use with readback off.")]
        public int bands = 1;
        [HideInInspector] public int band;

        [HideInInspector] public int[] resultLo;
        [HideInInspector] public int[] resultHi;
        [HideInInspector] public bool trapped;
        [HideInInspector] public bool busy;
        [HideInInspector] public string resultFunction;

        // f32 exports: results go straight into resultFloat (native copy, no per-value Udon loop); resultLo stays unset.
        [HideInInspector] public bool resultIsFloat;
        [HideInInspector] public float[] resultFloat;

        // One entry per export, filled by the editor. functionArgStart/Count index the flat arg arrays below.
        [HideInInspector] public int function;
        [HideInInspector] public string[] functionNames = new string[0];
        [HideInInspector] public Material[] functionMaterials = new Material[0];
        [HideInInspector] public RenderTexture[] functionTargets = new RenderTexture[0];
        [HideInInspector] public int[] functionArgStart = new int[0];
        [HideInInspector] public int[] functionArgCount = new int[0];
        [HideInInspector] public bool[] functionResultFloat = new bool[0];

        // Argument values of every function, flat; applied on every Run(). argNames are the Rust parameter names.
        [HideInInspector] public int[] argInts = new int[0];
        [HideInInspector] public float[] argFloats = new float[0];
        [HideInInspector] public bool[] argIsFloat = new bool[0];
        [HideInInspector] public string[] argNames = new string[0];

        private Texture2D _input;
        private int _inputWidth;
        private byte[] _bytes;

        /// <summary>Index of an export by name, or -1.</summary>
        public int FunctionIndex(string name)
        {
            for (int i = 0; i < functionNames.Length; i++) if (functionNames[i] == name) return i;
            return -1;
        }

        /// <summary>Makes <paramref name="name"/> the function Run() calls. False if there is no such export.</summary>
        public bool SetFunction(string name)
        {
            int i = FunctionIndex(name);
            if (i < 0) { Debug.LogError("[WASM2HLSL] No exported function '" + name + "'.", this); return false; }
            SetFunctionIndex(i);
            return true;
        }

        public void SetFunctionIndex(int i)
        {
            if (i < 0 || i >= functionNames.Length) return;
            function = i;
            wasmMaterial = functionMaterials[i];
            target = functionTargets[i];
            resultIsFloat = functionResultFloat[i];
        }

        public string FunctionName() { return function < functionNames.Length ? functionNames[function] : ""; }

        /// <summary>Wasm parameter index of the current function's argument <paramref name="name"/> (Rust name), or -1.</summary>
        public int ArgIndex(string name)
        {
            int start = ArgStart(), count = ArgCount();
            for (int i = 0; i < count; i++) if (argNames[start + i] == name) return i;
            return -1;
        }

        public void SetNamedArgInt(string name, int value)
        {
            int i = ArgIndex(name);
            if (i < 0) Debug.LogError("[WASM2HLSL] " + FunctionName() + " has no argument '" + name + "'.", this);
            else SetArgInt(i, value);
        }

        public void SetNamedArgFloat(string name, float value)
        {
            int i = ArgIndex(name);
            if (i < 0) Debug.LogError("[WASM2HLSL] " + FunctionName() + " has no argument '" + name + "'.", this);
            else SetArgFloat(i, value);
        }

        // Runners set up before multi-function support have one function and no function arrays.
        private int ArgStart() { return function < functionArgStart.Length ? functionArgStart[function] : 0; }
        private int ArgCount() { return function < functionArgCount.Length ? functionArgCount[function] : argIsFloat.Length; }

        /// <summary>Sets wasm parameter <paramref name="index"/> of the current function (0 is the pixel index when it is i32).</summary>
        public void SetArgInt(int index, int value)
        {
            if (index >= 0 && index < ArgCount()) argInts[ArgStart() + index] = value;
        }

        public void SetArgFloat(int index, float value)
        {
            if (index >= 0 && index < ArgCount()) argFloats[ArgStart() + index] = value;
        }

        /// <summary>Uploads words readable from wasm via the import env.input(i32) -> i32.</summary>
        public void SetInput(int[] words)
        {
            int w = Mathf.Clamp(words.Length, 1, 4096);
            int h = Mathf.Max(1, (words.Length + w - 1) / w);
            if (_input == null || _input.width != w || _input.height != h)
            {
                _input = new Texture2D(w, h, TextureFormat.RFloat, false, true);
                _input.filterMode = FilterMode.Point;
            }
            byte[] raw = new byte[w * h * 4];
            for (int i = 0; i < words.Length; i++)
            {
                int v = words[i];
                raw[i * 4] = (byte)(v & 0xFF);
                raw[i * 4 + 1] = (byte)((v >> 8) & 0xFF);
                raw[i * 4 + 2] = (byte)((v >> 16) & 0xFF);
                raw[i * 4 + 3] = (byte)((v >> 24) & 0xFF);
            }
            _input.LoadRawTextureData(raw);
            _input.Apply(false);
            _inputWidth = w;
        }

        /// <summary>SetFunction(name) then Run().</summary>
        public void RunFunction(string name)
        {
            if (!busy && SetFunction(name)) Run();
        }

        public void Run()
        {
            if (busy) return;
            busy = true;
            resultFunction = FunctionName();
            int start = ArgStart(), count = ArgCount();
            for (int i = 0; i < count; i++)
            {
                if (argIsFloat[start + i]) wasmMaterial.SetFloat("_WasmArg" + i, argFloats[start + i]);
                else wasmMaterial.SetInteger("_WasmArg" + i, argInts[start + i]);
            }
            if (_input != null)
            {
                wasmMaterial.SetTexture("_WasmInput", _input);
                wasmMaterial.SetInteger("_WasmInputWidth", _inputWidth);
            }
            wasmMaterial.SetInteger("_WasmWidth", target.width);
            wasmMaterial.SetInteger("_WasmBandRows", bands > 1 ? (target.height + bands - 1) / bands : 0);
            wasmMaterial.SetInteger("_WasmBand", band);
            VRCGraphics.Blit(null, target, wasmMaterial);
            if (readback) VRCAsyncGPUReadback.Request(target, 0, (IUdonEventReceiver)this);
            else busy = false;
        }

        public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
        {
            busy = false;
            // The client flushes in-flight readbacks while tearing the world down (leaving, quitting): the material is already gone.
            if (!Utilities.IsValid(wasmMaterial) || !Utilities.IsValid(target)) return;
            if (request.hasError) { Debug.LogError("[WASM2HLSL] GPU readback failed.", this); return; }

            // R32 target: rows [0, n) hold lo; hi and trap (if any) follow as extra planes of n values each.
            int hiPlane = wasmMaterial.GetInteger("_WasmHiPlane");
            int trapPlane = wasmMaterial.GetInteger("_WasmTrapPlane");
            int n = target.width * Mathf.Min(target.height, wasmMaterial.GetInteger("_WasmPlaneRows"));
            int size = request.layerDataSize;
            if (resultIsFloat)
            {
                // The R32_SInt texels hold the f32 bits, so a float readback reinterprets them for free.
                if (resultFloat == null || resultFloat.Length != size / 4) resultFloat = new float[size / 4];
                if (!request.TryGetData(resultFloat, 0)) { Debug.LogError("[WASM2HLSL] TryGetData failed.", this); return; }
                trapped = false;
                if (trapPlane > 0)
                    for (int i = 0; i < n; i++)
                        if (resultFloat[trapPlane * n + i] != 0f) { trapped = true; break; }
                if (Utilities.IsValid(callbackTarget)) callbackTarget.SendCustomEvent(callbackEvent);
                return;
            }
            if (_bytes == null || _bytes.Length != size) _bytes = new byte[size];
            if (!request.TryGetData(_bytes, 0)) { Debug.LogError("[WASM2HLSL] TryGetData failed.", this); return; }

            if (resultLo == null || resultLo.Length != n) { resultLo = new int[n]; resultHi = new int[n]; }
            for (int i = 0; i < n; i++) resultLo[i] = ReadInt(i * 4);
            if (hiPlane > 0) for (int i = 0; i < n; i++) resultHi[i] = ReadInt((hiPlane * n + i) * 4);

            trapped = false;
            if (trapPlane > 0)
                for (int i = 0; i < n; i++)
                    if (ReadInt((trapPlane * n + i) * 4) != 0) { trapped = true; break; }

            if (Utilities.IsValid(callbackTarget)) callbackTarget.SendCustomEvent(callbackEvent);
        }

        /// <summary>Result i reinterpreted as f32 (for exports returning f32).</summary>
        public float GetResultFloat(int i) { return resultIsFloat ? resultFloat[i] : BitsToFloat(resultLo[i]); }

        private int ReadInt(int o)
        {
            return _bytes[o] | (_bytes[o + 1] << 8) | (_bytes[o + 2] << 16) | (_bytes[o + 3] << 24);
        }

        public static float BitsToFloat(int bits)
        {
            int exp = (bits >> 23) & 0xFF;
            int man = bits & 0x7FFFFF;
            float sign = bits < 0 ? -1f : 1f;
            if (exp == 255) return man != 0 ? float.NaN : sign * float.PositiveInfinity;
            if (exp == 0) return sign * man * Mathf.Pow(2f, -149f);
            return sign * (man | 0x800000) * Mathf.Pow(2f, exp - 150);
        }
    }
}
