using System;
using System.Collections.Generic;
using System.Text;

namespace LogicCuteGuy.Wasm2Hlsl
{
    public enum WasmType : byte { I32 = 0x7F, I64 = 0x7E, F32 = 0x7D, F64 = 0x7C, V128 = 0x7B, FuncRef = 0x70, ExternRef = 0x6F }

    public sealed class FuncType
    {
        public WasmType[] Params;
        public WasmType[] Results;
    }

    public sealed class Import
    {
        public string Module, Name;
        public byte Kind;     // 0 func, 1 table, 2 memory, 3 global
        public uint TypeIndex; // func imports only
    }

    public sealed class Global
    {
        public WasmType Type;
        public bool Mutable;
        public ulong Init, InitHi; // raw bits (InitHi: upper half of a v128)
    }

    public sealed class Table
    {
        public WasmType Type;
        public uint[] Init; // func index per slot after active element segments, uint.MaxValue = null
    }

    public sealed class Body
    {
        public List<WasmType> Locals = new List<WasmType>();
        public byte[] Code;
    }

    public sealed class DataSegment
    {
        public uint Offset;
        public byte[] Bytes;
        public uint Index;  // segment index, counting passive ones
        public string Name; // from the name section (".rodata", ".data"); null when stripped
    }

    /// <summary>Minimal WebAssembly MVP binary parser (plus bulk-memory/sign-ext/sat encodings).</summary>
    public sealed class WasmModule
    {
        public List<FuncType> Types = new List<FuncType>();
        public List<Import> Imports = new List<Import>();
        public List<uint> FuncTypeIndices = new List<uint>(); // defined functions only
        public List<Global> Globals = new List<Global>();
        public Dictionary<string, uint> FuncExports = new Dictionary<string, uint>();
        public List<Body> Bodies = new List<Body>();
        public List<DataSegment> Data = new List<DataSegment>();
        public List<Table> Tables = new List<Table>();
        public List<uint[]> Elems = new List<uint[]>(); // functions of every element segment, by segment index (table.init)
        public HashSet<uint> GlobalRefFuncs = new HashSet<uint>(); // ref.func in global initialisers
        public uint MemoryPages;
        public bool HasMemory;
        public int ImportedFuncCount;
        /// <summary>.rodata this big or bigger is read from the ROM texture instead of registers; 0 = never.</summary>
        public uint MinRomBytes = 1024;

        public FuncType TypeOfFunc(uint funcIndex)
        {
            if (funcIndex < ImportedFuncCount)
            {
                int n = 0;
                foreach (var imp in Imports)
                    if (imp.Kind == 0 && n++ == funcIndex) return Types[(int)imp.TypeIndex];
            }
            return Types[(int)FuncTypeIndices[(int)(funcIndex - ImportedFuncCount)]];
        }

        public Import FuncImport(uint funcIndex)
        {
            int n = 0;
            foreach (var imp in Imports)
                if (imp.Kind == 0 && n++ == funcIndex) return imp;
            return null;
        }

