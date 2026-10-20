using System.Linq;
using System.Text;

namespace LogicCuteGuy.Wasm2Hlsl
{
    public static partial class WasmToHlsl
    {
        // v128 is a uint4 holding the 16 bytes little-endian: i8 lane k is byte (k & 3) of component k >> 2.
        // Narrow lanes are unpacked one byte (or half) position at a time, so each uint4 op works on 4 lanes at once.
        const string PreludeSimd = @"
uint4 w_ld128(uint a) { return uint4(w_ld32(a), w_ld32(a + 4u), w_ld32(a + 8u), w_ld32(a + 12u)); }
void w_st128(uint a, uint4 v) { w_st32(a, v.x); w_st32(a + 4u, v.y); w_st32(a + 8u, v.z); w_st32(a + 12u, v.w); }
uint4 w_u8(uint4 v, uint k) { return (v >> (k * 8u)) & 0xFFu; }
int4 w_s8(uint4 v, uint k) { return (int4)(v << (24u - k * 8u)) >> 24; }
uint4 w_p8(uint4 a, uint4 b, uint4 c, uint4 d) { return (a & 0xFFu) | ((b & 0xFFu) << 8) | ((c & 0xFFu) << 16) | (d << 24); }
uint4 w_u16(uint4 v, uint k) { return (v >> (k * 16u)) & 0xFFFFu; }
int4 w_s16(uint4 v, uint k) { return (int4)(v << (16u - k * 16u)) >> 16; }
uint4 w_p16(uint4 a, uint4 b) { return (a & 0xFFFFu) | (b << 16); }
uint2 w_m64(bool c) { return c ? uint2(0xFFFFFFFFu, 0xFFFFFFFFu) : uint2(0u, 0u); }
uint w_alltrue8(uint4 v) { return (uint)(all(w_u8(v, 0u) != 0u) && all(w_u8(v, 1u) != 0u) && all(w_u8(v, 2u) != 0u) && all(w_u8(v, 3u) != 0u)); }
uint w_alltrue16(uint4 v) { return (uint)(all(w_u16(v, 0u) != 0u) && all(w_u16(v, 1u) != 0u)); }
uint w_bitmask8(uint4 v) { uint4 n = ((v >> 7) & 1u) | ((v >> 14) & 2u) | ((v >> 21) & 4u) | ((v >> 28) & 8u); return n.x | (n.y << 4) | (n.z << 8) | (n.w << 12); }
uint w_bitmask16(uint4 v) { uint4 n = ((v >> 15) & 1u) | ((v >> 30) & 2u); return n.x | (n.y << 2) | (n.z << 4) | (n.w << 6); }
uint w_bitmask32(uint4 v) { uint4 n = v >> 31; return n.x | (n.y << 1) | (n.z << 2) | (n.w << 3); }
// Lanes 2c and 2c+1 (narrow16) or 4c..4c+3 (narrow8) of a:b land in component c.
uint4 w_narrow8(uint4 a, uint4 b, int lo, int hi)
{
    uint4 p = uint4(a.x, a.z, b.x, b.z), q = uint4(a.y, a.w, b.y, b.w);
    return w_p8((uint4)clamp(w_s16(p, 0u), lo, hi), (uint4)clamp(w_s16(p, 1u), lo, hi), (uint4)clamp(w_s16(q, 0u), lo, hi), (uint4)clamp(w_s16(q, 1u), lo, hi));
}
uint4 w_narrow16(uint4 a, uint4 b, int lo, int hi)
{
    uint4 p = uint4(a.x, a.z, b.x, b.z), q = uint4(a.y, a.w, b.y, b.w);
    return w_p16((uint4)clamp((int4)p, lo, hi), (uint4)clamp((int4)q, lo, hi));
}
// Widen the 8 bytes / 4 halves / 2 words of h to the next lane size.
uint4 w_ext8(uint2 h, bool s)
{
    uint4 b = uint4(h.x, h.x >> 16, h.y, h.y >> 16);
    return w_p16(s ? (uint4)((int4)(b << 24) >> 24) : b & 0xFFu, s ? (uint4)((int4)(b << 16) >> 24) : (b >> 8) & 0xFFu);
}
uint4 w_ext16(uint2 h, bool s) { uint4 b = uint4(h.x, h.x >> 16, h.y, h.y >> 16); return s ? (uint4)((int4)(b << 16) >> 16) : b & 0xFFFFu; }
uint4 w_ext32(uint2 h, bool s) { return uint4(h.x, s ? (uint)((int)h.x >> 31) : 0u, h.y, s ? (uint)((int)h.y >> 31) : 0u); }
uint w_lane8(uint4 v, uint i) { uint w = i < 4u ? v.x : i < 8u ? v.y : i < 12u ? v.z : v.w; return i < 16u ? (w >> ((i & 3u) * 8u)) & 0xFFu : 0u; }
uint4 w_swizzle(uint4 a, uint4 s)
{
    uint4 r = uint4(0u, 0u, 0u, 0u);
    [unroll] for (uint c = 0u; c < 4u; c++)
        [unroll] for (uint k = 0u; k < 4u; k++) r[c] |= w_lane8(a, (s[c] >> (k * 8u)) & 0xFFu) << (k * 8u);
    return r;
}
uint4 w_trunc_s4(float4 f) { return uint4(w_trunc_s(f.x), w_trunc_s(f.y), w_trunc_s(f.z), w_trunc_s(f.w)); }
uint4 w_trunc_u4(float4 f) { return uint4(w_trunc_u(f.x), w_trunc_u(f.y), w_trunc_u(f.z), w_trunc_u(f.w)); }
";
    }

