using LogicCuteGuy.Wasm2Hlsl;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace LogicCuteGuy.Wasm2Hlsl.Samples
{
    /// <summary>
    /// Animated plasma from plasma.wasm (WebAssembly SIMD), re-rendered every frame the runner is free.
    /// The export returns packed RGBA8 and the display material (WASM2HLSL/Int Display, Packed RGBA8) reads the render texture
    /// directly. Time is server time, so every client shows the same frame. Interact toggles the glow for everyone.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class SimdPlasmaDemo : UdonSharpBehaviour
    {
        public WasmGpuRunner runner;
        public Renderer display;
        [Tooltip("Brightness added to every byte with u8x16.add_sat when glowing.")]
        public int glowAmount = 70;

        [UdonSynced] private bool _glow;

        void Start()
        {
            runner.readback = false; // the display reads the render texture; skip the per-pixel Udon copy
            runner.SetFunction("plasma");
            runner.SetNamedArgInt("width", runner.target.width);
            display.material.mainTexture = runner.target;
        }

        public override void Interact()
        {
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
            _glow = !_glow;
            RequestSerialization();
        }

        void Update()
        {
            if (runner.busy) return;
            runner.SetNamedArgFloat("time", Networking.GetServerTimeInMilliseconds() % 3600000 / 1000f);
            runner.SetNamedArgInt("glow", _glow ? glowAmount : 0);
            runner.Run();
        }
    }
}
