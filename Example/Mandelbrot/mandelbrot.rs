// Mandelbrot and Julia iteration counts per pixel: two exports, one .wasm. Build:
//   RUSTFLAGS="-C link-arg=-zstack-size=1024 -C target-cpu=mvp -C target-feature=+bulk-memory,+sign-ext,+nontrapping-fptoint" \
//   cargo build --release --target wasm32-unknown-unknown
#![no_std]

#[panic_handler]
fn panic(_: &core::panic::PanicInfo) -> ! { loop {} }

/// Iterations of z = z² + c until |z| ≥ 2, capped at `max_iter`.
#[inline(always)]
fn escape(mut zx: f32, mut zy: f32, cx: f32, cy: f32, max_iter: i32) -> i32 {
    let mut i = 0;
    while i < max_iter && zx * zx + zy * zy < 4.0 {
        let t = zx * zx - zy * zy + cx;
        zy = 2.0 * zx * zy + cy;
        zx = t;
        i += 1;
    }
    i
}

/// Pixel `id` of a `width`-wide square image -> point in [-1.5, 1.5]², shifted left by `shift`.
#[inline(always)]
fn point(id: i32, width: i32, shift: f32) -> (f32, f32) {
    let w = if width > 0 { width } else { 1 };
    let (x, y) = (id % w, id / w);
    ((x as f32 / w as f32) * 3.0 - 1.5 - shift, (y as f32 / w as f32) * 3.0 - 1.5)
}

/// Mandelbrot set. `id` = pixel index (filled in by the shader), `width` = image width, `max_iter` = iteration cap.
/// @size 256x256
#[no_mangle]
pub extern "C" fn main(id: i32, width: i32, max_iter: i32) -> i32 {
    let (cx, cy) = point(id, width, 0.75);
    escape(0.0, 0.0, cx, cy, max_iter)
}

/// Julia set for the constant c = (`cr`, `ci`).
/// @size 256x256
#[no_mangle]
pub extern "C" fn julia(id: i32, width: i32, max_iter: i32, cr: f32, ci: f32) -> i32 {
    let (zx, zy) = point(id, width, 0.0);
    escape(zx, zy, cr, ci, max_iter)
}
