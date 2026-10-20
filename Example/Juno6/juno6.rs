// Juno-6 style voice on the GPU: one pixel = one stereo sample of one key.
// Pixel id -> (key 0..11, frame, channel): id = (key * FRAMES + frame) * 2 + ch, so each key is one
// contiguous interleaved-stereo block that Udon splits into a head clip (plays once) and a loop clip (sustain).
// Exports: `main` (the audio) and `env_curve` (a small ADSR picture for the panel).
// Signal path like the Juno: DCO (saw / PWM pulse / sub / noise) -> HPF -> 4-pole resonant VCF -> VCA -> chorus.
//
// The HPF and VCF are recursive (IIR). A pixel cannot see its neighbours, so each pixel re-runs the
// filters over the previous K samples starting from zero state: a truncated, but stateless, IIR.
// The chorus needs the voice ~2-5 ms earlier; the same warm-up loop records those samples on its way.
//
// Build:
//   RUSTFLAGS="-C link-arg=-zstack-size=1024 -C target-cpu=mvp -C target-feature=+bulk-memory,+sign-ext,+nontrapping-fptoint" \
//   cargo build --release --target wasm32-unknown-unknown
#![no_std]

#[panic_handler]
fn panic(_: &core::panic::PanicInfo) -> ! { loop {} }

#[link(wasm_import_module = "env")]
extern "C" {
    fn hlsl_exp2(x: f32) -> f32;
    fn hlsl_sqrt(x: f32) -> f32;
}

const SR: f32 = 32000.0;
const FRAMES: i32 = 96000; // 3 s per key: HEAD plays once, then [HEAD, FRAMES) loops while the key is held
const HEAD: i32 = 64000;
const LOOP: i32 = FRAMES - HEAD;
const XF: i32 = 8000; // the last 0.25 s crossfades into the audio just before HEAD, so the loop has no seam
const SETTLE0: f32 = 1.25; // seconds: the envelope is eased onto the sustain level here...
const SETTLE1: f32 = (HEAD - XF) as f32 / SR; // ...and pinned there from here on, so the loop has a steady level
const KEYS: i32 = 12; // C..B; Udon reaches other octaves with AudioSource.pitch
const K: i32 = 256; // filter warm-up samples
const BASE_NOTE: i32 = 48; // C3 at the 8' range
const LN2: f32 = 0.693147;
const TWO_PI_LOG2E: f32 = 9.064720; // 2*pi*log2(e): 1 - exp(-w) = 1 - exp2(-w * log2e)

fn exp2(x: f32) -> f32 { unsafe { hlsl_exp2(x) } }
fn abs(x: f32) -> f32 { if x < 0.0 { -x } else { x } }
fn clamp(x: f32, lo: f32, hi: f32) -> f32 { if x < lo { lo } else if x > hi { hi } else { x } }
fn floor(x: f32) -> f32 { let i = x as i32 as f32; if i > x { i - 1.0 } else { i } }
fn frac(x: f32) -> f32 { x - floor(x) }

/// Triangle LFO: 0 at p = 0, +1 at 0.25, -1 at 0.75.
fn tri(p: f32) -> f32 { 1.0 - 4.0 * abs(frac(p + 0.25) - 0.5) }

/// Integral of tri from 0 to p (in cycles), for continuous vibrato phase.
fn tri_int(p: f32) -> f32 {
    let u = frac(p + 0.25);
    let g = if u <= 0.5 { 2.0 * u * u - u } else { let v = u - 0.5; v - 2.0 * v * v };
    g + 0.125
}

/// PolyBLEP step correction (t = phase in [0,1), dt = phase increment).
fn blep(t: f32, dt: f32) -> f32 {
    if t < dt { let x = t / dt; x + x - x * x - 1.0 }
    else if t > 1.0 - dt { let x = (t - 1.0) / dt; x * x + x + x + 1.0 }
    else { 0.0 }
}

/// tanh-like soft clip.
fn sat(x: f32) -> f32 {
    if x > 3.0 { 1.0 } else if x < -3.0 { -1.0 } else { x * (27.0 + x * x) / (27.0 + 9.0 * x * x) }
}

