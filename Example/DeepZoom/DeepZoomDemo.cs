using LogicCuteGuy.Wasm2Hlsl;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace LogicCuteGuy.Wasm2Hlsl.Samples
{
    /// <summary>
    /// Endless Mandelbrot zoom computed in f64 on the GPU (deepzoom.wasm). The display material reads the runner's
    /// render texture directly (WASM2HLSL/Int Display, Iteration Palette), so Udon never touches the pixels.
    /// The zoom clock is synced: everyone sees the same depth. Interact restarts it.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class DeepZoomDemo : UdonSharpBehaviour
    {
        public WasmGpuRunner runner;
        public Renderer display;
        [Tooltip("Iteration cap at zoom 0; deeper views need more, see iterPerDoubling.")]
        public int baseIter = 200;
        public int iterPerDoubling = 45;
        [Tooltip("Zoom doublings per second.")]
        public float speed = 0.5f;
        [Tooltip("Doublings before starting over (32 = 4e9x). f64 stays sharp to about 44; f32 breaks into blocks after about 17.")]
        public float maxZoom = 32f;
        [Tooltip("The image is computed one band per frame, so each frame does 1/bands of the soft-float f64 work. Raise it on Quest.")]
        public int bands = 8;
        public double centerX = -0.743643887037158704752191506114774;
        public double centerY = 0.131825904205311970493132056385139;

        [UdonSynced] private int _startMs;
        private int _band;

        void Start()
        {
            runner.readback = false; // the display reads the render texture; skip the per-pixel Udon copy
            runner.bands = bands;
            runner.SetFunction("zoom");
            display.material.mainTexture = runner.target;
            // A double does not fit one float argument: send the float nearest to it plus the remainder.
            float hx = (float)centerX, hy = (float)centerY;
            runner.SetNamedArgFloat("cx_hi", hx);
            runner.SetNamedArgFloat("cx_lo", (float)(centerX - hx));
            runner.SetNamedArgFloat("cy_hi", hy);
            runner.SetNamedArgFloat("cy_lo", (float)(centerY - hy));
            runner.SetNamedArgInt("width", runner.target.width);
            if (Networking.IsOwner(gameObject)) Restart();
        }

        public override void Interact()
        {
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
            Restart();
        }

        private void Restart()
        {
            _startMs = Networking.GetServerTimeInMilliseconds();
            RequestSerialization();
        }

        void Update()
        {
            if (runner.busy) return;
            if (_band == 0) // new sweep: every band of it uses the same zoom, so a finished sweep is one coherent picture
            {
                float z = (Networking.GetServerTimeInMilliseconds() - _startMs) / 1000f * speed % maxZoom;
                runner.SetNamedArgFloat("zoom", z);
                runner.SetNamedArgInt("max_iter", baseIter + (int)(iterPerDoubling * z));
                display.material.SetFloat("_Phase", z * 0.05f);
            }
            runner.band = _band;
            runner.Run();
            _band = (_band + 1) % bands;
        }
    }
}
