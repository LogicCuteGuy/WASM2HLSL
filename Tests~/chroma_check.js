// Self-check for chroma.wasm: off-scale tones are cut, in-scale tones, highs and low bass pass unchanged, sweeten doubles in-scale tones,
// mix 0 is the bit-exact delayed dry signal, and feedback keeps an in-scale tone ringing after the input goes silent.
const fs = require('fs');
const TAPS = 4096, COEF = 2 * TAPS, WIN = 8192, SR = 48000, SRC = COEF + 2 * WIN;
const inp = new Float32Array(SRC + 2 * WIN), bits = new Int32Array(inp.buffer);
const env = { input: i => bits[i], hlsl_cos: Math.cos, hlsl_exp2: v => Math.pow(2, v) };
const m = new WebAssembly.Instance(new WebAssembly.Module(fs.readFileSync(process.argv[2])), { env }).exports;
let delay;
const setTaps = d => { delay = d; for (let i = 0; i < COEF; i++) inp[i] = m.taps(i, 0, 0xAB5, 34, SR, d); }; // C major
function ratio(freq, mix, count = 1024, sweet = 0) {
  inp.fill(0, COEF, SRC);
  for (let c = 0; c < 2; c++) for (let n = 0; n < WIN; n++) inp[SRC + c * WIN + n] = 0.5 * Math.sin(2 * Math.PI * freq * n / SR);
  let e = 0, d = 0;
  for (let id = 0; id < count * 2; id++) {
    const y = m.process(id, 1, count, mix, sweet, 0, delay), x = inp[SRC + (id & 1) * WIN + WIN - count + (id >> 1) - delay];
    if (mix === 0 && y !== x) throw new Error('bypass not bit-exact at ' + id);
    e += y * y; d += x * x;
  }
  return Math.sqrt(e / d);
}
const t = [[110, 'A2'], [138.59, 'C#3 (bass passes)'], [440, 'A4'], [466.16, 'A#4 x'], [523.25, 'C5'], [554.37, 'C#5 x'],
           [1318.5, 'E6'], [1480, 'F#6 x'], [8000, '8k (passes)']];
const show = () => t.map(([f, n]) => n + ' ' + ratio(f, 1).toFixed(2)).join('  ');
const pass = (f, lo, hi) => { const r = ratio(f, 1); return r > lo && r < hi; };
setTaps(2048); // HQ
console.log('HQ:  ' + show());
ratio(440, 0);
const hq = pass(110, 0.98, 1.02) && pass(138.59, 0.98, 1.02) && pass(523.25, 0.95, 1.05) && pass(8000, 0.98, 1.02)
  && pass(466.16, 0, 0.05) && pass(554.37, 0, 0.05) && pass(1480, 0, 0.05);
// tail: silent input, history = an in-scale tone -> output is that tone scaled by feedback
inp.fill(0, COEF);
for (let j = 0; j < WIN; j++) for (let c = 0; c < 2; c++) inp[COEF + j * 2 + c] = 0.5 * Math.sin(2 * Math.PI * 523.25 * j / SR);
let e = 0; for (let id = 0; id < 2048; id++) { const y = m.process(id, 1, 1024, 1, 0, 0.5, delay); e += y * y; }
const tail = Math.sqrt(e / 2048) / (0.5 / Math.SQRT2);
console.log('tail (feedback 0.5)', tail.toFixed(2));
const sw = [[523.25, 'C5'], [554.37, 'C#5 x'], [110, 'A2'], [8000, '8k']].map(([f, n]) => [n, ratio(f, 1, 1024, 1)]);
console.log('sweet 1: ' + sw.map(([n, v]) => n + ' ' + v.toFixed(2)).join('  '));
const sweetOk = sw[0][1] > 1.9 && sw[0][1] < 2.1 && sw[1][1] < 0.05 && sw[2][1] > 0.98 && sw[2][1] < 1.02 && sw[3][1] > 0.98 && sw[3][1] < 1.02;
setTaps(1024); // LOW latency: cuts only above ~800 Hz
console.log('LOW: ' + show());
ratio(440, 0);
const low = pass(466.16, 0.95, 1.05) && pass(523.25, 0.95, 1.05) && pass(1480, 0, 0.05) && pass(8000, 0.98, 1.02);
const ok = hq && low && sweetOk && tail > 0.4 && tail < 0.6;
console.log(ok ? 'ok' : 'FAIL'); process.exit(ok ? 0 : 1);
