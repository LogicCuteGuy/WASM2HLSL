const fs=require('fs'); const [,,wasm,n,out]=process.argv;
const inst=new WebAssembly.Instance(new WebAssembly.Module(fs.readFileSync(wasm)),{env:{sinf:x=>Math.fround(Math.sin(x)), hlsl_sin:x=>Math.fround(Math.sin(x)), input:i=>0}});
const res={}; for(let mode=0;mode<8;mode++){ const a=[]; for(let id=0;id<+n;id++) a.push(inst.exports.main(id,mode)|0); res[mode]=a; }
fs.writeFileSync(out, JSON.stringify(res));
