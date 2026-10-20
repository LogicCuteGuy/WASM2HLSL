# Audio DSP example

One pixel = one 48 kHz audio sample. `dsp.rs` synthesizes a detuned-saw arpeggio and runs it through a
65-tap windowed-sinc FIR lowpass whose cutoff sweeps 200 Hz → 8 kHz → 200 Hz every 2 s.
FIR filters are stateless, so every sample is independent, which is what a pixel shader needs.
Recursive filters (IIR, biquads, reverb feedback) don't map directly, because pixels can't read each other's output.
`Juno6/` works around that by re-running the IIR over a short window in every pixel.

- `dsp.wasm` has two exports, both `@size 512x192` = 98304 samples (2.048 s): `dry(id)` and `sweep(id, taps)`.
  They return 16-bit PCM as `i32`, so Udon only divides.
- `AudioDspDemo` builds the clip with `AudioClip.Create` + `SetData` and loops it. **Interact** cycles off → `dry` → `sweep`.
- **Networked**: `mode` and the server-time start are `[UdonSynced]` (Manual). Whoever interacts takes ownership; every client
  renders the same export and seeks to the same point of the loop.
- See it in `Example/Showcase.unity` (right station).

Verified in the editor: GPU output matches a CPU port of `dsp.rs` on all samples except one saw-wrap edge
(sample 69600, where `t·f` is exactly 319.0); in sweep mode the FIR spreads that one sample over 49 samples. `Renders~/` has both
renders as WAV plus a spectrogram.

Rebuild the wasm:

```bash
RUSTFLAGS="-C link-arg=-zstack-size=1024 -C target-cpu=mvp -C target-feature=+bulk-memory,+sign-ext,+nontrapping-fptoint" cargo build --release --target wasm32-unknown-unknown
```