    internal sealed partial class FuncEmitter
    {
        static readonly string[] Comp = { "x", "y", "z", "w" };

        /// <summary>
        /// A lane-wise op over 8/16/32-bit lanes. In <paramref name="fmt"/>, {0}, {1}, ... are one group of lanes of each operand
        /// (int4 when <paramref name="signed"/>, else uint4); the result is truncated back to the lane width.
        /// </summary>
        static string Lanes(int bits, bool signed, string fmt, params string[] v)
        {
            if (bits == 32) return "(uint4)(" + string.Format(fmt, v.Select(x => signed ? $"(int4){x}" : x).ToArray()) + ")";
            var parts = Enumerable.Range(0, 32 / bits).Select(k =>
                "(uint4)(" + string.Format(fmt, v.Select(x => $"w_{(signed ? 's' : 'u')}{bits}({x}, {k}u)").ToArray()) + ")");
            return $"w_p{bits}({string.Join(", ", parts)})";
        }

        /// <summary>A lane-wise op over the two 64-bit lanes; {0}, {1}, ... are uint2 (i64 or f64 bits) and so is the result.</summary>
        static string Lanes64(string fmt, params string[] v) =>
            $"uint4({string.Format(fmt, v.Select(x => $"({x}).xy").ToArray())}, {string.Format(fmt, v.Select(x => $"({x}).zw").ToArray())})";

        static string Replace(string v, int comp, string x) =>
            "uint4(" + string.Join(", ", Enumerable.Range(0, 4).Select(c => c == comp ? x : $"{v}.{Comp[c]}")) + ")";

        static string Shuffle(string a, string b, byte[] lanes)
        {
            var comps = new string[4];
            for (int c = 0; c < 4; c++)
            {
                var sb = new StringBuilder();
                for (int k = 0; k < 4; k++)
                {
                    int l = lanes[c * 4 + k], i = l & 15;
                    string w = $"{(l < 16 ? a : b)}.{Comp[i >> 2]}";
                    if ((i & 3) != 0) w = $"({w} >> {8 * (i & 3)}u)";
                    if (k < 3) w = $"({w} & 0xFFu)";
                    if (k > 0) w = $"({w} << {8 * k}u)";
                    sb.Append(k == 0 ? w : " | " + w);
                }
                comps[c] = sb.ToString();
            }
            return $"uint4({string.Join(", ", comps)})";
        }

