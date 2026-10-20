# Mandelbrot example

In `Example/Showcase.unity` (left station): the quad shows fractals computed on the GPU from `mandelbrot.rs`.

`mandelbrot.wasm` has two exports, both 256×256: `main` (Mandelbrot set) and `julia(id, width, max_iter, cr, ci)`.
`MandelbrotDemo` cycles Mandelbrot → four Julia sets, switching the runner's function with `SetFunction`.

**Networked**: the view index is `[UdonSynced]` (Manual). The owner advances it every `interval` seconds
(or when someone clicks the picture: Interact); every client, late joiners included, renders the same view on its own GPU.

To build it yourself: drag `mandelbrot.wasm` into the Hierarchy, add `MandelbrotDemo` to any GameObject,
and assign the runner and a Quad's renderer.