/// White noise from the sample index, in [-1, 1).
fn noise(m: i32) -> f32 {
    let mut x = (m as u32).wrapping_mul(747796405).wrapping_add(2891336453);
    x = ((x >> ((x >> 28) + 4)) ^ x).wrapping_mul(277803737);
    x = (x >> 22) ^ x;
    (x >> 8) as i32 as f32 / 8388608.0 - 1.0
}

/// Attack/decay/sustain with the key held (Udon fades the release). Settles on S before the loop.
fn env(t: f32, a: f32, d: f32, s: f32) -> f32 {
    let e = if t < a { t / a } else { s + (1.0 - s) * exp2(-(t - a) / d * 6.64) };
    e + (s - e) * clamp((t - SETTLE0) / (SETTLE1 - SETTLE0), 0.0, 1.0)
}

/// Sliders are 0..1 like the Juno's faders. flags: bit0 saw, bit1 pulse, bit2 PWM manual (else LFO),
/// bit3 VCA gate (else env), bits4-5 chorus off/I/II, bits6-7 range 16'/8'/4'.
/// @size 16384x141
#[no_mangle]
pub extern "C" fn main(
    id: i32, flags: i32,
    lfo_rate: f32, lfo_delay: f32, dco_lfo: f32, pwm: f32, sub: f32, noise_lvl: f32, hpf: f32,
    vcf_freq: f32, res: f32, vcf_env: f32, vcf_lfo: f32, kybd: f32,
    attack: f32, decay: f32, sustain: f32, level: f32,
) -> f32 {
    let ch = id & 1;
    let key = (id >> 1) / FRAMES;
    let n = (id >> 1) % FRAMES;
    if key >= KEYS { return 0.0; }
    let v = |m: i32| voice(m, ch, key, flags, lfo_rate, lfo_delay, dco_lfo, pwm, sub, noise_lvl, hpf,
                           vcf_freq, res, vcf_env, vcf_lfo, kybd, attack, decay, sustain, level);
    if n < FRAMES - XF { return v(n); }
    // Equal-power crossfade towards the audio one loop earlier: the clip end then flows into HEAD.
    let c = (n - (FRAMES - XF)) as f32 / XF as f32;
    unsafe { v(n) * hlsl_sqrt(1.0 - c) + v(n - LOOP) * hlsl_sqrt(c) }
}

