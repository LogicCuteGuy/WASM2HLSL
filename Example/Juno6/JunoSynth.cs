using LogicCuteGuy.Wasm2Hlsl;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

namespace LogicCuteGuy.Wasm2Hlsl.Samples
{
    /// <summary>
    /// Juno-6 style polysynth. juno6.wasm renders one held note per key (C..B, stereo, chorus included)
    /// in a single GPU pass whenever a control changes; playing is then just AudioSource playback:
    /// a head clip (attack/decay) followed sample-exactly by a looping sustain clip, so notes hold forever
    /// and the release (a volume fade here) always runs to the end.
    /// Every press takes a fresh voice (the previous one keeps releasing), so fast repeats never cut a tail;
    /// when all voices are busy the quietest releasing one is stolen. Other octaves use AudioSource.pitch.
    /// Plays from the on-panel keys (JunoKey) and from MIDI (VRCMidiListener on this GameObject, or a VRCMidiPlayer).
    /// Networked: the panel is synced (whoever moves a control takes ownership) and every client renders it on its own GPU;
    /// notes reach the others as network events. The <c>env_curve</c> export draws the envelope on <see cref="envDisplay"/>.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class JunoSynth : UdonSharpBehaviour
    {
        // Must match juno6.rs.
        private const int Keys = 12, Frames = 96000, Head = 64000, SampleRate = 32000, BaseNote = 48;

        public WasmGpuRunner runner;
        [Tooltip("One AudioSource per voice. More voices = more overlapping release tails.")]
        public AudioSource[] voices;
        [Tooltip("Second AudioSource per voice: plays the sustain loop after the head.")]
        public AudioSource[] loopVoices;
        [Tooltip("Scales every voice volume. An insert such as Chroma sets it near 0 so the voices are inaudible and it plays " +
                 "them back scaled up: VRChat replaces world mixer groups, and a muted source gives GetOutputData only zeros.")]
        public float send = 1f;
        [Tooltip("Read by an insert such as Chroma: each voice's velocity * release gain. While send < 1 the voice volume stays constant " +
                 "(a falling volume would reach Unity's inaudible limit and cut the sound with a click), so the insert applies this itself.")]
        public float[] level;
        [Tooltip("0..1 faders in kernel order: LFO rate, LFO delay, DCO LFO, PWM, sub, noise, HPF, VCF freq, res, VCF env, VCF LFO, kybd, A, D, S, level.")]
        public Slider[] faders;
        [Tooltip("Release only fades the voice here, so it never re-renders.")]
        public Slider release;
        [Tooltip("0/1 switches.")]
        public Slider saw, pulse, pwmManual, vcaGate;
        [Tooltip("0..2 switches: off/I/II and 16'/8'/4'.")]
        public Slider chorus, range;
        [Tooltip("Optional: shows the ADSR shape (env_curve export, 256x64).")]
        public RawImage envDisplay;

        private AudioClip[] _heads, _loops;
        private float[] _headBuf, _loopBuf;
        private Slider[] _controls; // faders, saw, pulse, pwmManual, vcaGate, chorus, range, release
        private float[] _last;
        [UdonSynced] private float[] _sync = new float[0];
        private bool _dirty = true, _ready, _pedal;
        private float _changedAt;
        private int _flags;
        private Texture2D _envTex;

        private int[] _note;
        private float[] _gain, _vel, _startedAt;
        private bool[] _releasing, _held;

        void Start()
        {
            runner.callbackTarget = this;
            _heads = new AudioClip[Keys];
            _loops = new AudioClip[Keys];
            for (int k = 0; k < Keys; k++)
            {
                _heads[k] = AudioClip.Create("juno" + k, Head, 2, SampleRate, false);
                _loops[k] = AudioClip.Create("juno" + k + " loop", Frames - Head, 2, SampleRate, false);
            }
            _headBuf = new float[Head * 2];
            _loopBuf = new float[(Frames - Head) * 2];

            int c = faders.Length;
            _controls = new Slider[c + 7];
            System.Array.Copy(faders, _controls, c);
            _controls[c] = saw; _controls[c + 1] = pulse; _controls[c + 2] = pwmManual; _controls[c + 3] = vcaGate;
            _controls[c + 4] = chorus; _controls[c + 5] = range; _controls[c + 6] = release;
            _last = new float[_controls.Length];
            for (int i = 0; i < _last.Length; i++) _last[i] = _controls[i].value;
            OnDeserialization(); // a late joiner may already have the panel

            int n = voices.Length;
            _note = new int[n];
            _gain = new float[n];
            level = new float[n];
            _vel = new float[n];
            _startedAt = new float[n];
            _releasing = new bool[n];
            _held = new bool[n];
            for (int v = 0; v < n; v++) _note[v] = -1;
        }

        void Update()
        {
            // Poll the controls: re-render once they stop moving and the GPU is free.
            bool changed = false;
            int r = _controls.Length - 1; // release only fades voices: shared, never re-rendered
            for (int i = 0; i < r; i++) changed |= Poll(i);
            if (Poll(r) | changed) Share();
            if (changed) { _dirty = true; _changedAt = Time.time; }
            if (_dirty && !runner.busy && Time.time - _changedAt > 0.15f) Render();

            float fade = Mathf.Exp(-Time.deltaTime / ReleaseTau());
            for (int v = 0; v < voices.Length; v++)
            {
                if (!_releasing[v]) continue;
                _gain[v] *= fade;
                level[v] = _gain[v] * _vel[v];
                voices[v].volume = loopVoices[v].volume = send < 1f ? send : level[v];
                if (_gain[v] < 0.001f) { voices[v].Stop(); loopVoices[v].Stop(); _releasing[v] = false; _note[v] = -1; level[v] = 0f; }
            }
        }

        private bool Poll(int i)
        {
            float value = _controls[i].value;
            if (_last[i] == value) return false;
            _last[i] = value;
            return true;
        }

        // A local control moved: take the panel and send it.
        private void Share()
        {
            if (!Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
            if (_sync.Length != _last.Length) _sync = new float[_last.Length];
            System.Array.Copy(_last, _sync, _last.Length);
            RequestSerialization();
        }

        public override void OnDeserialization()
        {
            if (_controls == null || _sync.Length != _controls.Length) return;
            for (int i = 0; i < _controls.Length; i++)
            {
                if (_last[i] == _sync[i]) continue;
                _controls[i].value = _sync[i];
                _last[i] = _controls[i].value; // so Update doesn't take it for a local move
                if (i < _controls.Length - 1) { _dirty = true; _changedAt = Time.time; }
            }
        }

        private float ReleaseTau()
        {
            if (vcaGate.value > 0.5f) return 0.005f;
            return 0.002f * Mathf.Pow(2f, release.value * 12.5f) / 4.6f; // fader = time to -40 dB, like decay
        }

        public void Render()
        {
            _flags = (saw.value > 0.5f ? 1 : 0) | (pulse.value > 0.5f ? 2 : 0) | (pwmManual.value > 0.5f ? 4 : 0)
                   | (vcaGate.value > 0.5f ? 8 : 0) | ((int)chorus.value << 4) | ((int)range.value << 6);
            runner.SetFunction("main");
            runner.SetNamedArgInt("flags", _flags);
            for (int i = 0; i < faders.Length; i++) runner.SetArgFloat(2 + i, faders[i].value);
            runner.Run();
            _dirty = false;
        }

        public void OnWasmComplete()
        {
            if (runner.resultFunction == "env_curve") { DrawEnv(); return; }
            float[] r = runner.resultFloat;
            for (int k = 0; k < Keys; k++)
            {
                System.Array.Copy(r, k * Frames * 2, _headBuf, 0, _headBuf.Length);
                System.Array.Copy(r, (k * Frames + Head) * 2, _loopBuf, 0, _loopBuf.Length);
                _heads[k].SetData(_headBuf, 0);
                _loops[k].SetData(_loopBuf, 0);
            }
            _ready = true;

            if (envDisplay == null) return;
            runner.SetFunction("env_curve");
            runner.SetNamedArgInt("flags", _flags);
            runner.SetNamedArgFloat("attack", faders[12].value);
            runner.SetNamedArgFloat("decay", faders[13].value);
            runner.SetNamedArgFloat("sustain", faders[14].value);
            runner.Run();
        }

        private void DrawEnv()
        {
            int[] r = runner.resultLo;
            int w = runner.target.width, h = r.Length / w;
            if (_envTex == null) _envTex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            Color32 bg = new Color32(20, 20, 24, 255), fill = new Color32(90, 50, 20, 255), line = new Color32(255, 140, 40, 255);
            Color32[] px = new Color32[r.Length];
            for (int i = 0; i < px.Length; i++) px[i] = r[i] == 2 ? line : r[i] == 1 ? fill : bg;
            _envTex.SetPixels32(px);
            _envTex.Apply(false);
            envDisplay.texture = _envTex;
        }

        /// <summary>Plays a MIDI note (0..127) at velocity 0..1 here and for everyone else; returns the local voice index or -1.</summary>
        public int NoteOn(int note, float velocity)
        {
            SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(NetNoteOn), note, velocity);
            return StartVoice(note, velocity);
        }

        [NetworkCallable(100)]
        public void NetNoteOn(int note, float velocity) { StartVoice(note, velocity); }

        private int StartVoice(int note, float velocity)
        {
            if (!_ready) return -1;
            int semis = note - BaseNote;
            int oct = Mathf.FloorToInt(semis / 12f);

            int v = -1;
            for (int i = 0; i < voices.Length && v < 0; i++) if (!IsPlaying(i)) v = i;
            if (v < 0) // all busy: steal the quietest releasing voice...
                for (int i = 0; i < voices.Length; i++)
                    if (_releasing[i] && (v < 0 || _gain[i] < _gain[v])) v = i;
            if (v < 0)
            {
                v = 0; // ...or else the oldest held one
                for (int i = 1; i < voices.Length; i++) if (_startedAt[i] < _startedAt[v]) v = i;
            }

            int k = semis - oct * 12;
            float pitch = Mathf.Pow(2f, oct);
            AudioSource s = voices[v], loop = loopVoices[v];
            s.Stop();
            loop.Stop();
            s.clip = _heads[k];
            loop.clip = _loops[k];
            loop.loop = true;
            s.pitch = loop.pitch = pitch;
            level[v] = velocity;
            s.volume = loop.volume = send < 1f ? send : velocity;
            _gain[v] = 1f;
            _vel[v] = velocity;
            _releasing[v] = false;
            _held[v] = false;
            _note[v] = note;
            _startedAt[v] = Time.time;
            double start = AudioSettings.dspTime + 0.02;
            s.PlayScheduled(start);
            loop.PlayScheduled(start + (double)Head / SampleRate / pitch); // the loop takes over exactly where the head ends
            return v;
        }

        private bool IsPlaying(int v) { return voices[v].isPlaying || loopVoices[v].isPlaying; }

        /// <summary>Releases a note here and for everyone else (held back while the local sustain pedal is down).</summary>
        public void NoteOff(int note)
        {
            if (_pedal)
            {
                for (int v = 0; v < voices.Length; v++) if (_note[v] == note && !_releasing[v]) _held[v] = true;
                return;
            }
            SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(NetNoteOff), note);
            NetNoteOff(note);
        }