        void LUn(int bits, bool signed, string fmt) { var a = Pop(); Push(WasmType.V128, Lanes(bits, signed, fmt, a)); }
        void LBin(int bits, bool signed, string fmt) { var b = Pop(); var a = Pop(); Push(WasmType.V128, Lanes(bits, signed, fmt, a, b)); }
        void L64Un(string fmt) { var a = Pop(); Push(WasmType.V128, Lanes64(fmt, a)); }
        void L64Bin(string fmt) { var b = Pop(); var a = Pop(); Push(WasmType.V128, Lanes64(fmt, a, b)); }
        void Shift(int bits, bool signed, string fmt) { var s = Pop(); var a = Pop(); Push(WasmType.V128, Lanes(bits, signed, fmt.Replace("{s}", s), a)); }
        void Shift64(string fn) { var s = Pop(); var a = Pop(); Push(WasmType.V128, Lanes64(fn + "({0}, " + s + ")", a)); }

        static string B(bool v) => v ? "true" : "false";

        /// <summary>One 0xFD (SIMD and relaxed SIMD) instruction.</summary>
        void Simd(uint op)
        {
            const WasmType I = WasmType.I32, L = WasmType.I64, F = WasmType.F32, D = WasmType.F64, V = WasmType.V128;
            if (op >= 35 && op <= 64) // i8x16 / i16x8 / i32x4 comparisons: eq ne lt_s lt_u gt_s gt_u le_s le_u ge_s ge_u
            {
                int k = (int)(op - 35) % 10, bits = 8 << (int)((op - 35) / 10);
                string[] cmp = { "==", "!=", "<", "<", ">", ">", "<=", "<=", ">=", ">=" };
                LBin(bits, k >= 2 && k % 2 == 0, $"0u - (uint4)({{0}} {cmp[k]} {{1}})");
                return;
            }
            if (op >= 65 && op <= 70) // f32x4 eq ne lt gt le ge
            {
                string[] cmp = { "==", "!=", "<", ">", "<=", ">=" };
                Bin(V, $"0u - (uint4)(asfloat({{0}}) {cmp[op - 65]} asfloat({{1}}))");
                return;
            }
            if (op >= 71 && op <= 76) // f64x2 eq ne lt gt le ge
            {
                string[] cmp = { "w_deq({0}, {1})", "!w_deq({0}, {1})", "w_dlt({0}, {1})", "w_dlt({1}, {0})", "w_dle({0}, {1})", "w_dle({1}, {0})" };
                L64Bin($"w_m64({cmp[op - 71]})");
                return;
            }

            switch (op)
            {
                case 0: Load(V, "w_ld128({0})"); break;
                case 1: case 2: Load(V, $"w_ext8(w_ld64({{0}}), {B(op == 1)})"); break;
                case 3: case 4: Load(V, $"w_ext16(w_ld64({{0}}), {B(op == 3)})"); break;
                case 5: case 6: Load(V, $"w_ext32(w_ld64({{0}}), {B(op == 5)})"); break;
                case 7: Load(V, "(uint4)(w_ld8({0}) * 0x01010101u)"); break;
                case 8: Load(V, "(uint4)(w_ld16({0}) * 0x10001u)"); break;
                case 9: Load(V, "(uint4)w_ld32({0})"); break;
                case 10: Load(V, "w_ld64({0}).xyxy"); break;
                case 11: Store("w_st128({0}, {1})"); break;
                case 12: { ulong lo = _r.U64Fixed(), hi = _r.U64Fixed(); Push(V, WasmToHlsl.Const(V, lo, hi)); break; }
                case 13: { var lanes = _r.Slice(16); var b = Pop(); var a = Pop(); Push(V, Shuffle(a, b, lanes)); break; }
                case 14: Bin(V, "w_swizzle({0}, {1})"); break;
                case 15: Un(V, "(uint4)(({0} & 0xFFu) * 0x01010101u)"); break;
                case 16: Un(V, "(uint4)(({0} & 0xFFFFu) * 0x10001u)"); break;
                case 17: Un(V, "(uint4){0}"); break;
                case 18: case 20: Un(V, "{0}.xyxy"); break;
                case 19: Un(V, "(uint4)asuint({0})"); break;

                case 21: case 22:
                {
                    int l = _r.Byte();
                    Un(I, op == 21 ? $"(uint)((int)({{0}}.{Comp[l >> 2]} << {24 - 8 * (l & 3)}u) >> 24)" : $"({{0}}.{Comp[l >> 2]} >> {8 * (l & 3)}u) & 0xFFu");
                    break;
                }
                case 23: { int l = _r.Byte(); var x = Pop(); var v = Pop(); Push(V, ReplaceByte(v, l, x)); break; }
                case 24: case 25:
                {
                    int l = _r.Byte();
                    Un(I, op == 24 ? $"(uint)((int)({{0}}.{Comp[l >> 1]} << {16 - 16 * (l & 1)}u) >> 16)" : $"({{0}}.{Comp[l >> 1]} >> {16 * (l & 1)}u) & 0xFFFFu");
                    break;
                }
                case 26: { int l = _r.Byte(); var x = Pop(); var v = Pop(); Push(V, ReplaceHalf(v, l, x)); break; }
                case 27: { int l = _r.Byte(); Un(I, $"{{0}}.{Comp[l]}"); break; }
                case 28: { int l = _r.Byte(); var x = Pop(); var v = Pop(); Push(V, Replace(v, l, x)); break; }
                case 29: case 33: { int l = _r.Byte(); Un(op == 29 ? L : D, l == 0 ? "{0}.xy" : "{0}.zw"); break; }
                case 30: case 34: { int l = _r.Byte(); var x = Pop(); var v = Pop(); Push(V, l == 0 ? $"uint4({x}, {v}.zw)" : $"uint4({v}.xy, {x})"); break; }
                case 31: { int l = _r.Byte(); Un(F, $"asfloat({{0}}.{Comp[l]})"); break; }
                case 32: { int l = _r.Byte(); var x = Pop(); var v = Pop(); Push(V, Replace(v, l, $"asuint({x})")); break; }

                case 77: Un(V, "~{0}"); break;
                case 78: Bin(V, "{0} & {1}"); break;
                case 79: Bin(V, "{0} & ~{1}"); break;
                case 80: Bin(V, "{0} | {1}"); break;
                case 81: Bin(V, "{0} ^ {1}"); break;
                case 82: Tern(V, "({0} & {2}) | ({1} & ~{2})"); break;
                case 83: Un(I, "(uint)any({0} != 0u)"); break;

                case 84: case 85: case 86: case 87: // v128.loadN_lane
                {
                    var v = Pop(); string addr = Addr(Pop()); int l = _r.Byte();
                    Push(V, op == 84 ? ReplaceByte(v, l, $"w_ld8({addr})") : op == 85 ? ReplaceHalf(v, l, $"w_ld16({addr})")
                        : op == 86 ? Replace(v, l, $"w_ld32({addr})") : l == 0 ? $"uint4(w_ld64({addr}), {v}.zw)" : $"uint4({v}.xy, w_ld64({addr}))");
                    break;
                }
                case 88: case 89: case 90: case 91: // v128.storeN_lane
                {
                    var v = Pop(); string addr = Addr(Pop()); int l = _r.Byte();
                    Line(op == 88 ? $"w_st8({addr}, {v}.{Comp[l >> 2]} >> {8 * (l & 3)}u);" : op == 89 ? $"w_st16({addr}, {v}.{Comp[l >> 1]} >> {16 * (l & 1)}u);"
                        : op == 90 ? $"w_st32({addr}, {v}.{Comp[l]});" : $"w_st64({addr}, {v}.{(l == 0 ? "xy" : "zw")});");
                    break;
                }
                case 92: Load(V, "uint4(w_ld32({0}), 0u, 0u, 0u)"); break;
                case 93: Load(V, "uint4(w_ld64({0}), 0u, 0u)"); break;
                case 94: Un(V, "uint4(asuint(w_d2f({0}.xy)), asuint(w_d2f({0}.zw)), 0u, 0u)"); break;
                case 95: Un(V, "uint4(w_f2d(asfloat({0}.x)), w_f2d(asfloat({0}.y)))"); break;

                // i8x16
                case 96: LUn(8, true, "abs({0})"); break;
                case 97: LUn(8, false, "0u - {0}"); break;
                case 98: LUn(8, false, "countbits({0})"); break;
                case 99: Un(I, "w_alltrue8({0})"); break;
                case 100: Un(I, "w_bitmask8({0})"); break;
                case 101: Bin(V, "w_narrow8({0}, {1}, -128, 127)"); break;
                case 102: Bin(V, "w_narrow8({0}, {1}, 0, 255)"); break;
                case 103: Un(V, "asuint(ceil(asfloat({0})))"); break;
                case 104: Un(V, "asuint(floor(asfloat({0})))"); break;
                case 105: Un(V, "asuint(trunc(asfloat({0})))"); break;
                case 106: Un(V, "asuint(round(asfloat({0})))"); break;
                case 107: Bin(V, "({0} << ({1} & 7u)) & (((0xFFu << ({1} & 7u)) & 0xFFu) * 0x01010101u)"); break; // SWAR: fxc miscompiles the per-byte form
                case 108: Shift(8, true, "{0} >> (int)({s} & 7u)"); break;
                case 109: Bin(V, "({0} >> ({1} & 7u)) & ((0xFFu >> ({1} & 7u)) * 0x01010101u)"); break;
                case 110: LBin(8, false, "{0} + {1}"); break;
                case 111: LBin(8, true, "clamp({0} + {1}, -128, 127)"); break;
                case 112: LBin(8, false, "min({0} + {1}, 255u)"); break;
                case 113: LBin(8, false, "{0} - {1}"); break;
                case 114: LBin(8, true, "clamp({0} - {1}, -128, 127)"); break;
                case 115: LBin(8, false, "max({0}, {1}) - {1}"); break;
                case 116: L64Un("w_dint({0}, 3u)"); break;
                case 117: L64Un("w_dint({0}, 2u)"); break;
                case 118: LBin(8, true, "min({0}, {1})"); break;
                case 119: LBin(8, false, "min({0}, {1})"); break;
                case 120: LBin(8, true, "max({0}, {1})"); break;
                case 121: LBin(8, false, "max({0}, {1})"); break;
                case 122: L64Un("w_dint({0}, 1u)"); break;
                case 123: LBin(8, false, "({0} + {1} + 1u) >> 1"); break;
                case 124: case 125:
                {
                    char c = op == 124 ? 's' : 'u';
                    Un(V, $"w_p16((uint4)(w_{c}8({{0}}, 0u) + w_{c}8({{0}}, 1u)), (uint4)(w_{c}8({{0}}, 2u) + w_{c}8({{0}}, 3u)))");
                    break;
                }
                case 126: case 127: { char c = op == 126 ? 's' : 'u'; Un(V, $"(uint4)(w_{c}16({{0}}, 0u) + w_{c}16({{0}}, 1u))"); break; }

                // i16x8
                case 128: LUn(16, true, "abs({0})"); break;
                case 129: LUn(16, false, "0u - {0}"); break;
                case 130: LBin(16, true, "clamp(({0} * {1} + 16384) >> 15, -32768, 32767)"); break;
                case 131: Un(I, "w_alltrue16({0})"); break;
                case 132: Un(I, "w_bitmask16({0})"); break;
                case 133: Bin(V, "w_narrow16({0}, {1}, -32768, 32767)"); break;
                case 134: Bin(V, "w_narrow16({0}, {1}, 0, 65535)"); break;
                case 135: Un(V, "w_ext8({0}.xy, true)"); break;
                case 136: Un(V, "w_ext8({0}.zw, true)"); break;
                case 137: Un(V, "w_ext8({0}.xy, false)"); break;
                case 138: Un(V, "w_ext8({0}.zw, false)"); break;
                case 139: Bin(V, "({0} << ({1} & 15u)) & (((0xFFFFu << ({1} & 15u)) & 0xFFFFu) * 0x10001u)"); break;
                case 140: Shift(16, true, "{0} >> (int)({s} & 15u)"); break;
                case 141: Bin(V, "({0} >> ({1} & 15u)) & ((0xFFFFu >> ({1} & 15u)) * 0x10001u)"); break;
                case 142: LBin(16, false, "{0} + {1}"); break;
                case 143: LBin(16, true, "clamp({0} + {1}, -32768, 32767)"); break;
                case 144: LBin(16, false, "min({0} + {1}, 65535u)"); break;
                case 145: LBin(16, false, "{0} - {1}"); break;
                case 146: LBin(16, true, "clamp({0} - {1}, -32768, 32767)"); break;
                case 147: LBin(16, false, "max({0}, {1}) - {1}"); break;
                case 148: L64Un("w_dint({0}, 0u)"); break;
                case 149: LBin(16, false, "{0} * {1}"); break;
                case 150: LBin(16, true, "min({0}, {1})"); break;
                case 151: LBin(16, false, "min({0}, {1})"); break;
                case 152: LBin(16, true, "max({0}, {1})"); break;
                case 153: LBin(16, false, "max({0}, {1})"); break;
                case 155: LBin(16, false, "({0} + {1} + 1u) >> 1"); break;
                case 156: case 157: case 158: case 159:
                {
                    var b = Pop(); var a = Pop(); string h = op == 156 || op == 158 ? "xy" : "zw", s = B(op < 158);
                    Push(V, Lanes(16, false, "{0} * {1}", $"w_ext8({a}.{h}, {s})", $"w_ext8({b}.{h}, {s})"));
                    break;
                }

                // i32x4
                case 160: Un(V, "(int4){0} < 0 ? 0u - {0} : {0}"); break;
                case 161: Un(V, "0u - {0}"); break;
                case 163: Un(I, "(uint)all({0} != 0u)"); break;
                case 164: Un(I, "w_bitmask32({0})"); break;
                case 167: Un(V, "w_ext16({0}.xy, true)"); break;
                case 168: Un(V, "w_ext16({0}.zw, true)"); break;
                case 169: Un(V, "w_ext16({0}.xy, false)"); break;
                case 170: Un(V, "w_ext16({0}.zw, false)"); break;
                case 171: Bin(V, "{0} << ({1} & 31u)"); break;
                case 172: Bin(V, "(uint4)((int4){0} >> (int)({1} & 31u))"); break;
                case 173: Bin(V, "{0} >> ({1} & 31u)"); break;
                case 174: Bin(V, "{0} + {1}"); break;
                case 177: Bin(V, "{0} - {1}"); break;
                case 181: Bin(V, "{0} * {1}"); break;
                case 182: Bin(V, "(uint4)min((int4){0}, (int4){1})"); break;
                case 183: Bin(V, "min({0}, {1})"); break;
                case 184: Bin(V, "(uint4)max((int4){0}, (int4){1})"); break;
                case 185: Bin(V, "max({0}, {1})"); break;
                case 186: Bin(V, "(uint4)(w_s16({0}, 0u) * w_s16({1}, 0u) + w_s16({0}, 1u) * w_s16({1}, 1u))"); break;
                case 188: Bin(V, "w_ext16({0}.xy, true) * w_ext16({1}.xy, true)"); break;
                case 189: Bin(V, "w_ext16({0}.zw, true) * w_ext16({1}.zw, true)"); break;
                case 190: Bin(V, "w_ext16({0}.xy, false) * w_ext16({1}.xy, false)"); break;
                case 191: Bin(V, "w_ext16({0}.zw, false) * w_ext16({1}.zw, false)"); break;

                // i64x2
                case 192: L64Un("w_abs64({0})"); break;
                case 193: L64Un("w_neg64({0})"); break;
                case 195: Un(I, "(uint)all(({0}.xz | {0}.yw) != 0u)"); break;
                case 196: Un(I, "({0}.y >> 31) | (({0}.w >> 31) << 1)"); break;
                case 199: Un(V, "w_ext32({0}.xy, true)"); break;
                case 200: Un(V, "w_ext32({0}.zw, true)"); break;
                case 201: Un(V, "w_ext32({0}.xy, false)"); break;
                case 202: Un(V, "w_ext32({0}.zw, false)"); break;
                case 203: Shift64("w_shl64"); break;
                case 204: Shift64("w_shrs64"); break;
                case 205: Shift64("w_shru64"); break;
                case 206: L64Bin("w_add64({0}, {1})"); break;
                case 209: L64Bin("w_sub64({0}, {1})"); break;
                case 213: L64Bin("w_mul64({0}, {1})"); break;
                case 214: L64Bin("w_m64(all({0} == {1}))"); break;
                case 215: L64Bin("w_m64(any({0} != {1}))"); break;
                case 216: L64Bin("w_m64(w_lts64({0}, {1}))"); break;
                case 217: L64Bin("w_m64(w_lts64({1}, {0}))"); break;
                case 218: L64Bin("w_m64(!w_lts64({1}, {0}))"); break;
                case 219: L64Bin("w_m64(!w_lts64({0}, {1}))"); break;
                case 220: case 221: case 222: case 223:
                {
                    var b = Pop(); var a = Pop(); string h = op == 220 || op == 222 ? "xy" : "zw", s = B(op < 222);
                    Push(V, Lanes64("w_mul64({0}, {1})", $"w_ext32({a}.{h}, {s})", $"w_ext32({b}.{h}, {s})"));
                    break;
                }

                // f32x4
                case 224: Un(V, "{0} & 0x7FFFFFFFu"); break;
                case 225: Un(V, "{0} ^ 0x80000000u"); break;
                case 227: Un(V, "asuint(sqrt(asfloat({0})))"); break;
                case 228: Bin(V, "asuint(asfloat({0}) + asfloat({1}))"); break;
                case 229: Bin(V, "asuint(asfloat({0}) - asfloat({1}))"); break;
                case 230: Bin(V, "asuint(asfloat({0}) * asfloat({1}))"); break;
                case 231: Bin(V, "asuint(asfloat({0}) / asfloat({1}))"); break;
                case 232: Bin(V, "asuint(min(asfloat({0}), asfloat({1})))"); break;
                case 233: Bin(V, "asuint(max(asfloat({0}), asfloat({1})))"); break;
                case 234: Bin(V, "asfloat({1}) < asfloat({0}) ? {1} : {0}"); break;
                case 235: Bin(V, "asfloat({0}) < asfloat({1}) ? {1} : {0}"); break;

                // f64x2
                case 236: L64Un("w_dabs({0})"); break;
                case 237: L64Un("w_dneg({0})"); break;
                case 239: L64Un("w_dsqrt({0})"); break;
                case 240: L64Bin("w_dadd({0}, {1})"); break;
                case 241: L64Bin("w_dsub({0}, {1})"); break;
                case 242: L64Bin("w_dmul({0}, {1})"); break;
                case 243: L64Bin("w_ddiv({0}, {1})"); break;
                case 244: L64Bin("w_dmin({0}, {1})"); break;
                case 245: L64Bin("w_dmax({0}, {1})"); break;
                case 246: L64Bin("w_dlt({1}, {0}) ? {1} : {0}"); break;
                case 247: L64Bin("w_dlt({0}, {1}) ? {1} : {0}"); break;

                // conversions
                case 248: Un(V, "w_trunc_s4(asfloat({0}))"); break;
                case 249: Un(V, "w_trunc_u4(asfloat({0}))"); break;
                case 250: Un(V, "asuint((float4)(int4){0})"); break;
                case 251: Un(V, "asuint((float4){0})"); break;
                case 252: Un(V, "uint4(w_dtrunc_s({0}.xy), w_dtrunc_s({0}.zw), 0u, 0u)"); break;
                case 253: Un(V, "uint4(w_dtrunc_u({0}.xy), w_dtrunc_u({0}.zw), 0u, 0u)"); break;
                case 254: Un(V, "uint4(w_dcvt_s32({0}.x), w_dcvt_s32({0}.y))"); break;
                case 255: Un(V, "uint4(w_dcvt_u32({0}.x), w_dcvt_u32({0}.y))"); break;

                // relaxed SIMD: one deterministic choice each (the strict instruction, except min/max below)
                case 256: Simd(14); break;
                case 257: Simd(248); break;
                case 258: Simd(249); break;
                case 259: Simd(252); break;
                case 260: Simd(253); break;
                case 261: Tern(V, "asuint(asfloat({0}) * asfloat({1}) + asfloat({2}))"); break;
                case 262: Tern(V, "asuint(-(asfloat({0}) * asfloat({1})) + asfloat({2}))"); break;
                case 263: { var c = Pop(); var b = Pop(); var a = Pop(); Push(V, Lanes64("w_dadd(w_dmul({0}, {1}), {2})", a, b, c)); break; }
                case 264: { var c = Pop(); var b = Pop(); var a = Pop(); Push(V, Lanes64("w_dsub({2}, w_dmul({0}, {1}))", a, b, c)); break; }
                case 265: case 266: case 267: case 268: Simd(82); break;
                // relaxed min/max: x86 minps/maxps (a < b ? a : b). LLVM lowers pmin/pmax to these and relies on it.
                case 269: Bin(V, "asfloat({0}) < asfloat({1}) ? {0} : {1}"); break;
                case 270: Bin(V, "asfloat({0}) > asfloat({1}) ? {0} : {1}"); break;
                case 271: L64Bin("w_dlt({0}, {1}) ? {0} : {1}"); break;
                case 272: L64Bin("w_dlt({1}, {0}) ? {0} : {1}"); break;
                case 273: Simd(130); break;
                case 274: Bin(V, "w_p16((uint4)(w_s8({0}, 0u) * w_s8({1}, 0u) + w_s8({0}, 1u) * w_s8({1}, 1u)), (uint4)(w_s8({0}, 2u) * w_s8({1}, 2u) + w_s8({0}, 3u) * w_s8({1}, 3u)))"); break;
                case 275: Tern(V, "(uint4)(w_s8({0}, 0u) * w_s8({1}, 0u) + w_s8({0}, 1u) * w_s8({1}, 1u) + w_s8({0}, 2u) * w_s8({1}, 2u) + w_s8({0}, 3u) * w_s8({1}, 3u)) + {2}"); break;

                default: throw new System.NotSupportedException($"SIMD opcode 0xFD {op} is not supported");
            }
        }

        static string ReplaceByte(string v, int l, string x)
        {
            int sh = 8 * (l & 3);
            return Replace(v, l >> 2, $"({v}.{Comp[l >> 2]} & ~(0xFFu << {sh}u)) | (({x} & 0xFFu) << {sh}u)");
        }

        static string ReplaceHalf(string v, int l, string x)
        {
            int sh = 16 * (l & 1);
            return Replace(v, l >> 1, $"({v}.{Comp[l >> 1]} & ~(0xFFFFu << {sh}u)) | (({x} & 0xFFFFu) << {sh}u)");
        }
    }
}
