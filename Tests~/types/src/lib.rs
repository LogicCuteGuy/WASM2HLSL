// Conformance kernel for f64 (software float) and v128 (SIMD + relaxed SIMD).
// Every export is (id) -> i64; types.sh runs each on the GPU and compares all results bit-for-bit with Node.
#![no_std]
#![feature(core_float_math)]
use core::arch::wasm32::*;

#[panic_handler]
fn panic(_: &core::panic::PanicInfo) -> ! { loop {} }

fn mix(x: u64) -> u64 {
    let mut z = x.wrapping_add(0x9E3779B97F4A7C15);
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58476D1CE4E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D049BB133111EB);
    z ^ (z >> 31)
}

static SPECIAL: [u64; 16] = [
    0, 0x8000000000000000, 0x7FF0000000000000, 0xFFF0000000000000, 0x7FF8000000000000, 0x3FF0000000000000,
    0xBFF0000000000000, 0x0010000000000000, 1, 0x7FEFFFFFFFFFFFFF, 0x3FE0000000000000, 0x4004000000000000,
    0xC004000000000000, 0x3FF8000000000000, 0x4330000000000001, 0x000FFFFFFFFFFFFF,
];

/// Random doubles biased toward the hard cases: specials, subnormals, huge/tiny exponents, near-equal pairs.
fn rf(seed: u64) -> f64 {
    let h = mix(seed);
    let s = h & 0x8000000000000000;
    let frac = mix(h) & 0x000FFFFFFFFFFFFF;
    let bits = match h & 7 {
        0 => SPECIAL[((h >> 8) & 15) as usize],
        1 => s | frac,                                   // subnormal
        2 => s | (((h >> 8) % 2046 + 1) << 52) | frac,   // any exponent
        3 => s | ((((h >> 8) % 120) + 1960) << 52) | frac, // near overflow
        4 => s | ((((h >> 8) % 120) + 1) << 52) | frac,  // near underflow
        _ => s | ((((h >> 8) % 80) + 983) << 52) | frac, // around 1
    };
    f64::from_bits(bits)
}

fn pair(id: i32) -> (f64, f64) {
    let a = rf(id as u64 * 2 + 1);
    let h = mix(id as u64 ^ 0xABCDEF);
    let b = if h & 3 == 0 { f64::from_bits(a.to_bits() ^ (h >> 8 & 0xFF)) } // near-equal: cancellation
            else if h & 3 == 1 { -f64::from_bits(a.to_bits() ^ (h >> 8 & 0xFFFF)) }
            else { rf(id as u64 * 2 + 2) };
    (a, b)
}

fn bits(x: f64) -> i64 { if x != x { 0x7FF8000000000000 } else { x.to_bits() as i64 } }
fn bits32(x: f32) -> i64 { if x != x { 0x7FC00000 } else { x.to_bits() as i64 } }

/// Scalar f64 arithmetic, exact to the bit.
#[no_mangle]
pub extern "C" fn f64_arith(id: i32) -> i64 {
    let (a, b) = pair(id / 8);
    match id & 7 {
        0 => bits(a + b),
        1 => bits(a - b),
        2 => bits(a * b),
        3 => bits(a / b),
        4 => bits(core::f64::math::sqrt(a)),
        5 => bits(a.min(b)) ^ bits(a.max(b)).rotate_left(1),
        6 => ((a == b) as i64) | ((a != b) as i64) << 1 | ((a < b) as i64) << 2 | ((a <= b) as i64) << 3
             | ((a > b) as i64) << 4 | ((a >= b) as i64) << 5,
        _ => bits(a.copysign(b)) ^ bits(-a).rotate_left(3) ^ bits(f64::from_bits(a.to_bits() & !(1 << 63))).rotate_left(7),
    }
}