#[inline(always)]
fn voice(
    n: i32, ch: i32, key: i32, flags: i32,
    lfo_rate: f32, lfo_delay: f32, dco_lfo: f32, pwm: f32, sub: f32, noise_lvl: f32, hpf: f32,
    vcf_freq: f32, res: f32, vcf_env: f32, vcf_lfo: f32, kybd: f32,
    attack: f32, decay: f32, sustain: f32, level: f32,
) -> f32 {
    let note = BASE_NOTE + key + 12 * (((flags >> 6) & 3) - 1);
    let f0 = 440.0 * exp2((note - 69) as f32 / 12.0);
    let t = n as f32 / SR;

    // LFO with delayed fade-in.
    let rate = 0.1 * exp2(lfo_rate * 7.6);
    let dly = lfo_delay * 1.2; // fully faded in before the loop starts
    let amp = if dly < 0.001 { 1.0 } else { clamp(t / dly, 0.0, 1.0) };
    let lp = rate * t;
    let lfo = amp * tri(lp);

    // DCO: local frequency for the window, analytic phase so neighbouring pixels agree.
    let depth = dco_lfo * dco_lfo * 0.5; // octaves
    let f = f0 * exp2(depth * lfo);
    let phase_n = f0 * (t + depth * LN2 * amp * tri_int(lp) / rate);
    let dt = f / SR;
    let w = if flags & 4 != 0 { 0.5 - 0.45 * pwm } else { 0.5 - 0.225 * pwm * (1.0 + lfo) };
    let (saw_on, pulse_on) = (flags & 1 != 0, flags & 2 != 0);

    // Envelope -> VCF/VCA.
    let a = 0.001 * exp2(attack * 11.5);
    let d = 0.002 * exp2(decay * 12.5);
    let e = env(t, a, d, sustain);
    let cut = vcf_freq * 10.0 + vcf_env * e * 8.0 + vcf_lfo * lfo * 3.0 + kybd * (note - 60) as f32 / 12.0;
    let fc = clamp(20.0 * exp2(cut), 30.0, SR * 0.25);
    let g = 1.0 - exp2(-fc / SR * TWO_PI_LOG2E);
    let k = res * 3.9;
    let gh = if hpf <= 0.0 { 0.0 } else { 1.0 - exp2(-20.0 * exp2(hpf * 5.6) / SR * TWO_PI_LOG2E) };

    // Chorus I/II: BBD delay swept by a triangle, left and right in antiphase.
    let mode = (flags >> 4) & 3;
    let (mut di, mut df) = (0, 0.0);
    if mode != 0 {
        let crate_ = if mode == 1 { 0.513 } else { 0.863 };
        let sweep = if mode == 1 { 1.5 } else { 1.85 };
        let ct = tri(crate_ * t) * if ch == 0 { 1.0 } else { -1.0 };
        let samples = (3.5 + sweep * ct) * SR / 1000.0;
        di = samples as i32;
        df = samples - di as f32;
    }

    let mut m = n - di - 1 - K;
    if m < 0 { m = 0; }
    let (mut hl, mut s1, mut s2, mut s3, mut s4) = (0.0f32, 0.0f32, 0.0f32, 0.0f32, 0.0f32);
    let (mut y_now, mut y_a, mut y_b) = (0.0f32, 0.0f32, 0.0f32);
    while m <= n {
        let p = frac(phase_n - f * (n - m) as f32 / SR);
        let mut x = 0.0;
        if saw_on { x += 2.0 * p - 1.0 - blep(p, dt); }
        if pulse_on { x += (if p < w { 1.0 } else { -1.0 }) + blep(p, dt) - blep(frac(p - w + 1.0), dt); }
        x += sub * (if frac((phase_n - f * (n - m) as f32 / SR) * 0.5) < 0.5 { 1.0 } else { -1.0 });
        x += noise_lvl * noise(m);
        x *= 0.4;

        hl += gh * (x - hl);
        x -= hl;

        let u = sat(x - k * s4);
        s1 += g * (u - s1);
        s2 += g * (s1 - s2);
        s3 += g * (s2 - s3);
        s4 += g * (s3 - s4);
        let y = s4 * (1.0 + k * 0.4);

        if m == n - di - 1 { y_a = y; }
        if m == n - di { y_b = y; }
        y_now = y;
        m += 1;
    }

    let gate = flags & 8 != 0;
    let vca = |tt: f32| if tt <= 0.0 { 0.0 } else if gate { clamp(tt / 0.002, 0.0, 1.0) } else { env(tt, a, d, sustain) };
    let mut out = y_now * vca(t);
    if mode != 0 {
        let wet = y_b + (y_a - y_b) * df;
        out = 0.6 * (out + wet * vca(t - (di as f32 + df) / SR));
    }
    clamp(out * level * 1.5, -1.0, 1.0)
}

/// Envelope display for the panel: 0 = background, 1 = under the curve, 2 = the curve.
/// x spans the 3 s a key renders, y is 0..1. Same flags/attack/decay/sustain as `main`.
/// @size 256x64
#[no_mangle]
pub extern "C" fn env_curve(id: i32, flags: i32, attack: f32, decay: f32, sustain: f32) -> i32 {
    const W: i32 = 256;
    const H: f32 = 64.0;
    let t = (id % W) as f32 / W as f32 * FRAMES as f32 / SR;
    let y = ((id / W) as f32 + 0.5) / H;
    let e = if flags & 8 != 0 { 1.0 } else {
        env(t, 0.001 * exp2(attack * 11.5), 0.002 * exp2(decay * 12.5), sustain)
    };
    if abs(y - e) < 1.5 / H { 2 } else if y < e { 1 } else { 0 }
}
