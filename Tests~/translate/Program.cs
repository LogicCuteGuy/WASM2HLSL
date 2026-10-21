using System; using System.IO; using LogicCuteGuy.Wasm2Hlsl;
class P { static int Main(string[] a) {
  var m = WasmModule.Parse(File.ReadAllBytes(a[0]));
  File.WriteAllText(a[1], WasmToHlsl.TranslateLibrary(m, Path.GetFileName(a[0])));
  foreach (var e in m.FuncExports.Keys) File.WriteAllText(Path.ChangeExtension(a[1], null) + "_" + e + ".shader", WasmToHlsl.GenerateShader(m, e, "WASM/test/" + e, Path.GetFileName(a[1])));
  var rom = WasmToHlsl.RomBytes(m, out int rw, out int rh);
  if (rom != null) File.WriteAllBytes(Path.ChangeExtension(a[1], ".rom"), rom); else File.Delete(Path.ChangeExtension(a[1], ".rom"));
  Console.WriteLine($"rom={WasmToHlsl.RomByteCount(m)}" + (rom != null ? $" ({rw}x{rh})" : ""));
  Console.WriteLine($"exports: {string.Join(",", m.FuncExports.Keys)} mem={WasmToHlsl.MemoryBytes(m)} funcs={m.Bodies.Count}");
  return 0; } }