/// f64 rounding and conversions.
#[no_mangle]
pub extern "C" fn f64_conv(id: i32) -> i64 {
    let (a, b) = pair(id / 8);
    let n = mix(id as u64 + 77);
    let small = f64::from_bits((a.to_bits() & 0x800FFFFFFFFFFFFF) | ((1023 + (n % 70)) << 52)); // |x| in [1, 2^70)
    match id & 7 {
        0 => bits(core::f64::math::ceil(a)) ^ bits(core::f64::math::ceil(small)).rotate_left(13),
        1 => bits(core::f64::math::floor(a)) ^ bits(core::f64::math::floor(small)).rotate_left(13),
        2 => bits(core::f64::math::trunc(a)) ^ bits(core::f64::math::trunc(small)).rotate_left(13),
        3 => bits(core::f64::math::round_ties_even(a)) ^ bits(core::f64::math::round_ties_even(small)).rotate_left(13) ^ bits(core::f64::math::round_ties_even(b * 0.5)).rotate_left(29),
        4 => (small as i32) as i64 ^ ((small as u32) as i64) << 32 ^ (a as i64).rotate_left(5) ^ (b as u64 as i64).rotate_left(9),
        5 => (small as i64) ^ (small as u64 as i64).rotate_left(11),
        6 => bits(n as i64 as f64) ^ bits(n as f64).rotate_left(7) ^ bits((n as i32) as f64).rotate_left(17) ^ bits((n as u32) as f64).rotate_left(27),
        _ => { let f = a as f32; bits32(f) ^ bits(f as f64).rotate_left(32) ^ bits32(b as f32).rotate_left(11) }
    }
}

fn rv(seed: u64) -> v128 {
    let lo = mix(seed);
    let hi = mix(seed ^ 0x5555);
    // Some lanes get extreme values so saturation and sign paths trigger.
    let pick = mix(seed ^ 0x77);
    let lo = if pick & 3 == 0 { lo | 0x8080808080808080 } else if pick & 3 == 1 { lo & 0x7F7F7F7F7F7F7F7F } else { lo };
    u64x2(lo, hi)
}

fn fold(v: v128) -> i64 { (u64x2_extract_lane::<0>(v) ^ u64x2_extract_lane::<1>(v).rotate_left(29)) as i64 }