        public static WasmModule Parse(byte[] bytes)
        {
            var r = new Reader(bytes, 0);
            if (r.U32Fixed() != 0x6D736100) throw new FormatException("Not a wasm binary (bad magic).");
            if (r.U32Fixed() != 1) throw new FormatException("Unsupported wasm version.");
            var m = new WasmModule();

            while (!r.End)
            {
                byte id = r.Byte();
                uint size = r.U32();
                int end = r.Pos + (int)size;
                switch (id)
                {
                    case 1:
                        for (uint n = r.U32(), i = 0; i < n; i++)
                        {
                            if (r.Byte() != 0x60) throw new FormatException("Bad func type.");
                            m.Types.Add(new FuncType { Params = r.ValTypes(), Results = r.ValTypes() });
                        }
                        break;
                    case 2:
                        for (uint n = r.U32(), i = 0; i < n; i++)
                        {
                            var imp = new Import { Module = r.Name(), Name = r.Name(), Kind = r.Byte() };
                            switch (imp.Kind)
                            {
                                case 0: imp.TypeIndex = r.U32(); m.ImportedFuncCount++; break;
                                case 1: m.AddTable(r); break;
                                case 2: m.HasMemory = true; m.MemoryPages = r.Limits(out _); break;
                                case 3: throw new NotSupportedException($"Imported global '{imp.Module}.{imp.Name}' is not supported.");
                            }
                            m.Imports.Add(imp);
                        }
                        break;
                    case 3:
                        for (uint n = r.U32(), i = 0; i < n; i++) m.FuncTypeIndices.Add(r.U32());
                        break;
                    case 4:
                        for (uint n = r.U32(), i = 0; i < n; i++) m.AddTable(r);
                        break;
                    case 5:
                        for (uint n = r.U32(), i = 0; i < n; i++) { m.HasMemory = true; m.MemoryPages = r.Limits(out _); }
                        break;
                    case 6:
                        for (uint n = r.U32(), i = 0; i < n; i++)
                        {
                            var g = new Global { Type = (WasmType)r.Byte(), Mutable = r.Byte() != 0 };
                            g.Init = r.ConstExpr(out g.InitHi);
                            if (g.Type == WasmType.FuncRef && g.Init != uint.MaxValue) m.GlobalRefFuncs.Add((uint)g.Init);
                            m.Globals.Add(g);
                        }
                        break;
                    case 7:
                        for (uint n = r.U32(), i = 0; i < n; i++)
                        {
                            string name = r.Name(); byte kind = r.Byte(); uint idx = r.U32();
                            if (kind == 0) m.FuncExports[name] = idx;
                        }
                        break;
                    case 9:
                        for (uint n = r.U32(), i = 0; i < n; i++)
                        {
                            uint flags = r.U32();
                            bool active = (flags & 1) == 0;
                            bool exprs = (flags & 4) != 0;
                            uint offset = 0, table = 0;
                            if (active)
                            {
                                if ((flags & 2) != 0) table = r.U32();
                                offset = (uint)r.ConstExpr();
                            }
                            if ((flags & 3) != 0) r.Byte(); // elemkind / reftype
                            var funcs = new uint[r.U32()];
                            for (uint k = 0; k < funcs.Length; k++)
                            {
                                uint f;
                                if (exprs)
                                {
                                    byte op = r.Byte();
                                    if (op == 0xD2) f = r.U32();       // ref.func
                                    else if (op == 0xD0) { r.Byte(); f = uint.MaxValue; } // ref.null
                                    else throw new NotSupportedException("Unsupported element expression.");
                                    if (r.Byte() != 0x0B) throw new FormatException("Bad element expression.");
                                }
                                else f = r.U32();
                                funcs[k] = f;
                                if (active && table < m.Tables.Count && offset + k < m.Tables[(int)table].Init.Length) m.Tables[(int)table].Init[offset + k] = f;
                            }
                            m.Elems.Add(funcs);
                        }
                        break;
                    case 10:
                        for (uint n = r.U32(), i = 0; i < n; i++)
                        {
                            uint bodySize = r.U32();
                            int bodyEnd = r.Pos + (int)bodySize;
                            var b = new Body();
                            for (uint gn = r.U32(), g = 0; g < gn; g++)
                            {
                                uint count = r.U32(); var t = (WasmType)r.Byte();
                                for (uint c = 0; c < count; c++) b.Locals.Add(t);
                            }
                            b.Code = r.Slice(bodyEnd - r.Pos);
                            m.Bodies.Add(b);
                        }
                        break;
                    case 11:
                        for (uint n = r.U32(), i = 0; i < n; i++)
                        {
                            uint flags = r.U32();
                            if (flags == 1) { r.Slice((int)r.U32()); continue; } // passive: only reachable via memory.init
                            if (flags == 2) r.U32();
                            uint off = (uint)r.ConstExpr();
                            m.Data.Add(new DataSegment { Offset = off, Bytes = r.Slice((int)r.U32()), Index = i });
                        }
                        break;
                    case 0:
                        if (r.Name() == "name") ReadDataNames(r, end, m);
                        break;
                    // 8 start, 12 datacount: ignored
                }
                r.Pos = end;
            }

            return m;
        }

