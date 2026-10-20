// Mandelbrot deep zoom in f64: about 10^9 times deeper than f32 allows before the image turns into blocks. Build:
//   RUSTFLAGS="-C link-arg=-zstack-size=1024 -C target-cpu=mvp -C target-feature=+bulk-memory,+sign-ext,+nontrapping-fptoint" \
//   cargo build --release --target wasm32-unknown-unknown
#![no_std]

#[panic_handler]
fn panic(_: &core::panic::PanicInfo) -> ! { loop {} }

#[link(wasm_import_module = "env")]
extern "C" {
    fn hlsl_exp2(x: f32) -> f32;
    fn hlsl_log2(x: f32) -> f32;
}

/// Pixel `id` of a `width`-wide square view of the Mandelbrot set, in f64.
/// The center arrives as two floats per axis (`cx_hi + cx_lo`): Udon splits a double so no precision is lost on the way in.
/// The view is 3 * 2^-`zoom` wide. Returns iterations * 256 + a smooth fraction (0..255), or -1 inside the set.
/// @size 256x256
#[no_mangle]
pub extern "C" fn zoom(id: i32, width: i32, max_iter: i32, cx_hi: f32, cx_lo: f32, cy_hi: f32, cy_lo: f32, zoom: f32) -> i32 {
    let w = if width > 0 { width } else { 1 };
    let step = 3.0 * unsafe { hlsl_exp2(-zoom) } as f64 / w as f64;
    let cx = cx_hi as f64 + cx_lo as f64 + ((id % w) as f64 - w as f64 * 0.5) * step;
    let cy = cy_hi as f64 + cy_lo as f64 + ((id / w) as f64 - w as f64 * 0.5) * step;
    let (mut zx, mut zy) = (0.0f64, 0.0f64);
    let mut i = 0;
    while i < max_iter {
        let (xx, yy) = (zx * zx, zy * zy);
        if xx + yy > 256.0 { break; } // a large bailout makes the smooth fraction accurate
        zy = (zx + zx) * zy + cy;
        zx = xx - yy + cx;
        i += 1;
    }
    if i >= max_iter { return -1; }
    // Smooth (continuous) iteration count; f32 is plenty for coloring.
    let r2 = (zx * zx + zy * zy) as f32;
    let nu = unsafe { hlsl_log2(hlsl_log2(r2) * 0.5) };
    let frac = 1.0 - nu;
    let f = if frac < 0.0 { 0.0 } else if frac > 0.999 { 0.999 } else { frac };
    i * 256 + (f * 256.0) as i32
}