/// i8x16 ops, in two exports to keep each shader quick to compile.
#[no_mangle]
pub extern "C" fn simd_i8_a_0(id: i32) -> i64 {
    let a = rv(id as u64 * 3); let b = rv(id as u64 * 3 + 1);
    let s = (mix(id as u64) % 19) as u32;
    let r = match id % 6 { 0 => i8x16_add(a, b), 1 => i8x16_add_sat(a, b), 2 => u8x16_add_sat(a, b), 3 => i8x16_sub(a, b), 4 => i8x16_sub_sat(a, b), _ => u8x16_sub_sat(a, b) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_i8_a_1(id: i32) -> i64 {
    let a = rv(id as u64 * 3); let b = rv(id as u64 * 3 + 1);
    let s = (mix(id as u64) % 19) as u32;
    let r = match id % 6 { 0 => i8x16_min(a, b), 1 => u8x16_min(a, b), 2 => i8x16_max(a, b), 3 => u8x16_max(a, b), 4 => u8x16_avgr(a, b), _ => i8x16_abs(a) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_i8_a_2(id: i32) -> i64 {
    let a = rv(id as u64 * 3); let b = rv(id as u64 * 3 + 1);
    let s = (mix(id as u64) % 19) as u32;
    let r = match id % 5 { 0 => i8x16_neg(a), 1 => i8x16_popcnt(a), 2 => i8x16_eq(a, i8x16_replace_lane::<3>(a, 7)), 3 => i8x16_ne(a, b), _ => i8x16_lt(a, b) };
    fold(r)
}


#[no_mangle]
pub extern "C" fn simd_i8_b_0(id: i32) -> i64 {
    let a = rv(id as u64 * 3); let b = rv(id as u64 * 3 + 1);
    let s = (mix(id as u64) % 19) as u32;
    let r = match id % 6 { 0 => u8x16_lt(a, b), 1 => i8x16_gt(a, b), 2 => u8x16_gt(a, b), 3 => i8x16_le(a, b), 4 => u8x16_le(a, b), _ => i8x16_ge(a, b) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_i8_b_1(id: i32) -> i64 {
    let a = rv(id as u64 * 3); let b = rv(id as u64 * 3 + 1);
    let s = (mix(id as u64) % 19) as u32;
    let r = match id % 6 { 0 => u8x16_ge(a, b), 1 => i8x16_shl(a, s), 2 => i8x16_shr(a, s), 3 => u8x16_shr(a, s), 4 => i8x16_narrow_i16x8(a, b), _ => u8x16_narrow_i16x8(a, b) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_i8_b_2(id: i32) -> i64 {
    let a = rv(id as u64 * 3); let b = rv(id as u64 * 3 + 1);
    let s = (mix(id as u64) % 19) as u32;
    let r = match id % 5 { 0 => i8x16_swizzle(a, v128_and(b, u8x16_splat(0x1F))), 1 => i8x16_shuffle::<0, 17, 2, 19, 31, 5, 22, 7, 8, 25, 10, 27, 12, 13, 30, 16>(a, b), 2 => u64x2(i8x16_bitmask(a) as u64 | (i8x16_all_true(a) as u64) << 20 | (i8x16_all_true(u8x16_splat(s as u8 + 1)) as u64) << 21
                    | (v128_any_true(a) as u64) << 22 | (v128_any_true(u8x16_splat(0)) as u64) << 23,
                    i8x16_extract_lane::<5>(a) as i64 as u64 ^ (u8x16_extract_lane::<11>(b) as u64) << 40), 3 => i8x16_splat(s as i8 * 13), _ => i8x16_replace_lane::<14>(a, s as i8) };
    fold(r)
}


#[no_mangle]
pub extern "C" fn simd_i16_0(id: i32) -> i64 {
    let a = rv(id as u64 * 5); let b = rv(id as u64 * 5 + 1);
    let s = (mix(id as u64) % 35) as u32;
    let r = match id % 6 { 0 => i16x8_add(a, b), 1 => i16x8_add_sat(a, b), 2 => u16x8_add_sat(a, b), 3 => i16x8_sub(a, b), 4 => i16x8_sub_sat(a, b), _ => u16x8_sub_sat(a, b) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_i16_1(id: i32) -> i64 {
    let a = rv(id as u64 * 5); let b = rv(id as u64 * 5 + 1);
    let s = (mix(id as u64) % 35) as u32;
    let r = match id % 6 { 0 => i16x8_mul(a, b), 1 => i16x8_min(a, b), 2 => u16x8_min(a, b), 3 => i16x8_max(a, b), 4 => u16x8_max(a, b), _ => u16x8_avgr(a, b) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_i16_2(id: i32) -> i64 {
    let a = rv(id as u64 * 5); let b = rv(id as u64 * 5 + 1);
    let s = (mix(id as u64) % 35) as u32;
    let r = match id % 6 { 0 => i16x8_abs(a), 1 => i16x8_neg(a), 2 => i16x8_q15mulr_sat(a, b), 3 => i16x8_eq(a, i16x8_replace_lane::<2>(a, 9)), 4 => i16x8_ne(a, b), _ => i16x8_lt(a, b) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_i16_3(id: i32) -> i64 {
    let a = rv(id as u64 * 5); let b = rv(id as u64 * 5 + 1);
    let s = (mix(id as u64) % 35) as u32;
    let r = match id % 6 { 0 => u16x8_lt(a, b), 1 => i16x8_gt(a, b), 2 => u16x8_ge(a, b), 3 => i16x8_le(a, b), 4 => i16x8_shl(a, s), _ => i16x8_shr(a, s) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_i16_4(id: i32) -> i64 {
    let a = rv(id as u64 * 5); let b = rv(id as u64 * 5 + 1);
    let s = (mix(id as u64) % 35) as u32;
    let r = match id % 6 { 0 => u16x8_shr(a, s), 1 => i16x8_narrow_i32x4(a, b), 2 => u16x8_narrow_i32x4(a, b), 3 => i16x8_extend_low_i8x16(a), 4 => i16x8_extend_high_i8x16(a), _ => u16x8_extend_low_u8x16(a) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_i16_5(id: i32) -> i64 {
    let a = rv(id as u64 * 5); let b = rv(id as u64 * 5 + 1);
    let s = (mix(id as u64) % 35) as u32;
    let r = match id % 6 { 0 => u16x8_extend_high_u8x16(a), 1 => i16x8_extmul_low_i8x16(a, b), 2 => i16x8_extmul_high_i8x16(a, b), 3 => u16x8_extmul_low_u8x16(a, b), 4 => u16x8_extmul_high_u8x16(a, b), _ => i16x8_extadd_pairwise_i8x16(a) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_i16_6(id: i32) -> i64 {
    let a = rv(id as u64 * 5); let b = rv(id as u64 * 5 + 1);
    let s = (mix(id as u64) % 35) as u32;
    let r = match id % 4 { 0 => u16x8_extadd_pairwise_u8x16(a), 1 => u64x2(i16x8_bitmask(a) as u64 | (i16x8_all_true(a) as u64) << 20 | (i16x8_all_true(u16x8_splat(s as u16 + 1)) as u64) << 21,
                    i16x8_extract_lane::<3>(a) as i64 as u64 ^ (u16x8_extract_lane::<6>(b) as u64) << 30), 2 => i16x8_splat(s as i16 * 1000 - 9000), _ => i16x8_replace_lane::<7>(a, s as i16 - 17) };
    fold(r)
}


#[no_mangle]
pub extern "C" fn simd_i32_0(id: i32) -> i64 {
    let a = rv(id as u64 * 7); let b = rv(id as u64 * 7 + 1);
    let s = (mix(id as u64) % 67) as u32;
    let r = match id % 6 { 0 => i32x4_add(a, b), 1 => i32x4_sub(a, b), 2 => i32x4_mul(a, b), 3 => i32x4_min(a, b), 4 => u32x4_min(a, b), _ => i32x4_max(a, b) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_i32_1(id: i32) -> i64 {
    let a = rv(id as u64 * 7); let b = rv(id as u64 * 7 + 1);
    let s = (mix(id as u64) % 67) as u32;
    let r = match id % 6 { 0 => u32x4_max(a, b), 1 => i32x4_abs(a), 2 => i32x4_neg(a), 3 => i32x4_eq(a, i32x4_replace_lane::<1>(a, 5)), 4 => i32x4_ne(a, b), _ => i32x4_lt(a, b) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_i32_2(id: i32) -> i64 {
    let a = rv(id as u64 * 7); let b = rv(id as u64 * 7 + 1);
    let s = (mix(id as u64) % 67) as u32;
    let r = match id % 6 { 0 => u32x4_lt(a, b), 1 => i32x4_gt(a, b), 2 => u32x4_gt(a, b), 3 => i32x4_le(a, b), 4 => u32x4_ge(a, b), _ => i32x4_shl(a, s) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_i32_3(id: i32) -> i64 {
    let a = rv(id as u64 * 7); let b = rv(id as u64 * 7 + 1);
    let s = (mix(id as u64) % 67) as u32;
    let r = match id % 6 { 0 => i32x4_shr(a, s), 1 => u32x4_shr(a, s), 2 => i32x4_extend_low_i16x8(a), 3 => i32x4_extend_high_i16x8(a), 4 => u32x4_extend_low_u16x8(a), _ => u32x4_extend_high_u16x8(a) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_i32_4(id: i32) -> i64 {
    let a = rv(id as u64 * 7); let b = rv(id as u64 * 7 + 1);
    let s = (mix(id as u64) % 67) as u32;
    let r = match id % 6 { 0 => i32x4_extmul_low_i16x8(a, b), 1 => i32x4_extmul_high_i16x8(a, b), 2 => u32x4_extmul_low_u16x8(a, b), 3 => u32x4_extmul_high_u16x8(a, b), 4 => i32x4_dot_i16x8(a, b), _ => i32x4_extadd_pairwise_i16x8(a) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_i32_5(id: i32) -> i64 {
    let a = rv(id as u64 * 7); let b = rv(id as u64 * 7 + 1);
    let s = (mix(id as u64) % 67) as u32;
    let r = match id % 6 { 0 => u32x4_extadd_pairwise_u16x8(a), 1 => u64x2(i32x4_bitmask(a) as u64 | (i32x4_all_true(a) as u64) << 20 | (i32x4_all_true(u32x4_splat(s + 1)) as u64) << 21,
                    i32x4_extract_lane::<2>(a) as u64), 2 => i32x4_splat(s as i32 * -77777), 3 => v128_bitselect(a, b, rv(id as u64 * 7 + 2)), 4 => v128_xor(v128_andnot(a, b), v128_or(v128_not(a), v128_and(a, b))), _ => i32x4_replace_lane::<3>(a, s as i32) };
    fold(r)
}


#[no_mangle]
pub extern "C" fn simd_i64_0(id: i32) -> i64 {
    let a = rv(id as u64 * 11); let b = rv(id as u64 * 11 + 1);
    let s = (mix(id as u64) % 131) as u32;
    let r = match id % 6 { 0 => i64x2_add(a, b), 1 => i64x2_sub(a, b), 2 => i64x2_mul(a, b), 3 => i64x2_abs(a), 4 => i64x2_neg(a), _ => i64x2_eq(a, i64x2_replace_lane::<0>(a, 3)) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_i64_1(id: i32) -> i64 {
    let a = rv(id as u64 * 11); let b = rv(id as u64 * 11 + 1);
    let s = (mix(id as u64) % 131) as u32;
    let r = match id % 6 { 0 => i64x2_ne(a, b), 1 => i64x2_lt(a, b), 2 => i64x2_gt(a, b), 3 => i64x2_le(a, b), 4 => i64x2_ge(a, b), _ => i64x2_shl(a, s) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_i64_2(id: i32) -> i64 {
    let a = rv(id as u64 * 11); let b = rv(id as u64 * 11 + 1);
    let s = (mix(id as u64) % 131) as u32;
    let r = match id % 6 { 0 => i64x2_shr(a, s), 1 => u64x2_shr(a, s), 2 => i64x2_extend_low_i32x4(a), 3 => i64x2_extend_high_i32x4(a), 4 => u64x2_extend_low_u32x4(a), _ => u64x2_extend_high_u32x4(a) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_i64_3(id: i32) -> i64 {
    let a = rv(id as u64 * 11); let b = rv(id as u64 * 11 + 1);
    let s = (mix(id as u64) % 131) as u32;
    let r = match id % 6 { 0 => i64x2_extmul_low_i32x4(a, b), 1 => i64x2_extmul_high_i32x4(a, b), 2 => u64x2_extmul_low_u32x4(a, b), 3 => u64x2_extmul_high_u32x4(a, b), 4 => u64x2(i64x2_bitmask(a) as u64 | (i64x2_all_true(a) as u64) << 20 | (i64x2_all_true(u64x2_splat(s as u64)) as u64) << 21, 0), _ => i64x2_splat(s as i64 * -0x123456789) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_i64_4(id: i32) -> i64 {
    let a = rv(id as u64 * 11); let b = rv(id as u64 * 11 + 1);
    let s = (mix(id as u64) % 131) as u32;
    let r = match id % 2 { 0 => u64x2_splat(i64x2_extract_lane::<1>(a) as u64), _ => i64x2_replace_lane::<1>(a, s as i64) };
    fold(r)
}


/// f64x2 lanes go through the same software float as scalar f64: exact. Split in three: fxc compile time grows faster than linearly with kernel size.
#[no_mangle]
pub extern "C" fn simd_f64_arith_0(id: i32) -> i64 {
    let (x, y) = pair(id / 4);
    let (z, w) = pair(id / 4 + 100000);
    let a = f64x2(x, z); let b = f64x2(y, w);
    let canon = |v: v128| v128_bitselect(f64x2_splat(f64::NAN), v, f64x2_ne(v, v));
    let r = match id % 6 { 0 => f64x2_add(a, b), 1 => f64x2_sub(a, b), 2 => f64x2_mul(a, b), 3 => f64x2_div(a, b), 4 => f64x2_sqrt(a), _ => f64x2_min(a, b) };
    fold(canon(r))
}

#[no_mangle]
pub extern "C" fn simd_f64_arith_1(id: i32) -> i64 {
    let (x, y) = pair(id / 4);
    let (z, w) = pair(id / 4 + 100000);
    let a = f64x2(x, z); let b = f64x2(y, w);
    let canon = |v: v128| v128_bitselect(f64x2_splat(f64::NAN), v, f64x2_ne(v, v));
    let r = match id % 3 { 0 => f64x2_max(a, b), 1 => f64x2_pmin(a, b), _ => f64x2_pmax(a, b) };
    fold(canon(r))
}


#[no_mangle]
pub extern "C" fn simd_f64_round_0(id: i32) -> i64 {
    let (x, y) = pair(id / 4);
    let (z, w) = pair(id / 4 + 100000);
    let a = f64x2(x, z); let b = f64x2(y, w);
    let canon = |v: v128| v128_bitselect(f64x2_splat(f64::NAN), v, f64x2_ne(v, v));
    let r = match id % 6 { 0 => f64x2_abs(a), 1 => f64x2_neg(a), 2 => f64x2_ceil(a), 3 => f64x2_floor(a), 4 => f64x2_trunc(a), _ => f64x2_nearest(a) };
    fold(canon(r))
}

#[no_mangle]
pub extern "C" fn simd_f64_round_1(id: i32) -> i64 {
    let (x, y) = pair(id / 4);
    let (z, w) = pair(id / 4 + 100000);
    let a = f64x2(x, z); let b = f64x2(y, w);
    let canon = |v: v128| v128_bitselect(f64x2_splat(f64::NAN), v, f64x2_ne(v, v));
    let r = match id % 2 { 0 => v128_or(v128_or(f64x2_eq(a, b), v128_and(f64x2_ne(a, b), u64x2_splat(2))), v128_and(f64x2_lt(a, b), u64x2_splat(4))), _ => v128_or(v128_or(f64x2_gt(a, b), v128_and(f64x2_le(a, b), u64x2_splat(2))), v128_and(f64x2_ge(a, b), u64x2_splat(4))) };
    fold(canon(r))
}


#[no_mangle]
pub extern "C" fn simd_f64_conv_0(id: i32) -> i64 {
    let (x, y) = pair(id / 4);
    let (z, w) = pair(id / 4 + 100000);
    let a = f64x2(x, z); let b = f64x2(y, w);
    let canon = |v: v128| v128_bitselect(f64x2_splat(f64::NAN), v, f64x2_ne(v, v));
    let r = match id % 6 { 0 => f64x2_convert_low_i32x4(rv(id as u64)), 1 => f64x2_convert_low_u32x4(rv(id as u64)), 2 => f64x2_promote_low_f32x4(f32x4(x as f32, y as f32, 0.0, 0.0)), 3 => f32x4_demote_f64x2_zero(a), 4 => i32x4_trunc_sat_f64x2_zero(a), _ => u32x4_trunc_sat_f64x2_zero(b) };
    fold(canon(r))
}

#[no_mangle]
pub extern "C" fn simd_f64_conv_1(id: i32) -> i64 {
    let (x, y) = pair(id / 4);
    let (z, w) = pair(id / 4 + 100000);
    let a = f64x2(x, z); let b = f64x2(y, w);
    let canon = |v: v128| v128_bitselect(f64x2_splat(f64::NAN), v, f64x2_ne(v, v));
    let r = match id % 1 { _ => f64x2_replace_lane::<1>(f64x2_splat(f64x2_extract_lane::<0>(a)), y) };
    fold(canon(r))
}


/// f32x4 with values the GPU computes exactly (normal range, no div/sqrt): bit-exact too.
#[no_mangle]
pub extern "C" fn simd_f32_0(id: i32) -> i64 {
    let h = mix(id as u64);
    let f = |k: u64| { let m = mix(h ^ k); f32::from_bits(((m & 1) << 31) as u32 | (((m >> 8) % 40 + 107) << 23) as u32 | (m >> 32) as u32 & 0x7FFFFF) };
    let a = f32x4(f(1), f(2), f(3), f(4));
    let b = f32x4(f(5), f(2), f(7), -f(8));
    let ints = rv(id as u64);
    let r = match id % 6 { 0 => f32x4_add(a, b), 1 => f32x4_sub(a, b), 2 => f32x4_mul(a, b), 3 => f32x4_min(a, b), 4 => f32x4_max(a, b), _ => f32x4_pmin(a, b) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_f32_1(id: i32) -> i64 {
    let h = mix(id as u64);
    let f = |k: u64| { let m = mix(h ^ k); f32::from_bits(((m & 1) << 31) as u32 | (((m >> 8) % 40 + 107) << 23) as u32 | (m >> 32) as u32 & 0x7FFFFF) };
    let a = f32x4(f(1), f(2), f(3), f(4));
    let b = f32x4(f(5), f(2), f(7), -f(8));
    let ints = rv(id as u64);
    let r = match id % 6 { 0 => f32x4_pmax(a, b), 1 => f32x4_abs(a), 2 => f32x4_neg(a), 3 => f32x4_ceil(a), 4 => f32x4_floor(a), _ => f32x4_trunc(a) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_f32_2(id: i32) -> i64 {
    let h = mix(id as u64);
    let f = |k: u64| { let m = mix(h ^ k); f32::from_bits(((m & 1) << 31) as u32 | (((m >> 8) % 40 + 107) << 23) as u32 | (m >> 32) as u32 & 0x7FFFFF) };
    let a = f32x4(f(1), f(2), f(3), f(4));
    let b = f32x4(f(5), f(2), f(7), -f(8));
    let ints = rv(id as u64);
    let r = match id % 6 { 0 => f32x4_nearest(a), 1 => v128_or(v128_or(f32x4_eq(a, b), v128_and(f32x4_ne(a, b), u32x4_splat(2))), v128_and(f32x4_lt(a, b), u32x4_splat(4))), 2 => v128_or(v128_or(f32x4_gt(a, b), v128_and(f32x4_le(a, b), u32x4_splat(2))), v128_and(f32x4_ge(a, b), u32x4_splat(4))), 3 => f32x4_convert_i32x4(i32x4_shr(ints, 8)), 4 => f32x4_convert_u32x4(u32x4_shr(ints, 8)), _ => i32x4_trunc_sat_f32x4(f32x4_mul(a, f32x4_splat(1e9))) };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_f32_3(id: i32) -> i64 {
    let h = mix(id as u64);
    let f = |k: u64| { let m = mix(h ^ k); f32::from_bits(((m & 1) << 31) as u32 | (((m >> 8) % 40 + 107) << 23) as u32 | (m >> 32) as u32 & 0x7FFFFF) };
    let a = f32x4(f(1), f(2), f(3), f(4));
    let b = f32x4(f(5), f(2), f(7), -f(8));
    let ints = rv(id as u64);
    let r = match id % 2 { 0 => u32x4_trunc_sat_f32x4(f32x4_mul(a, f32x4_splat(1e9))), _ => f32x4_replace_lane::<2>(f32x4_splat(f32x4_extract_lane::<1>(a)), f(9)) };
    fold(r)
}


/// Loads/stores through linear memory and the relaxed-SIMD instructions whose results don't depend on the choice.
#[no_mangle]
pub extern "C" fn simd_mem_0(id: i32) -> i64 {
    let mut buf = [0u8; 48];
    for i in 0..6 { buf[i * 8..i * 8 + 8].copy_from_slice(&mix(id as u64 * 13 + i as u64).to_le_bytes()); }
    let o = (id as usize / 16) % 24;
    let p = buf[o..].as_ptr();
    let a = rv(id as u64 * 17); let b = rv(id as u64 * 17 + 1);
    let r = unsafe {
        match id % 6 { 0 => v128_load(p as *const v128), 1 => i16x8_load_extend_i8x8(p as *const i8), 2 => u16x8_load_extend_u8x8(p), 3 => i32x4_load_extend_i16x4(p as *const i16), 4 => u32x4_load_extend_u16x4(p as *const u16), _ => i64x2_load_extend_i32x2(p as *const i32) }
    };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_mem_1(id: i32) -> i64 {
    let mut buf = [0u8; 48];
    for i in 0..6 { buf[i * 8..i * 8 + 8].copy_from_slice(&mix(id as u64 * 13 + i as u64).to_le_bytes()); }
    let o = (id as usize / 16) % 24;
    let p = buf[o..].as_ptr();
    let a = rv(id as u64 * 17); let b = rv(id as u64 * 17 + 1);
    let r = unsafe {
        match id % 6 { 0 => u64x2_load_extend_u32x2(p as *const u32), 1 => v128_or(v128_load8_splat(p), v128_and(v128_load16_splat(p as *const u16), u32x4_splat(0xF0F0))), 2 => v128_xor(v128_load32_splat(p as *const u32), v128_load64_splat(p.add(3) as *const u64)), 3 => v128_xor(v128_load32_zero(p as *const u32), v128_load64_zero(p.add(5) as *const u64)), 4 => v128_load16_lane::<3>(v128_load8_lane::<9>(a, p), p.add(1) as *const u16), _ => v128_load64_lane::<1>(v128_load32_lane::<2>(a, p as *const u32), p.add(2) as *const u64) }
    };
    fold(r)
}

#[no_mangle]
pub extern "C" fn simd_mem_2(id: i32) -> i64 {
    let mut buf = [0u8; 48];
    for i in 0..6 { buf[i * 8..i * 8 + 8].copy_from_slice(&mix(id as u64 * 13 + i as u64).to_le_bytes()); }
    let o = (id as usize / 16) % 24;
    let p = buf[o..].as_ptr();
    let a = rv(id as u64 * 17); let b = rv(id as u64 * 17 + 1);
    let r = unsafe {
        match id % 4 { 0 => {
                let q = buf.as_mut_ptr().add(o);
                v128_store8_lane::<13>(a, q); v128_store16_lane::<5>(a, q.add(3) as *mut u16);
                v128_store32_lane::<1>(b, q.add(7) as *mut u32); v128_store64_lane::<0>(b, q.add(11) as *mut u64);
                v128_store(q.add(19) as *mut v128, i32x4_add(a, b));
                v128_load(buf.as_ptr().add(o / 2) as *const v128)
            }, 1 => i8x16_relaxed_swizzle(a, v128_and(b, u8x16_splat(0x0F))), 2 => i8x16_relaxed_laneselect(a, b, i8x16_shr(rv(id as u64), 7)), _ => i32x4_relaxed_dot_i8x16_i7x16_add(a, v128_and(b, u8x16_splat(0x3F)), i16x8_relaxed_dot_i8x16_i7x16(a, v128_and(b, u8x16_splat(0x3F)))) }
    };
    fold(r)
}

