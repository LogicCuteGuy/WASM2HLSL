using LogicCuteGuy.Wasm2Hlsl;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Rendering;
using VRC.SDKBase;

namespace LogicCuteGuy.Wasm2Hlsl.Samples
{
    /// <summary>
    /// One insert in an audio chain: inputs → ChromaFx → output. Chroma-style scale filter (chroma.wasm) on the GPU, live:
    /// off-scale notes are removed from the sound; in-scale notes, highs and unresolvable bass pass unchanged.
    /// The input sources play at a tiny volume (VRChat replaces world mixer groups, so a muted mixer can't hide them). Each audio block, Udon grabs their newest samples
    /// (GetOutputData), the GPU removes the off-scale notes, and the result is written just ahead of the
    /// play head of a looping clip on <see cref="output"/>. The sources' own code is untouched; any AudioSource can feed a chain.
    /// All inputs are summed on the GPU first (<see cref="mixMaterial"/>), so the filter runs once whatever the number of sources.
    /// Bypass sends the dry signal down the same path, so switching it never shifts timing.
    /// Latency = block wait + GPU readback + write-ahead + filter. The write-ahead tunes itself: it grows when a block arrives
    /// too late and shrinks back while blocks keep arriving in time. The panel readout shows the result.
    /// Networked: the panel is synced (whoever moves a control takes ownership); every client processes its own audio.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class ChromaFx : UdonSharpBehaviour
    {
        // Must match chroma.rs.
        // Input: [main + tail coefficients, Coef][output history, 2 * Win][source rows]; the texture is Taps wide.
        private const int Taps = 4096, Coef = 2 * Taps, Win = 8192, Src = Coef + 2 * Win, MaxBlock = Win - Taps;
        // Output clip: ring of Ring frames; blocks land _lead frames ahead of the play head, followed by Clear frames of silence.
        // A block landing less than Guard frames ahead may already have been read by the mixer (one DSP buffer): it counts as late.
        // Ring > 2 * (maxLead + MaxBlock + Clear) so "ahead" past Ring / 2 always means the play head overtook the write position.
        private const int Ring = 131072, Clear = 16384, Guard = 1024;
        private const int Controls = 5; // color, key, scale, bypass, mode
        // Inputs play at -60 dB so they are inaudible except through here (VRChat replaces world mixer groups, and a muted
        // source gives GetOutputData only zeros); the mixdown scales them back up. Float samples lose nothing.
        // Not lower: measured, a source under ~4e-4 (-68 dB) is culled as inaudible and GetOutputData returns exact zeros.
        // Juno voices play at velocity * envelope * Send, so very soft notes and the end of a release fall under that limit.
        private const float Send = 0.001f;

        public WasmGpuRunner runner;
        [Tooltip("What this chain listens to. Start turns them down to -100 dB (Juno voices through JunoSynth.send). " +
                 "Sources with a clip are skipped while stopped; sources without one (video players) are always read.")]
        public AudioSource[] inputs;
        [Tooltip("Plays the result at full volume.")]
        public AudioSource output;
        [Tooltip("Most inputs read per block. Only the upload grows with it; the filter runs once on their sum.")]
        public int maxInputs = 8;
        [Tooltip("ChromaMix.mat: sums the input rows on the GPU before the filter.")]
        public Material mixMaterial;
        [Tooltip("RFloat render texture, 4096 x 10: the summed input the filter reads.")]
        public RenderTexture mixed;
        [Tooltip("0..200. Off-scale notes fade out by 50, in-scale notes get up to +6 dB by 100, resonance grows over the whole range.")]
        public Slider color;
        [Tooltip("Root 0..11 (C..B).")]
        public Slider key;
        [Tooltip("0 major, 1 minor, 2 major pentatonic, 3 minor pentatonic, 4 chromatic.")]
        public Slider scale;
        [Tooltip("0/1 switch: dry signal through the same path (same latency, so switching it never shifts timing).")]
        public Slider bypass;
        [Tooltip("0/1 switch: 0 = HQ (43 ms filter delay, cuts off-scale notes above ~400 Hz), 1 = LOW (21 ms, above ~800 Hz).")]
        public Slider mode;
        [Tooltip("Smallest write-ahead in frames. Raise it if the output crackles on this machine.")]
        public int minLead = 3072;
        [Tooltip("Largest write-ahead in frames (16384 = 341 ms at 48 kHz). The write-ahead grows up to this after hitches " +
                 "(avatars loading in busy instances), trading latency for no dropouts.")]
        public int maxLead = 16384;
        [Tooltip("Optional: shows key, scale, colour and latency.")]
        public Text readout;

        private int[] _masks = { 0xAB5, 0x5AD, 0x295, 0x4A9, 0xFFF }; // bit n = root + n semitones is in the scale
        private string[] _scaleNames = { "major", "minor", "major pent", "minor pent", "chromatic" };
        private string[] _keyNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        private float[] _in, _win, _hist, _block, _zeros, _gains;
        private JunoSynth _juno;
        private int[] _level; // per input: index into _juno.level, or -1 (full level)
        private byte[] _bytes;
        private Texture2D _tex;
        private AudioClip _clip;
        private int _sr, _count, _write = -1, _lead;
        private double _lastDsp, _lastActive, _launchDsp, _lastLate;
        private float _latencyMs;
        private bool _tapsDirty = true, _tapsReady;
        private float[] _last;
        [UdonSynced] private float[] _sync = new float[0];

        void Start()
        {
            runner.callbackTarget = this;
            foreach (AudioSource s in inputs)
            {
                if (s == null) continue;
                // Over the voice limit (busy instances) Unity silences the lowest priority, then the quietest: these play at -60 dB.
                s.priority = 0;
                s.spatialBlend = 0f; // 2D: distance must not lower the level we read (it would also push it under the cull limit)
                JunoSynth juno = s.GetComponentInParent<JunoSynth>();
                if (juno != null) juno.send = Send; // it rewrites its voice volumes every frame
                else s.volume = Send;
            }
            _gains = new float[16];
            _level = new int[inputs.Length];
            for (int i = 0; i < inputs.Length; i++)
            {
                _level[i] = -1;
                if (inputs[i] == null) continue;
                JunoSynth j = inputs[i].GetComponentInParent<JunoSynth>();
                if (j == null) continue;
                _juno = j;
                for (int v = 0; v < j.voices.Length; v++)
                    if (j.voices[v] == inputs[i] || j.loopVoices[v] == inputs[i]) _level[i] = v;
            }
            _sr = AudioSettings.outputSampleRate;
            int floats = Src + maxInputs * 2 * Win; // whole rows of Taps
            _in = new float[floats];
            _bytes = new byte[floats * 4];
            _tex = new Texture2D(Taps, floats / Taps, TextureFormat.RFloat, false, true);
            _tex.filterMode = FilterMode.Point;
            _win = new float[Win];
            _hist = new float[2 * Win];
            _block = new float[0];
            _zeros = new float[Clear * 2];
            _clip = AudioClip.Create("chroma", Ring, 2, _sr, false);
            output.clip = _clip;
            output.loop = true;
            output.priority = 0;

            _lead = minLead;
            _last = new float[Controls];
            for (int i = 0; i < Controls; i++) _last[i] = Panel(i);
            OnDeserialization(); // a late joiner may already have the panel
            ShowReadout();
        }

        void Update()
        {
            if (PollPanel()) Share();
            if (runner.busy) return;
            if (_tapsDirty) { RunTaps(); return; }
            if (!_tapsReady) return;

            double now = AudioSettings.dspTime;
            int count = (int)System.Math.Round((now - _lastDsp) * _sr);
            if (count <= 0) return; // the mixer hasn't produced a new block yet
            _lastDsp = now;
            if (count > MaxBlock) count = MaxBlock; // after a hitch: keep the newest samples

            int rows = 0;
            for (int i = 0; i < inputs.Length && rows < maxInputs; i++)
            {
                AudioSource s = inputs[i];
                if (s == null || (s.clip != null && !s.isPlaying)) continue;
                _gains[rows] = _level[i] < 0 ? 1f : _juno.level[_level[i]]; // Juno voices stay at a constant volume; their envelope is applied here
                int at = Src + rows * 2 * Win;
                s.GetOutputData(_win, 0);
                System.Array.Copy(_win, 0, _in, at, Win);
                s.GetOutputData(_win, 1);
                System.Array.Copy(_win, 0, _in, at + Win, Win);
                rows++;
            }
            if (rows == 0)
            {
                if (output.isPlaying && now - _lastActive > 0.25) { output.Stop(); _write = -1; _hist = new float[2 * Win]; }
                return;
            }
            _lastActive = now;

            System.Array.Copy(_hist, 0, _in, Coef, 2 * Win);
            System.Buffer.BlockCopy(_in, 0, _bytes, 0, (Src + rows * 2 * Win) * 4); // rows past `rows` are never read
            _tex.LoadRawTextureData(_bytes);
            _tex.Apply(false);
            mixMaterial.SetInteger("_Rows", rows);
            mixMaterial.SetFloat("_Gain", 1f / Send);
            mixMaterial.SetFloatArray("_Gains", _gains);
            VRCGraphics.Blit(_tex, mixed, mixMaterial); // runs on the GPU before the filter below

            float c = color.value;
            _count = count;
            _launchDsp = now;
            runner.SetFunction("process");
            runner.wasmMaterial.SetTexture("_WasmInput", mixed);
            runner.wasmMaterial.SetInteger("_WasmInputWidth", Taps);
            runner.SetNamedArgInt("rows", 1); // the sum
            runner.SetNamedArgInt("count", count);
            // Cutting off-scale notes alone leaves a held in-scale note dry; the in-scale boost and resonance are what you hear.
            bool on = bypass.value < 0.5f;
            runner.SetNamedArgFloat("mix", on ? Mathf.Min(c / 50f, 1f) : 0f);
            runner.SetNamedArgFloat("sweet", on ? Mathf.Min(c / 100f, 1f) : 0f);
            runner.SetNamedArgFloat("feedback", on ? c / 200f * 0.7f : 0f);
            runner.SetNamedArgInt("delay", Delay());
            runner.Run();
        }

        private void RunTaps()
        {
            _tapsDirty = false;
            runner.SetFunction("taps");
            runner.SetNamedArgInt("root", (int)key.value);
            runner.SetNamedArgInt("mask", _masks[(int)scale.value]);
            runner.SetNamedArgFloat("cycles", 34f); // window = 34 periods: a cut note's notch ends at its neighbouring semitones
            runner.SetNamedArgFloat("sr", _sr);
            runner.SetNamedArgInt("delay", Delay());
            runner.Run();
        }

        public void OnWasmComplete()
        {
            if (runner.resultFunction == "taps")
            {
                System.Array.Copy(runner.resultFloat, 0, _in, 0, Coef);
                _tapsReady = true;
                return;
            }
            int n = _count * 2;
            if (_block.Length != n) _block = new float[n];
            System.Array.Copy(runner.resultFloat, _block, n);
            System.Array.Copy(_hist, n, _hist, 0, 2 * Win - n); // the feedback reads this: newest output last
            System.Array.Copy(_block, 0, _hist, 2 * Win - n, n);

            if (!output.isPlaying) { output.Play(); _write = -1; }
            int play = output.timeSamples;
            int ahead = (_write - play + Ring) % Ring;
            double now = AudioSettings.dspTime;
            if (_write >= 0 && (ahead < Guard || ahead > Ring / 2)) // too late: write further ahead from now on
            {
                // A hitch lets the play head overtake the write position: grow by the frames it got ahead, not just one step.
                int behind = ahead > Ring / 2 ? Ring - ahead : 0;
                _lead = Mathf.Min(_lead + 512 + behind, Mathf.Min(maxLead, Ring / 2 - MaxBlock - Clear));
                _lastLate = now;
                _write = -1;
            }
            else if (_write >= 0 && _lead > minLead && now - _lastLate > 5.0 && ahead - 256 >= Guard + 512)
            {
                _lead -= 256; // in time for 5 s: pull 256 frames closer (overwrites the end of the previous block once)
                _lastLate = now;
                _write = (_write - 256 + Ring) % Ring;
            }
            if (_write < 0) _write = (play + _lead) % Ring; // first block or late: resync
            ahead = (_write - play + Ring) % Ring;
            float latency = (float)((now - _launchDsp) * 1000.0) + ahead * 1000f / _sr;
            if (Mathf.Abs(latency - _latencyMs) > 5f) { _latencyMs = latency; ShowReadout(); }
            _clip.SetData(_block, _write); // SetData wraps around the clip
            _write = (_write + _count) % Ring;
            _clip.SetData(_zeros, _write); // so a stall plays silence, not old audio
        }

        // Filter delay in frames; the taps in _in were built with the mode's value. LOW cuts only notes whose window fits in it twice.
        private int Delay() { return mode.value > 0.5f ? 1024 : 2048; }

        // ---- panel

        private float Panel(int i)
        {
            if (i == 0) return color.value;
            if (i == 1) return key.value;
            if (i == 2) return scale.value;
            if (i == 3) return bypass.value;
            return mode.value;
        }

        private bool PollPanel()
        {
            bool moved = false;
            for (int i = 0; i < Controls; i++)
            {
                float v = Panel(i);
                if (v == _last[i]) continue;
                if (i == 1 || i == 2 || i == 4) _tapsDirty = true;
                _last[i] = v;
                moved = true;
            }
            if (moved) ShowReadout();
            return moved;
        }

        private void Share()
        {
            if (!Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
            if (_sync.Length != Controls) _sync = new float[Controls];
            System.Array.Copy(_last, _sync, Controls);
            RequestSerialization();
        }

        public override void OnDeserialization()
        {
            if (_last == null || _sync.Length != Controls) return;
            color.value = _sync[0];
            key.value = _sync[1];
            scale.value = _sync[2];
            bypass.value = _sync[3];
            mode.value = _sync[4];
            for (int i = 0; i < Controls; i++)
            {
                float v = Panel(i);
                if (v != _last[i] && (i == 1 || i == 2 || i == 4)) _tapsDirty = true;
                _last[i] = v; // so Update doesn't take it for a local move
            }
            ShowReadout();
        }

        private void ShowReadout()
        {
            if (readout == null) return;
            string latency = _latencyMs > 0f ? "   ~" + Mathf.RoundToInt(_latencyMs) + " ms" : "";
            readout.text = (bypass.value > 0.5f ? "BYPASS"
                : _keyNames[(int)key.value] + " " + _scaleNames[(int)scale.value] + "   COLOR " + Mathf.RoundToInt(color.value)
                  + (mode.value > 0.5f ? "   LOW" : "   HQ")) + latency;
        }
    }
}
