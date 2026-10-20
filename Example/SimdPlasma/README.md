# SIMD Plasma example (v128)

An animated plasma written with WebAssembly SIMD (`core::arch::wasm32`, built with `+simd128`).

- Per pixel, `plasma.rs` evaluates four sine terms in the four lanes of one `f32x4` (a polynomial sine: `floor`, `abs`, `mul`, `add`),
  sums them across lanes with two `i32x4_shuffle`s, computes R, G, B in another `f32x4`, converts with `i32x4_trunc_sat_f32x4`,
  packs to bytes with `u16x8_narrow_i32x4` + `u8x16_narrow_i16x8`, brightens with `u8x16_add_sat`, and returns lane 0: packed RGBA8.
- `plasma.wasm` has one export, `plasma(id, width, time, glow)`, `@size 256x256`.
- `SimdPlasmaDemo` re-runs it whenever the runner is free. The quad's material is `WASM2HLSL/Int Display` in *Packed RGBA8* mode
  reading the runner's render texture, so nothing goes through a per-pixel Udon loop.
- **Networked**: time is server time, so all clients show the same frame. Interact toggles the glow (`[UdonSynced]`).
- Verified: GPU output matches Node's WebAssembly on all 65536 pixels within 1 per color byte (GPU float rounding); the shader compiles in under a second.

Add it to `Showcase.unity`: open the scene, run **Tools > WASM2HLSL > Add f64 + SIMD Examples To Scene**, save.
Or by hand: drag `plasma.wasm` into the Hierarchy, add a Quad with a material using `WASM2HLSL/Int Display` (Mode: Packed RGBA8),
add `SimdPlasmaDemo` to any GameObject and assign the runner and the Quad's renderer.

Rebuild the wasm:

```bash
RUSTFLAGS="-C link-arg=-zstack-size=1024 -C target-cpu=mvp -C target-feature=+bulk-memory,+sign-ext,+nontrapping-fptoint,+simd128" cargo build --release --target wasm32-unknown-unknown
```
