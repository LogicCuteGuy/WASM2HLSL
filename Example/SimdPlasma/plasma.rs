// Animated plasma written with WebAssembly SIMD (v128): four sine terms per pixel in one f32x4, the color in
// another, then packed to RGBA8 with saturating narrows. Build (note +simd128):
//   RUSTFLAGS="-C link-arg=-zstack-size=1024 -C target-cpu=mvp -C target-feature=+bulk-memory,+sign-ext,+nontrapping-fptoint,+simd128" \
//   cargo build --release --target wasm32-unknown-unknown
#![no_std]
use core::arch::wasm32::*;

#[panic_handler]
fn panic(_: &core::panic::PanicInfo) -> ! { loop {} }

/// sin(2π·t) in every lane, error < 0.001: wrap to [-0.5, 0.5), parabola 8t - 16t|t|, one refinement step.
#[inline(always)]
fn sin_turns(t: v128) -> v128 {
    let t = f32x4_sub(t, f32x4_floor(f32x4_add(t, f32x4_splat(0.5))));
    let y = f32x4_mul(f32x4_mul(f32x4_splat(8.0), t), f32x4_sub(f32x4_splat(1.0), f32x4_mul(f32x4_splat(2.0), f32x4_abs(t))));
    f32x4_add(y, f32x4_mul(f32x4_splat(0.225), f32x4_sub(f32x4_mul(y, f32x4_abs(y)), y)))
}

/// Plasma pixel `id` of a `width`-wide image at `time` seconds. `glow` (0..255) brightens with saturating byte adds.
/// Returns packed RGBA8 (r | g << 8 | b << 16 | a << 24).
/// @size 256x256
#[no_mangle]
pub extern "C" fn plasma(id: i32, width: i32, time: f32, glow: i32) -> i32 {
    let w = if width > 0 { width } else { 1 };
    let x = (id % w) as f32 / w as f32 - 0.5;
    let y = (id / w) as f32 / w as f32 - 0.5;
    let r = f32x4_extract_lane::<0>(f32x4_sqrt(f32x4_splat(x * x + y * y)));
    // Four plasma terms side by side, summed across lanes with two shuffles.
    let s = sin_turns(f32x4(x * 1.7 + time * 0.13, y * 1.3 - time * 0.11, (x + y) * 1.1 + time * 0.07, r * 2.5 - time * 0.17));
    let h = f32x4_add(s, i32x4_shuffle::<2, 3, 0, 1>(s, s));
    let h = f32x4_add(h, i32x4_shuffle::<1, 0, 3, 2>(h, h));
    // Cosine palette: R, G, B (and A) in one vector.
    let phase = f32x4_add(f32x4_mul(h, f32x4_splat(0.25)), f32x4_splat(time * 0.05));
    let rgb = f32x4_add(f32x4_splat(0.5), f32x4_mul(f32x4_splat(0.5), sin_turns(f32x4_add(phase, f32x4(0.0, 0.33, 0.67, 0.25)))));
    let rgb = f32x4_replace_lane::<3>(rgb, 1.0);
    // 0..1 floats -> 0..255 ints -> bytes, then a saturating brighten on all 16 byte lanes at once.
    let c = i32x4_trunc_sat_f32x4(f32x4_add(f32x4_mul(rgb, f32x4_splat(255.0)), f32x4_splat(0.5)));
    let c = u8x16_narrow_i16x8(u16x8_narrow_i32x4(c, c), u16x8_narrow_i32x4(c, c));
    let c = u8x16_add_sat(c, u8x16_splat(glow.clamp(0, 255) as u8));
    i32x4_extract_lane::<0>(c)
}
