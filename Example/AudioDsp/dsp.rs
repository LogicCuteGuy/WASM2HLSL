// Audio DSP on the GPU: one pixel = one output sample (48 kHz mono).
// Source: an 8-step arpeggio of two detuned naive sawtooths (harmonic-rich, plucked envelope).
// Exports: `dry` (source only) and `sweep` (source through the effect).
// Effect: windowed-sinc FIR lowpass whose cutoff sweeps 200 Hz -> 8 kHz -> 200 Hz every 2 s.
// FIR needs no state, so every sample is independent — exactly what a pixel shader wants.
// Build:
//   RUSTFLAGS="-C link-arg=-zstack-size=1024 -C target-cpu=mvp -C target-feature=+bulk-memory,+sign-ext,+nontrapping-fptoint" \
//   cargo build --release --target wasm32-unknown-unknown
#![no_std]

#[panic_handler]
fn panic(_: &core::panic::PanicInfo) -> ! { loop {} }

#[link(wasm_import_module = "env")]
extern "C" {
    fn hlsl_sin(x: f32) -> f32;
    fn hlsl_cos(x: f32) -> f32;
    fn hlsl_exp2(x: f32) -> f32;
}

const SR: f32 = 48000.0;
const PI: f32 = 3.14159265;
const STEP: i32 = 6000; // 1/8 s per note
const NOTES: [f32; 8] = [110.0, 130.81, 164.81, 220.0, 196.0, 164.81, 130.81, 98.0];

fn frac(x: f32) -> f32 { x - (x as i32) as f32 } // x >= 0

/// Dry signal at sample n (0 before the start).
fn source(n: i32) -> f32 {
    if n < 0 { return 0.0; }
    let f = NOTES[((n / STEP) & 7) as usize];
    let t = n as f32 / SR;
    let env = unsafe { hlsl_exp2(-((n % STEP) as f32 / SR) * 12.0) };
    let saw = frac(t * f) + frac(t * f * 1.007) - 1.0;
    0.4 * env * saw
}

/// Signed 16-bit PCM, so Udon only has to divide.
fn pcm(y: f32) -> i32 {
    let s = y * 32767.0;
    (if s > 32767.0 { 32767.0 } else if s < -32768.0 { -32768.0 } else { s }) as i32
}

/// The unfiltered arpeggio. `id` = sample index.
/// @size 512x192
#[no_mangle]
pub extern "C" fn dry(id: i32) -> i32 { pcm(source(id)) }

/// The arpeggio through the sweeping FIR lowpass. `taps` = FIR half-length (2*taps+1 coefficients).
/// @size 512x192
#[no_mangle]
pub extern "C" fn sweep(id: i32, taps: i32) -> i32 {
    let t = id as f32 / SR;
    let lfo = 0.5 - 0.5 * unsafe { hlsl_cos(PI * t) };
    let wc = 200.0 * unsafe { hlsl_exp2(lfo * 5.32) } / SR; // cutoff, cycles/sample
    let (mut acc, mut norm) = (0.0f32, 0.0f32);
    let mut k = -taps;
    while k <= taps {
        let x = 2.0 * PI * wc * k as f32;
        let sinc = if k == 0 { 1.0 } else { unsafe { hlsl_sin(x) / x } };
        let win = 0.54 + 0.46 * unsafe { hlsl_cos(PI * k as f32 / (taps + 1) as f32) }; // Hamming
        let h = sinc * win;
        acc += h * source(id - k);
        norm += h;
        k += 1;
    }
    pcm(acc / norm) // unity gain at DC
}
