using LogicCuteGuy.Wasm2Hlsl;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace LogicCuteGuy.Wasm2Hlsl.Samples
{
    /// <summary>
    /// Renders dsp.wasm on the GPU (one pixel = one 48 kHz sample) into an AudioClip and loops it.
    /// Interact cycles off → dry (export <c>dry</c>) → filter sweep (export <c>sweep</c>) for everyone;
    /// the synced start time keeps every client at the same point of the loop.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class AudioDspDemo : UdonSharpBehaviour
    {
        public WasmGpuRunner runner;
        public AudioSource source;
        [Tooltip("FIR half-length: the filter has 2*taps+1 coefficients.")]
        public int taps = 32;
        [Tooltip("0 = off, 1 = dry, 2 = filter sweep.")]
        [UdonSynced] public int mode;

        [UdonSynced] private int _startMs; // server time playback started
        private int _shown;
        private AudioClip _clip;
        private float[] _samples;

        void Start()
        {
            runner.callbackTarget = this;
            Apply();
        }

        public override void Interact()
        {
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
            mode = (mode + 1) % 3;
            _startMs = Networking.GetServerTimeInMilliseconds();
            RequestSerialization();
            Apply();
        }

        public override void OnDeserialization() { Apply(); }

        private void Apply()
        {
            if (mode == 0) { source.Stop(); _shown = 0; return; }
            if (runner.busy || mode == _shown) return; // OnWasmComplete calls back in
            _shown = mode;
            if (mode == 1) runner.SetFunction("dry");
            else
            {
                runner.SetFunction("sweep");
                runner.SetNamedArgInt("taps", taps);
            }
            runner.Run();
        }

        public void OnWasmComplete()
        {
            if (mode == 0 || runner.resultFunction != (mode == 2 ? "sweep" : "dry")) { Apply(); return; } // stale: mode changed while busy
            int[] pcm = runner.resultLo;
            int n = pcm.Length;
            if (_samples == null || _samples.Length != n)
            {
                _samples = new float[n];
                _clip = AudioClip.Create("wasm-dsp", n, 1, 48000, false);
            }
            for (int i = 0; i < n; i++) _samples[i] = pcm[i] / 32768f;
            _clip.SetData(_samples, 0);

            source.clip = _clip;
            source.loop = true;
            source.Play();
            source.time = Mathf.Repeat((Networking.GetServerTimeInMilliseconds() - _startMs) / 1000f, _clip.length);
            if (runner.trapped) Debug.LogWarning("[AudioDsp] some samples trapped.", this);
        }
    }
}
