// Writes reftypes.wasm: funcref/externref values, typed select, ref.func/ref.null/ref.is_null, funcref globals,
// three tables and every table instruction. No compiler emits these from Rust, so the module is assembled by hand.
// Exports refs(id) -> i64 and tables(id) -> i64 (types.sh compares them with Node).
const fs = require('fs');
const u = n => { const b = []; do { let x = n & 0x7F; n >>>= 7; if (n) x |= 0x80; b.push(x); } while (n); return b; };
const s = n => { const b = []; for (;;) { const x = n & 0x7F; n >>= 7; if ((n === 0 && !(x & 0x40)) || (n === -1 && (x & 0x40))) { b.push(x); return b; } b.push(x | 0x80); } };
const vec = items => [...u(items.length), ...items.flat(Infinity)];
const sec = (id, items) => { const body = vec(items); return [id, ...u(body.length), ...body]; };
const str = t => vec([...Buffer.from(t)]);

const I32 = 0x7F, I64 = 0x7E, FUNCREF = 0x70, EXTERNREF = 0x6F;
const get = i => [0x20, ...u(i)], set = i => [0x21, ...u(i)], c = v => [0x41, ...s(v)];
const and = 0x71, add = 0x6A, mul = 0x6C, sub = 0x6B, xor = 0x73, or = 0x72, shl = 0x74, shr = 0x76, eq = 0x46, ext = 0xAD;
const callInd = (type, table) => [0x11, ...u(type), ...u(table)];
const tget = t => [0x25, ...u(t)], tset = t => [0x26, ...u(t)];
const fc = (op, ...imm) => [0xFC, ...u(op), ...imm.flatMap(u)];
const nullf = [0xD0, FUNCREF], nulle = [0xD0, EXTERNREF], isnull = 0xD1, rfunc = f => [0xD2, ...u(f)];
const selRef = [0x1C, 1, FUNCREF];
const body = (locals, code) => { const b = [...vec(locals.map(([n, t]) => [...u(n), t])), ...code.flat(Infinity), 0x0B]; return [...u(b.length), ...b]; };

const x = get(1), mode = get(2);
const callTable = (t, idx) => [x, idx, callInd(1, t), ext];
const modes = [
  // 0: call_indirect through table 0 (written in mode 1, so dispatch reads the table)
  callTable(0, [x, c(3), and]),
  // 1: table.set 0 [x&3] = table.get 0 [(x+1)&3], then call through it
  [x, c(3), and, x, c(1), add, c(3), and, tget(0), tset(0), callTable(0, [x, c(3), and])],
  // 2: table.init table 1 from passive segment 1, then call a filled slot
  [x, c(1), and, x, c(1), shr, c(1), and, c(2), fc(12, 1, 1), callTable(1, [x, c(1), and, x, c(2), shr, c(1), and, add])],
  // 3: table.copy 0 -> 1
  [x, c(1), and, x, c(1), shr, c(2), and, c(2), fc(14, 1, 0), callTable(1, [x, c(1), and, x, c(2), shr, c(1), and, add])],
  // 4: table.fill 1 with a typed-select funcref
  [c(0), rfunc(3), rfunc(0), x, c(1), and, selRef, c(4), fc(17, 1), callTable(1, [x, c(3), and])],
  // 5: table.size, table.grow (table 1 is at its max, so grow fails everywhere)
  [fc(16, 0), fc(16, 2), c(8), shl, add, nullf, c(1), fc(15, 1), c(16), shl, xor, ext],
  // 6: elem.drop, zero-length table.init after the drop, null slots, an externref table
  [fc(13, 1), c(0), c(0), c(0), fc(12, 1, 1), c(1), nulle, tset(2), c(0), tget(1), isnull, c(1), tget(2), isnull, c(1), shl, or, ext],
  // 7: funcref global stored into a table and called; global.set of a ref.func
  [c(0), [0x23, 0], tset(1), x, c(0), callInd(1, 1), rfunc(1), [0x24, 0], c(1), [0x23, 0], tset(1), x, c(1), callInd(1, 1), add, ext],
];
let chain = modes[7];
for (let k = 6; k >= 0; k--) chain = [mode, c(k), eq, 0x04, I64, modes[k], 0x05, chain, 0x0B];

const bytes = [
  0x00, 0x61, 0x73, 0x6D, 1, 0, 0, 0,
  ...sec(1, [[0x60, 1, I32, 1, I64], [0x60, 1, I32, 1, I32]]),
  ...sec(3, [[1], [1], [1], [1], [0], [0]]),
  ...sec(4, [[FUNCREF, 0, 8], [FUNCREF, 1, 4, 4], [EXTERNREF, 0, 2]]),
  ...sec(6, [[FUNCREF, 1, ...rfunc(2), 0x0B], [EXTERNREF, 1, ...nulle, 0x0B]]),
  ...sec(7, [[...str('refs'), 0, 4], [...str('tables'), 0, 5]]),
  ...sec(9, [[0, ...c(0), 0x0B, ...vec([[0], [1], [2], [3]])], [1, 0, ...vec([[3], [2], [1], [0]])], [3, 0, ...vec([[0], [1], [2], [3]])]]),
  ...sec(10, [
    body([], [get(0), c(1), add]), body([], [get(0), c(2), mul]), body([], [get(0), c(7), sub]), body([], [get(0), c(0x55), xor]),
    body([[1, EXTERNREF], [1, FUNCREF]], [
      nullf, isnull, get(1), isnull, c(1), shl, or,
      rfunc(1), nullf, get(0), c(1), and, selRef, set(2),
      get(2), isnull, c(2), shl, or, [0x23, 1], isnull, c(3), shl, or, ext]),
    body([[2, I32]], [get(0), c(3), shr, set(1), get(0), c(7), and, set(2), chain]),
  ]),
];
const out = Buffer.from(bytes.flat(Infinity));
new WebAssembly.Module(out); // validates
fs.writeFileSync(process.argv[2] || 'reftypes.wasm', out);
