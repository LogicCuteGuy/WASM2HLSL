// Mandelbrot and Julia sets per pixel: two exports, one .wasm, smooth-coloured through a 64 KB palette. Build:
//   RUSTFLAGS="-C link-arg=-zstack-size=1024 -C target-cpu=mvp -C target-feature=+bulk-memory,+sign-ext,+nontrapping-fptoint" \
//   cargo build --release --target wasm32-unknown-unknown
//
// PALETTE is a plain `static`: it lands in .rodata, which the importer reads from a texture (the asset's "rom"),
// so it doesn't count toward the 64000-byte GPU memory limit. Nothing special is needed in the source.
#![no_std]

#[panic_handler]
fn panic(_: &core::panic::PanicInfo) -> ! { loop {} }

#[link(wasm_import_module = "env")]
extern "C" { fn hlsl_log2(x: f32) -> f32; }

const PAL_N: usize = 16384; // 16384 x 4 bytes = 64 KB
const PAL_PER_ITER: f32 = 512.0; // palette entries per iteration: one cycle every 32 iterations

/// Cyclic gradient through KEYS with smoothstep between them, built at compile time.
const fn make_palette() -> [u32; PAL_N] {
    const KEYS: [[u32; 3]; 6] = [[0, 7, 100], [32, 107, 203], [237, 255, 255], [255, 170, 0], [120, 20, 60], [0, 2, 40]];
    let mut p = [0u32; PAL_N];
    let mut i = 0;
    while i < PAL_N {
        let seg = PAL_N / KEYS.len();
        let (k, f) = (i / seg, (i % seg) as u64); // f / seg in [0, 1)
        let (a, b) = (KEYS[k % KEYS.len()], KEYS[(k + 1) % KEYS.len()]);
        let s = seg as u64;
        let t = f * f * (3 * s - 2 * f); // smoothstep * s^3
        let mut c = 0;
        let mut rgb = 0u32;
        while c < 3 {
            let v = (a[c] as u64 * (s * s * s - t) + b[c] as u64 * t) / (s * s * s);
            rgb |= (v as u32) << (8 * c);
            c += 1;
        }
        p[i] = rgb | 0xFF00_0000;
        i += 1;
    }
    p
}
static PALETTE: [u32; PAL_N] = make_palette();

/// Iterates z = z² + c until |z| ≥ 256 (or `max_iter`), then picks the colour from the fractional escape count.
/// Returns packed RGBA (r in the low byte); points inside the set are black.
#[inline(always)]
fn colour(mut zx: f32, mut zy: f32, cx: f32, cy: f32, max_iter: i32) -> i32 {
    let mut i = 0;
    while i < max_iter && zx * zx + zy * zy < 65536.0 {
        let t = zx * zx - zy * zy + cx;
        zy = 2.0 * zx * zy + cy;
        zx = t;
        i += 1;
    }
    if i >= max_iter { return 0xFF00_0000u32 as i32; }
    // Smooth count: i + 1 - log2(log2|z|), so neighbouring pixels blend instead of banding.
    let nu = i as f32 + 1.0 - unsafe { hlsl_log2(0.5 * hlsl_log2(zx * zx + zy * zy)) };
    let k = (nu * PAL_PER_ITER) as i32;
    PALETTE[(k as usize) % PAL_N] as i32
}

/// Pixel `id` of a `width`-wide square image -> point in [-1.5, 1.5]², shifted left by `shift`.
#[inline(always)]
fn point(id: i32, width: i32, shift: f32) -> (f32, f32) {
    let w = if width > 0 { width } else { 1 };
    let (x, y) = (id % w, id / w);
    ((x as f32 / w as f32) * 3.0 - 1.5 - shift, (y as f32 / w as f32) * 3.0 - 1.5)
}

/// Mandelbrot set as packed RGBA. `id` = pixel index (filled in by the shader), `width` = image width, `max_iter` = iteration cap.
/// @size 256x256
#[no_mangle]
pub extern "C" fn main(id: i32, width: i32, max_iter: i32) -> i32 {
    let (cx, cy) = point(id, width, 0.75);
    colour(0.0, 0.0, cx, cy, max_iter)
}

/// Julia set for the constant c = (`cr`, `ci`), as packed RGBA.
/// @size 256x256
#[no_mangle]
pub extern "C" fn julia(id: i32, width: i32, max_iter: i32, cr: f32, ci: f32) -> i32 {
    let (zx, zy) = point(id, width, 0.0);
    colour(zx, zy, cr, ci, max_iter)
}
