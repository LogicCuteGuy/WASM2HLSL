# Juno-6 example

A playable Juno-6 style polysynth: center station of `Example/Showcase.unity`.

- **Panel**: LFO (rate, delay), DCO (LFO, PWM, PWM mod, pulse, saw, sub, noise, range 16'/8'/4'), HPF, VCF (freq, res, env, LFO, kybd),
  VCA (env/gate, level), ENV (A D S R), chorus off/I/II.
- **Keyboard**: 3 octaves, C2 to C5. Each key has a `JunoKey` and an `EventTrigger` (pointer down = note on, pointer up/exit = note off),
  so a note sounds for as long as you hold it. In VR, each hand can hold a key.
- **Polyphony**: 12 voices. Every press takes a fresh voice and the previous one keeps releasing, so fast repeats never cut a tail.
  When all 12 are busy, the quietest releasing voice is stolen.
- **MIDI**: `VRCMidiListener` on the `Juno6` object. Note on/off with velocity, sustain pedal (CC 64), and CCs 1/77 DCO LFO, 76 LFO rate,
  78 LFO delay, 74 VCF freq, 71 res, 73 attack, 75 decay, 72 release, 7 level. A `VRCMidiPlayer` can target the same behaviour.
  In VRChat, launch with `--midi=<device name>`; in the editor, use the SDK's MIDI window.
- **Networked**: the panel (all faders and switches) is a Manual `[UdonSynced] float[]`; moving a control takes ownership.
  Every client renders the sound on its own GPU. Note on/off (keys and MIDI) go to the other players as `[NetworkCallable]`
  events, so everyone hears everyone play. Remote note-offs match by note number.
- **Two exports**: `main` renders the audio; `env_curve` (`@size 256x64`) draws the ADSR shape.
  It shows on the panel under ENV (`JunoSynth.envDisplay`, a RawImage) and runs right after each `main` render.

## How it works

`juno6.rs` renders one held note per key (C..B, 32 kHz stereo, 3 s each) in **one** GPU pass, about 2.3 M pixels:
`id = (key * 96000 + frame) * 2 + channel`. Udon re-renders when a control stops moving, which takes about 30 to 90 ms on a desktop GPU.
Playing a note is then only AudioSource playback:

- Each key is split into a **head** clip (0 to 2 s, attack/decay) and a **loop** clip (2 to 3 s, sustain). The kernel crossfades the last
  0.25 s back into the audio before 2 s, so the loop has no seam. `PlayScheduled` starts the loop exactly where the head ends,
  so a held note never runs out. Note-off fades both sources over the release time. Each voice has two AudioSources.
- Other octaves play the same clips with `AudioSource.pitch` (×2 per octave).
- The VCF/HPF are recursive filters. Each pixel re-runs them over the previous 256 samples from zero state:
  a stateless, truncated IIR. The chorus (BBD-style delay, left/right in antiphase) reuses that warm-up loop
  to get the delayed samples.
- The `f32` export uses the runner's float readback, and Udon slices it into clips with `Array.Copy`. There's no per-sample Udon loop.

Simplifications: the envelope settles on S by 1.75 s, so attack or decay longer than that is shortened. LFO delay is capped at 1.2 s.
Release fades the VCA only and doesn't sweep the filter. Notes outside C3..B3 change their envelope speed by the pitch factor.

Rebuild the wasm (then the importer Size must stay 16384 × 141):

```bash
RUSTFLAGS="-C link-arg=-zstack-size=1024 -C target-cpu=mvp -C target-feature=+bulk-memory,+sign-ext,+nontrapping-fptoint" cargo build --release --target wasm32-unknown-unknown
```
