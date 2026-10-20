using System; using System.IO; using LogicCuteGuy.Wasm2Hlsl;
class P { static int Main(string[] a) {
  var m = WasmModule.Parse(File.ReadAllBytes(a[0]));
  File.WriteAllText(a[1], WasmToHlsl.TranslateLibrary(m, Path.GetFileName(a[0])));
  foreach (var e in m.FuncExports.Keys) File.WriteAllText(Path.ChangeExtension(a[1], null) + "_" + e + ".shader", WasmToHlsl.GenerateShader(m, e, "WASM/test/" + e, Path.GetFileName(a[1])));
  Console.WriteLine($"exports: {string.Join(",", m.FuncExports.Keys)} mem={WasmToHlsl.MemoryBytes(m)} funcs={m.Bodies.Count}");
  return 0; } }
