# Changelog

## 0.2.0 - 2026-10-22

### Added
- Big constant tables: `.rodata` of 1 KB or more (`static` / `const` tables, `include_bytes!`) is read from a texture
  instead of GPU registers, so it no longer counts toward the 64000-byte memory limit. No source changes needed.
  The `.wasm` asset gets a `rom` texture, already set as `_WasmRom` on every export's material.
- Import setting **Texture Constants From (bytes)** (default 1024; 0 = keep everything in registers).
- The asset summary shows the ROM texture size.
- `Tests~/run.sh` mode 8: a 96 KB table read through the ROM texture, checked bit-for-bit against Node.
  `run.sh` falls back to d3dcompiler when `fxc.exe` is missing.

### Changed
- Example/Mandelbrot: smooth colouring through a 64 KB palette in the ROM texture; exports return packed RGBA.
- Importer version 9: every `.wasm` reimports once. Modules without large `.rodata` produce the same HLSL as before.

### Fixed
- Audio examples dropping out in busy instances: player voices and avatar sounds go over Unity's voice limit (32), and
  Unity silenced our sources first (default priority 128; Chroma's inputs are also the quietest at -60 dB).
  AudioDsp, Juno-6 voices and every Chroma input/output now set priority 0 at Start.
- Chroma: after a hitch that let the play head pass the write position, the write-ahead resynced without growing, so the next
  hitch dropped out again. It now grows by the gap, up to the new `maxLead` field (16384 frames). The output ring is 131072 frames.
- `WasmGpuRunner`: no more Udon exception (`Material.GetInteger`, runner halted) when leaving or quitting a world. The client
  delivers in-flight readbacks after the material is destroyed; the callback now ignores them.

## 0.1.0

- First release.