        /// <summary>Data segment names (name section, subsection 9): tells read-only .rodata apart from .data.</summary>
        static void ReadDataNames(Reader r, int end, WasmModule m)
        {
            while (r.Pos < end)
            {
                byte sub = r.Byte();
                int subEnd = (int)r.U32() + r.Pos;
                if (sub == 9)
                    for (uint n = r.U32(), i = 0; i < n; i++)
                    {
                        uint idx = r.U32(); string name = r.Name();
                        foreach (var d in m.Data) if (d.Index == idx) d.Name = name;
                    }
                r.Pos = subEnd;
            }
        }

        void AddTable(Reader r)
        {
            var t = new Table { Type = (WasmType)r.Byte() };
            t.Init = new uint[r.Limits(out _)];
            for (int i = 0; i < t.Init.Length; i++) t.Init[i] = uint.MaxValue;
            Tables.Add(t);
        }
    }

    internal sealed class Reader
    {
        readonly byte[] _b;
        public int Pos;
        public Reader(byte[] b, int pos) { _b = b; Pos = pos; }
        public bool End => Pos >= _b.Length;
        public byte Byte() => _b[Pos++];

        public uint U32Fixed() { uint v = BitConverter.ToUInt32(_b, Pos); Pos += 4; return v; }
        public ulong U64Fixed() { ulong v = BitConverter.ToUInt64(_b, Pos); Pos += 8; return v; }

        public uint U32()
        {
            uint result = 0; int shift = 0; byte b;
            do { b = _b[Pos++]; result |= (uint)(b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0);
            return result;
        }

        public long S64()
        {
            long result = 0; int shift = 0; byte b;
            do { b = _b[Pos++]; result |= (long)(b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0);
            if (shift < 64 && (b & 0x40) != 0) result |= -1L << shift;
            return result;
        }

        public int S32() => (int)S64();

        public byte[] Slice(int n) { var s = new byte[n]; Array.Copy(_b, Pos, s, 0, n); Pos += n; return s; }
        public string Name() => Encoding.UTF8.GetString(Slice((int)U32()));

        public WasmType[] ValTypes()
        {
            var t = new WasmType[U32()];
            for (int i = 0; i < t.Length; i++) t[i] = (WasmType)Byte();
            return t;
        }

        public uint Limits(out uint? max)
        {
            byte flags = Byte();
            uint min = U32();
            max = (flags & 1) != 0 ? U32() : (uint?)null;
            return min;
        }

        /// <summary>Evaluates a constant init expression to raw bits.</summary>
        public ulong ConstExpr() => ConstExpr(out _);

        public ulong ConstExpr(out ulong hi)
        {
            byte op = Byte(); ulong v; hi = 0;
            switch (op)
            {
                case 0x41: v = (uint)S32(); break;
                case 0x42: v = (ulong)S64(); break;
                case 0x43: v = U32Fixed(); break;
                case 0x44: v = U64Fixed(); break;
                case 0xD0: Byte(); v = uint.MaxValue; break; // ref.null
                case 0xFD:
                    if (U32() != 12) throw new NotSupportedException("Unsupported v128 constant expression.");
                    v = U64Fixed(); hi = U64Fixed(); break;
                case 0xD2: v = U32(); break;
                default: throw new NotSupportedException($"Unsupported constant expression opcode 0x{op:X2}.");
            }
            if (Byte() != 0x0B) throw new NotSupportedException("Only single-instruction constant expressions are supported.");
            return v;
        }
    }
}
