# Chroma example

A live audio insert in the style of Xynth's [Chroma](https://www.xynth.audio/plugins/chroma) "harmonic sweetener":
it removes the notes that sit off a musical scale and leaves everything else (in-scale notes, highs, deep bass) untouched. It runs on the GPU in real time.
The video player and the Juno6 share one chain (one runner, panel and bypass). The sources' own code isn't touched:

```
Video player speaker + Juno6 voices (24 AudioSources, at -100 dB) ─▶ Chroma (×100000) ─▶ Output (at the Juno6)
```

**Add it to a scene:** open `Example/Showcase.unity`, run **Tools > WASM2HLSL > Add Chroma Chain To Scene**, then save.
Running it again rebuilds the chain: older Chroma chains are removed and an existing video player is reused.
The menu comes from the setup script at `Assets/ChromaSetup/Editor/ChromaSetup.cs`, which lives in the project, not the package. It does three things:
- Adds the chain panel above the Juno-6.
- Adds the SDK's `UdonSyncPlayer (Unity)` video player behind spawn (z = -4), unless the scene has one.
- Creates `ChromaMix.mat` and `ChromaMixed.renderTexture` here if missing.
- Takes the sources off the old `ChromaSend` mixer group. At Start, `ChromaFx` itself turns them down to -100 dB: the video
  speaker's volume, and `JunoSynth.send` for the Juno voices (JunoSynth rewrites their volumes every frame). The mixdown scales them
  back up ×100000; float samples lose nothing. Not a muted mixer group: the VRChat client replaces every world AudioSource's mixer
  group with its own, and a muted AudioSource gives `GetOutputData` only zeros. (`ChromaSend.mixer` is no longer used.)

## Panel

| Control | |
|---|---|
| **COLOR** 0..200 | The amount of effect. Off-scale notes fade out between 0 and 50. In-scale notes (and in-scale harmonics) are boosted up to +6 dB by 100. In-scale resonance (the tail) grows over the whole range: feedback 0.35 at 100, 0.7 at 200. |
| **KEY** 0..11 | Root, C..B. |
| **SCALE** 0..4 | major, minor, major pentatonic, minor pentatonic, chromatic (snaps to equal temperament). |
| **HQ / LOW** | Filter delay. HQ: 43 ms, cuts off-scale notes from about 400 Hz up. LOW: 21 ms, from about 800 Hz up. |
| **BYPASS** | The dry signal goes through the same path. Latency stays the same, so switching it never jumps. |

The readout shows the buffering latency the chain measures (block wait + GPU readback + write-ahead). Filter delay comes on top.
The panel is synced (Manual). Whoever moves a control takes ownership, and every client processes its own audio.

## How it works

- `chroma.rs` has two exports.
  - `taps` builds two FIRs of 4096 taps from Hann-windowed cosines, one per note, MIDI 28 to 108 (41 Hz to 4.2 kHz).
    Each window lasts 34 cycles of its note, so a cut note's notch ends exactly at its neighbouring semitones.
    - **Main filter** = the dry signal delayed by 43 ms (LOW: 21 ms) **minus** one cosine per off-scale note, all centred on that delay.
      They are linear-phase and cancel exactly against the delayed dry, so whatever isn't an off-scale note passes unchanged.
      A note is only cut when its window fits in twice the delay; lower notes can't be told from their neighbours, so they pass.
    - **In-scale bank** = one cosine per in-scale note, centred on the same delay. It is added on top of the main filter
      (sweeten: in-scale notes and harmonics stand out, so even a held in-scale note changes) and runs over the output history (tail).

    It runs only when KEY, SCALE or the mode changes.
  - `process` runs one pixel per output sample. It sums the input rows, runs the main FIR and the in-scale bank over them (one loop), and adds the in-scale bank over the output history.
- Every audio block (when `AudioSettings.dspTime` moves), `ChromaFx` does the following:
  1. Reads the newest 8192 samples of each playing input with `GetOutputData`.
  2. Packs them into an RFloat texture with `Buffer.BlockCopy` + `LoadRawTextureData`.
  3. Sums the source rows on the GPU (`ChromaMix.shader`, a Blit into `ChromaMixed`). The filter is linear, so this equals
     filtering each source and summing, but `process` then runs on one row whatever the number of sources.
  4. Runs `process` for the new frames.
  5. Writes the result just ahead of the play head of a looping clip. The write-ahead tunes itself: it starts at
     `minLead` (3072 frames, 64 ms). When a block lands inside the mixer's last DSP buffer it grows by 512; when a hitch let the
     play head pass the write position it grows by that gap too, up to `maxLead` (16384 frames, 341 ms). It shrinks by 256
     after 5 s with every block on time.

  There are no per-sample Udon loops.
- The output history goes back in as an input row, and the in-scale bank runs over it (feedback).
  The feedback delay is fixed at 4096 frames, so it doesn't depend on block size.

Measured (`Tests~/chroma_check.js`, C major, 48 kHz):

| Tone | HQ | LOW |
|---|---|---|
| A2 / C#3 (bass, passes) | 1.00 / 1.00 | 1.00 / 1.00 |
| A4 / C5 / E6 (in scale) | 0.99 / 0.98 / 1.00 | 1.00 / 1.00 / 1.00 |
| A#4 / C#5 (off scale) | 0.00 / 0.00 | 1.00 / 1.00 (below LOW's range) |
| F#6 (off scale) | 0.00 | 0.00 |
| 8 kHz (passes) | 1.00 | 1.00 |

Bypass is the bit-exact dry signal, delayed by the filter delay. Feedback 0.5 rings at 0.50. `taps` on the D3D11 GPU matches Node to 4e-7.

**Limits**
- **Latency:** buffering is about 50 to 80 ms on a desktop at 90 fps (the readout shows it), plus the filter delay.
  The filter delay is 43 ms (HQ) or 21 ms (LOW) for everything, bypass included. The video audio trails the picture by that much.
  If the output crackles, raise `minLead` on the ChromaFx. Long hitches (avatars loading in a busy instance) raise the write-ahead
  automatically, up to `maxLead`; after a hitch longer than about 85 ms (`MaxBlock`) the audio from that hitch is lost.
- **Busy instances:** Unity plays at most 32 sounds at once and silences the lowest-priority, then quietest, sources first.
  The inputs here play at -60 dB, so `ChromaFx` sets every input and the output to priority 0 (the highest) at Start.
- **Low notes:** the window can't separate semitones below about 400 Hz (LOW: 800 Hz), so off-scale notes there pass unchanged.
- **GPU cost:** about 0.7 ms per block (1.2 ms with the tail), measured with 800 frames on a desktop GPU, whatever the number of sources.
  Before the mixdown, 12 Juno voices cost 9 ms. Only the texture upload still grows with active inputs (up to `maxInputs` = 13).

Rebuild the wasm:

```bash
RUSTFLAGS="-C link-arg=-zstack-size=1024 -C target-cpu=mvp -C target-feature=+bulk-memory,+sign-ext,+nontrapping-fptoint" cargo build --release --target wasm32-unknown-unknown
cp target/wasm32-unknown-unknown/release/chroma.wasm . && rm -rf target Cargo.lock
node ../../Tests~/chroma_check.js chroma.wasm
```
