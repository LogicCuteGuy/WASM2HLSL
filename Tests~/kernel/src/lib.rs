#![no_std]
#[panic_handler]
fn panic(_: &core::panic::PanicInfo) -> ! { loop {} }

#[link(wasm_import_module = "env")]
extern "C" { fn hlsl_sin(x: f32) -> f32; fn input(i: i32) -> i32; }
static PRIMES: [u32; 8] = [2, 3, 5, 7, 11, 13, 17, 19];

#[inline(never)]
fn collatz(mut n: u32) -> u32 {
    let mut steps = 0;
    while n > 1 { n = if n % 2 == 0 { n / 2 } else { 3 * n + 1 }; steps += 1; if steps > 1000 { break; } }
    steps
}

#[inline(never)]
fn mix64(x: u64) -> u64 {
    let mut z = x.wrapping_add(0x9E3779B97F4A7C15);
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58476D1CE4E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D049BB133111EB);
    z ^ (z >> 31)
}

#[inline(never)]
fn op(kind: u32, a: i32, b: i32) -> i32 {
    match kind {
        0 => a.wrapping_add(b),
        1 => a.wrapping_sub(b),
        2 => a.wrapping_mul(b),
        3 => if b != 0 { a / b } else { 0 },
        4 => if b != 0 { a % b } else { 0 },
        5 => a.rotate_left(b as u32),
        6 => (a as u32).leading_zeros() as i32,
        _ => a ^ b,
    }
}

#[inline(never)]
fn sort_sum(seed: u32) -> u32 {
    let mut arr = [0u32; 16];
    let mut s = seed;
    for i in 0..16 { s = s.wrapping_mul(1664525).wrapping_add(1013904223); arr[i] = s >> 20; }
    // insertion sort
    for i in 1..16 { let mut j = i; while j > 0 && arr[j - 1] > arr[j] { arr.swap(j - 1, j); j -= 1; } }
    let mut acc = 0u32;
    for i in 0..16 { acc = acc.wrapping_mul(31).wrapping_add(arr[i]); }
    acc
}

#[no_mangle]
pub extern "C" fn main(id: i32, mode: i32) -> i32 {
    let x = id as u32;
    match mode {
        0 => collatz(x + 1) as i32,
        1 => { let h = mix64(x as u64 * 0x1234567 + 99); (h as u32 ^ (h >> 32) as u32) as i32 }
        2 => op(x % 8, (x as i32) * 7919 - 50000, (x as i32 % 13) - 6),
        3 => sort_sum(x) as i32,
        4 => { let f = (x as f32) * 0.37 + 0.5; (unsafe { hlsl_sin(f) } * 1000.0 + f * 3.0) as i32 + PRIMES[(x % 8) as usize] as i32 }
        5 => { let a = (x as u64) << 33 | 12345; let b = (x as u64 % 97) + 3; ((a / b) as u32 ^ (a % b) as u32) as i32 }
        6 => { let v = (x as i64 - 500) * 123456789; (v >> 7) as i32 ^ ((v as f64) as f32 as i32 >> 3) }
        _ => (x as i32).wrapping_neg() ^ unsafe { input(id) },
    }
}
