#!/usr/bin/env bash
# GPU conformance test: Rust kernel -> wasm -> HLSL -> D3D11 compute, compared bit-for-bit with Node's WebAssembly.
# Needs: rustup target wasm32-unknown-unknown, .NET SDK, Node, Windows SDK fxc.exe (set FXC=...).
set -euo pipefail
cd "$(dirname "$0")"
FXC="${FXC:-/c/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/fxc.exe}"
OUT=out; mkdir -p $OUT
(cd kernel && RUSTFLAGS="-C link-arg=-zstack-size=2048 -C target-cpu=mvp -C target-feature=+bulk-memory,+sign-ext,+nontrapping-fptoint" \
  cargo build --release --target wasm32-unknown-unknown -q)
W=kernel/target/wasm32-unknown-unknown/release/kernel.wasm
dotnet run -c Release --project translate -- $W $OUT/kernel.hlsl
F=$(grep -o 'f[0-9]*(id, (uint)_WasmArg1)' $OUT/kernel_main.shader)
cat > $OUT/cs.hlsl <<HL
int _WasmWidth; int _WasmArg1;
#include "kernel.hlsl"
RWStructuredBuffer<int4> outb : register(u0);
[numthreads(64, 1, 1)] void cs(uint3 t : SV_DispatchThreadID)
{ uint id = t.x; if (id >= (uint)_WasmWidth) return; uint r = $F; outb[id] = int4(asint(r), 0, (int)w_trap, 0); }
HL
# No fxc.exe: gpu compiles the .hlsl itself with d3dcompiler_47.dll (same compiler).
CS=$OUT/cs.hlsl; [ -x "$FXC" ] && "$FXC" //nologo //T cs_5_0 //E cs //Fo $OUT/cs.cso $OUT/cs.hlsl && CS=$OUT/cs.cso
node ref.js $W 1024 $OUT/ref.json
dotnet build -c Release -v q -nologo gpu
fail=0
for m in 0 1 2 3 4 5 6 7 8; do
  WASM_ROM=$OUT/kernel.rom dotnet gpu/bin/Release/net10.0/gpu.dll $CS 1024 $m > $OUT/g$m.json 2>/dev/null
  # mode 4 uses the GPU's sin(): allow ±1
  node -e "const r=require('./$OUT/ref.json')[$m], g=require('./$OUT/g$m.json'); const tol=$m===4?1:0; let bad=0; for(let i=0;i<r.length;i++) if(Math.abs(r[i]-g[i])>tol) bad++; console.log('mode $m mismatches', bad); process.exit(bad?1:0)" || fail=1
done
exit $fail
