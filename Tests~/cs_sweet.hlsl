int _WasmWidth; int _WasmMode;
#include "chroma.hlsl"
RWStructuredBuffer<int4> outb : register(u0);
[numthreads(64, 1, 1)] void cs(uint3 t : SV_DispatchThreadID)
{ uint id = t.x; if (id >= (uint)_WasmWidth) return; outb[id] = int4(asint(f3(id, 1u, 800u, 1.0, 1.0, 0.35, 2048u)), 0, (int)w_trap, 0); }
