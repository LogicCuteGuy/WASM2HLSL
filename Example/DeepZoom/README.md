# Deep Zoom example (f64)

An endless zoom into the Mandelbrot set's seahorse valley, computed in **f64** on the GPU.
f32 runs out of precision after about 17 zoom doublings and the picture turns into blocks; `deepzoom.rs` uses `f64`,
which WASM2HLSL runs as bit-exact IEEE double in software, so the zoom stays sharp to about 44 doublings (10^13).
The demo goes to 32 doublings (4 × 10^9) and starts over; deeper needs more iterations than a frame should spend.

- `deepzoom.wasm` has one export, `zoom(id, width, max_iter, cx_hi, cx_lo, cy_hi, cy_lo, zoom)`, `@size 256x256`.
  Udon only passes floats, so `DeepZoomDemo` splits each double coordinate into the nearest float plus the remainder;
  the kernel adds them back in f64.
- It returns the smooth iteration count × 256 (or -1 inside the set). **No readback loop**: the quad's material uses
  `WASM2HLSL/Int Display` in *Iteration Palette* mode with the runner's render texture as `_MainTex`, so the pixels never go through Udon.
- **Networked**: the zoom start time is `[UdonSynced]`; every client renders the same depth. Interact restarts the zoom.
- Cost: every f64 add/mul is ~100 GPU instructions. The iteration cap grows with depth (`baseIter + iterPerDoubling × zoom`,
  1640 at the bottom); measured on a desktop GPU the whole image takes ~27 ms. So it is computed in `bands` (default 8) horizontal bands,
  one per frame (`runner.bands`: the shader discards the other rows), about 3.5 ms per frame; on Quest raise `bands` or lower `maxZoom` / `iterPerDoubling`. Importing the `.wasm` compiles the shader in about a minute.
- Verified: the GPU result matches Node's WebAssembly on all 65536 pixels (iteration counts exact).

Add it to `Showcase.unity`: open the scene, run **Tools > WASM2HLSL > Add f64 + SIMD Examples To Scene**, save.
Or by hand: drag `deepzoom.wasm` into the Hierarchy (makes the runner), add a Quad with a material using `WASM2HLSL/Int Display`
(Mode: Iteration Palette), add `DeepZoomDemo` to any GameObject and assign the runner and the Quad's renderer.

Rebuild the wasm:

```bash
RUSTFLAGS="-C link-arg=-zstack-size=1024 -C target-cpu=mvp -C target-feature=+bulk-memory,+sign-ext,+nontrapping-fptoint" cargo build --release --target wasm32-unknown-unknown
```