        // ponytail: remote note-offs match by note number only, so two players holding the same note release together.
        [NetworkCallable(100)]
        public void NetNoteOff(int note)
        {
            for (int v = 0; v < voices.Length; v++)
                if (_note[v] == note && !_releasing[v]) Release(v);
        }

        private void Release(int v) { _releasing[v] = true; _held[v] = false; }

        // ---- MIDI (VRCMidiListener / VRCMidiPlayer)

        public override void MidiNoteOn(int channel, int number, int velocity)
        {
            if (velocity == 0) NoteOff(number);
            else NoteOn(number, velocity / 127f);
        }

        public override void MidiNoteOff(int channel, int number, int velocity) { NoteOff(number); }

        public override void MidiControlChange(int channel, int number, int value)
        {
            float x = value / 127f;
            switch (number)
            {
                case 64: // sustain pedal
                    _pedal = value >= 64;
                    if (!_pedal)
                        for (int v = 0; v < voices.Length; v++)
                            if (_held[v]) NoteOff(_note[v]);
                    break;
                case 1: faders[2].value = x; break;  // mod wheel -> DCO LFO
                case 76: faders[0].value = x; break; // LFO rate
                case 77: faders[2].value = x; break; // vibrato depth -> DCO LFO
                case 78: faders[1].value = x; break; // LFO delay
                case 74: faders[7].value = x; break; // VCF freq
                case 71: faders[8].value = x; break; // VCF res
                case 73: faders[12].value = x; break; // attack
                case 75: faders[13].value = x; break; // decay
                case 72: release.value = x; break;
                case 7: faders[15].value = x; break; // volume -> level
            }
        }
    }
}
