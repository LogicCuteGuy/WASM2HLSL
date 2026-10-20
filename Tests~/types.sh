#!/usr/bin/env bash
# Data-type conformance: f64 (software float) and v128 (SIMD) kernels -> HLSL -> D3D11 compute, compared bit-for-bit with Node.
# Every export of types/ and reftypes.wasm is (id: i32) -> i64; exports are kept small because fxc's compile time
# grows faster than linearly with shader size. Needs: Rust wasm32 target, .NET SDK, Node (Windows: d3dcompiler_47.dll).
set -euo pipefail
cd "$(dirname "$0")"
OUT=out; mkdir -p $OUT
N=${N:-4096}
(cd types && RUSTC_BOOTSTRAP=1 RUSTFLAGS="-C link-arg=-zstack-size=2048 -C target-cpu=mvp -C target-feature=+bulk-memory,+sign-ext,+nontrapping-fptoint,+simd128,+relaxed-simd" \
  cargo build --release --target wasm32-unknown-unknown -q)
node reftypes.js $OUT/reftypes.wasm
dotnet build -c Release -v q -nologo gpu > /dev/null
fail=0
for W in types/target/wasm32-unknown-unknown/release/types.wasm $OUT/reftypes.wasm; do
  B=$(basename $W .wasm)
  rm -f $OUT/${B}_*.shader $OUT/cs_${B}_*.hlsl
  dotnet run -c Release --project translate -- $W $OUT/$B.hlsl
  for S in $OUT/${B}_*.shader; do
    E=${S#$OUT/${B}_}; E=${E%.shader}
    [ -n "${ONLY:-}" ] && [[ "$E" != $ONLY ]] && continue # e.g. ONLY='simd_i8*'
    F=$(grep -o 'f[0-9]*(id)' $S)
    cat > $OUT/cs_${B}_$E.hlsl <<HL
int _WasmWidth; int _WasmArg1;
#include "$B.hlsl"
RWStructuredBuffer<int4> outb : register(u0);
[numthreads(64, 1, 1)] void cs(uint3 t : SV_DispatchThreadID)
{ uint id = t.x; if (id >= (uint)_WasmWidth) return; uint2 r = $F; outb[id] = int4(asint(r.x), asint(r.y), (int)w_trap, 0); }
HL
    dotnet gpu/bin/Release/net10.0/gpu.dll $OUT/cs_${B}_$E.hlsl $N 0 hi > $OUT/g_$E.json 2> $OUT/g_$E.log || { echo "$B.$E GPU run failed"; fail=1; continue; }
    grep -E "compiled|gpu " $OUT/g_$E.log
    node -e "
      const fs=require('fs'); const mod=new WebAssembly.Module(fs.readFileSync('$W'));
      const g=require('./$OUT/g_$E.json'); let bad=0, first='';
      for (let i=0;i<$N;i++) { const r=BigInt.asUintN(64, new WebAssembly.Instance(mod,{}).exports['$E'](i)); const gr=(BigInt(g[2*i+1]>>>0)<<32n)|BigInt(g[2*i]>>>0);
        if (r!==gr) { if (!bad) first=' first at id '+i+': node 0x'+r.toString(16)+' gpu 0x'+gr.toString(16); bad++; } }
      console.log('$B.$E mismatches', bad, first); process.exit(bad?1:0)" || fail=1
  done
done
exit $fail
