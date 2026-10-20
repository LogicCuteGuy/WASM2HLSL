using LogicCuteGuy.Wasm2Hlsl;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace LogicCuteGuy.Wasm2Hlsl.Samples
{
    /// <summary>
    /// Shows mandelbrot.wasm on <see cref="display"/>, cycling between its two exports:
    /// view 0 runs <c>main</c> (Mandelbrot), views 1..4 run <c>julia</c> with a different c.
    /// The view is synced: the owner advances it every <see cref="interval"/> seconds (or on Interact) and every client renders it.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class MandelbrotDemo : UdonSharpBehaviour
    {
        public WasmGpuRunner runner;
        public Renderer display;
        public int maxIter = 64;
        [Tooltip("Seconds between views, advanced by the owner. 0 = only on Interact.")]
        public float interval = 6f;

        private float[] _cr = { -0.8f, 0.285f, -0.4f, -0.70176f };
        private float[] _ci = { 0.156f, 0.01f, 0.6f, -0.3842f };

        [UdonSynced] private int _view;
        private int _shown = -1;
        private float _next;
        private Texture2D _tex;

        void Start()
        {
            runner.callbackTarget = this;
            _next = Time.time + interval;
            Show();
        }

        void Update()
        {
            if (interval > 0f && Time.time >= _next && Networking.IsOwner(gameObject)) Advance();
        }

        public override void Interact()
        {
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
            Advance();
        }

        public override void OnDeserialization() { Show(); }

        private void Advance()
        {
            _next = Time.time + interval;
            _view = (_view + 1) % (_cr.Length + 1);
            RequestSerialization();
            Show();
        }

        private void Show()
        {
            if (runner.busy || _view == _shown) return; // OnWasmComplete calls back in
            _shown = _view;
            if (_view == 0) runner.SetFunction("main");
            else
            {
                runner.SetFunction("julia");
                runner.SetNamedArgFloat("cr", _cr[_view - 1]);
                runner.SetNamedArgFloat("ci", _ci[_view - 1]);
            }
            runner.SetNamedArgInt("width", runner.target.width);
            runner.SetNamedArgInt("max_iter", maxIter);
            runner.Run();
        }

        public void OnWasmComplete()
        {
            int w = runner.target.width, h = runner.resultLo.Length / w;
            if (_tex == null)
            {
                _tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                _tex.filterMode = FilterMode.Point;
            }
            Color32[] px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++)
            {
                byte v = (byte)(255 * runner.resultLo[i] / maxIter);
                px[i] = new Color32(v, (byte)(v / 2), (byte)(255 - v), 255);
            }
            _tex.SetPixels32(px);
            _tex.Apply(false);
            display.material.mainTexture = _tex;
            if (runner.trapped) Debug.LogWarning("[Mandelbrot] some pixels trapped.", this);
            Show(); // the view may have changed while the GPU was busy
        }
    }
}
