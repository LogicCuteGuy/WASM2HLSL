// Chroma-style "harmonic sweetener" insert: keeps the sound and removes the parts that sit off a musical scale.
// Main filter = a delay of `delay` frames MINUS a bank of Hann-windowed cosines, one per off-scale note (MIDI 28..108),
// each centred on that same delay so the bank is linear-phase and cancels exactly against the delayed dry signal.
// So in-scale notes, highs above C8 and bass the bank can't resolve all pass unchanged; only off-scale notes are cut.
// Constant-Q: each note's window lasts `cycles` periods of that note (34: off-scale -35 dB, 24: -16 dB). A note is only
// cut when its window fits in 2 * delay (HQ: above ~400 Hz, LOW: above ~560 Hz); below that its semitones blur, so it passes.
// Sweeten: a second bank, of the in-scale notes, is added on top of the input, so in-scale notes and harmonics stand out
// (a held in-scale note would otherwise pass unchanged). Tail: the output is fed back through that same bank,
// so in-scale notes keep ringing after the input stops.
//
// Exports:
//   `taps`    one pixel = one FIR coefficient: [0, TAPS) main filter, [TAPS, 2 * TAPS) in-scale bank (sweeten + tail).
//             Run when key / scale / mode change; Udon puts the result at the start of the input.
//   `process` one pixel = one output sample (interleaved stereo).
// Input layout (f32 bits, see ChromaFx.cs): [2 * TAPS coefficients][output history, interleaved, WIN frames]
//                                           [source 0 L][source 0 R][source 1 L]... each WIN samples, newest last.
//
// Build:
//   RUSTFLAGS="-C link-arg=-zstack-size=1024 -C target-cpu=mvp -C target-feature=+bulk-memory,+sign-ext,+nontrapping-fptoint" //   cargo build --release --target wasm32-unknown-unknown
#![no_std]

#[panic_handler]
fn panic(_: &core::panic::PanicInfo) -> ! { loop {} }

#[link(wasm_import_module = "env")]
extern "C" {
    fn input(i: i32) -> i32;
    fn hlsl_cos(x: f32) -> f32;
    fn hlsl_exp2(x: f32) -> f32;
}

const TAPS: i32 = 4096; // FIR length: 85 ms at 48 kHz
const WIN: i32 = 8192; // samples per source channel (AudioSource.GetOutputData length)
const HIST: i32 = 2 * TAPS; // output history (2 * WIN floats), after both coefficient sets
const SRC: i32 = HIST + 2 * WIN; // first source row
const DELAY: i32 = WIN - TAPS; // feedback delay in frames: >= any block, so that output is already known
const TWO_PI: f32 = 6.2831853;

fn cos(v: f32) -> f32 { unsafe { hlsl_cos(v) } }
fn x(i: i32) -> f32 { f32::from_bits(unsafe { input(i) } as u32) }
fn frac(v: f32) -> f32 { v - (v as i32) as f32 } // v >= 0

/// FIR coefficient `id` of the scale filter: [0, 4096) main filter, [4096, 8192) tail filter.
/// `root` 0..11 (C..B); `mask` bit n = pitch class root+n is in the scale; `cycles` = window length in periods of each note;
/// `sr` = output sample rate; `delay` = latency of the main filter in frames (<= 2048). Gain at each cut note is about 0.
/// @size 8192
#[no_mangle]
pub extern "C" fn taps(id: i32, root: i32, mask: i32, cycles: f32, sr: f32, delay: i32) -> f32 {
    let tail = id >= TAPS;
    let k = (id & (TAPS - 1)) as f32;
    let d = delay as f32;
    let mut h = if !tail && id == delay { 1.0 } else { 0.0 };
    let mut m = 28; // E1 (41 Hz) .. C8 (4.2 kHz)
    while m <= 108 {
        let in_scale = (mask >> ((m - root + 120) % 12)) & 1 != 0;
        let f = 440.0 * unsafe { hlsl_exp2((m - 69) as f32 / 12.0) };
        let len = cycles * sr / f;
        // Both banks are centred on the delay, so they line up in phase with the delayed dry signal:
        // the main filter subtracts the off-scale notes, the in-scale bank adds to the in-scale ones.
        if (tail == in_scale) && len <= 2.0 * d {
            let j = k - (d - 0.5 * len); // window position
            if j >= 0.0 && j < len {
                let t = if k > d { k - d } else { d - k }; // cos is even
                let hann = 0.5 - 0.5 * cos(TWO_PI * j / len);
                let c = hann * cos(TWO_PI * frac(f * t / sr)) * 4.0 / len; // a Hann-windowed cosine has gain len/4 at its frequency
                if tail { h += c; } else { h -= c; }
            }
        }
        m += 1;
    }
    h
}

/// Output sample `id` = frame * 2 + channel of the newest `count` frames (count <= WIN - TAPS).
/// `rows` sources are summed. `mix` 0 = dry (bypass), 1 = off-scale notes removed. `sweet` = in-scale bank added on top
/// (1 = +6 dB on in-scale notes). `feedback` 0..<1 = tail length.
/// `delay` must be the one `taps` was built with: the dry signal is delayed by it too, so bypass keeps the same timing.
/// @size 8192
#[no_mangle]
pub extern "C" fn process(id: i32, rows: i32, count: i32, mix: f32, sweet: f32, feedback: f32, delay: i32) -> f32 {
    let frame = id >> 1;
    let ch = id & 1;
    if frame >= count { return 0.0; }
    let n = WIN - count + frame; // index in the source rows; the newest sample is WIN - 1
    let mut dry = 0.0;
    let mut wet = 0.0;
    let mut res = 0.0;
    let mut s = 0;
    while s < rows {
        let base = SRC + (s * 2 + ch) * WIN + n;
        dry += x(base - delay);
        if mix > 0.0 || sweet > 0.0 {
            let mut k = 0;
            while k < TAPS { let v = x(base - k); wet += x(k) * v; res += x(TAPS + k) * v; k += 1; }
        }
        s += 1;
    }
    let mut out = dry + mix * (wet - dry) + sweet * res;
    if feedback > 0.0 {
        // History frame WIN - 1 is the frame just before this block, so output frame n - DELAY - k
        // sits at history frame WIN - DELAY + frame - k, whatever count is.
        let base = HIST + (WIN - DELAY + frame) * 2 + ch;
        let mut tail = 0.0;
        let mut k = 0;
        while k < TAPS { tail += x(TAPS + k) * x(base - k * 2); k += 1; }
        out += feedback * tail;
    }
    out
}
