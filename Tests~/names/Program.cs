// Checks SourceNames on one export per language: `dotnet run --project names`.
using System; using LogicCuteGuy.Wasm2Hlsl;
class P {
  static int fails;
  static void Check(string ext, string src, string export, string args, string doc) {
    var r = SourceNames.Parse(src, ext);
    string got = r.TryGetValue(export, out var f) ? string.Join(",", f.Params) + " | " + f.Doc : "(missing)";
    string want = args + " | " + doc;
    if (got != want) { fails++; Console.WriteLine($"FAIL {ext} {export}: got '{got}' want '{want}'"); } else Console.WriteLine($"ok   {ext} {export}");
  }
  static int Main(string[] a) {
    if (a.Length > 0) { foreach (var kv in SourceNames.Find(a[0], null)) Console.WriteLine($"{kv.Key}({string.Join(", ", kv.Value.Params)})  // {kv.Value.Doc.Replace('\n', ' ')}"); return 0; }
    Check(".rs", "/// Scales.\n/// Second line.\n#[no_mangle]\npub extern \"C\" fn main(id: i32, mut scale: f32 /* gain */) -> f32 { 0.0 }", "main", "id,scale", "Scales.\nSecond line.");
    Check(".rs", "#[export_name = \"tick\"]\npub unsafe extern \"C\" fn tick_impl(\n    id: i32, // pixel\n    t: f32,\n) {}", "tick", "id,t", "");
    Check(".c", "// Adds.\n__attribute__((export_name(\"add\")))\nint add_impl(int32_t id, const float *k, int arr[4]) {\n  if (id) { return 1; }\n  return 0;\n}", "add", "id,k,arr", "Adds.");
    Check(".c", "/** Void args. */\nint32_t frame(void) { while (1) { } }", "frame", "", "Void args.");
    Check(".zig", "/// Zig doc.\nexport fn mix(id: i32, comptime n: u32, x: f32) f32 { return x; }", "mix", "id,n,x", "Zig doc.");
    Check(".ts", "// AS doc.\nexport function blend(id: i32, a: f32 = 1.0): f32 { return a; }", "blend", "id,a", "AS doc.");
    Check(".go", "// Go doc.\n//export step\nfunc step(id, n int32, x float32) float32 { return x }\nfunc helper(a int32) {}", "step", "id,n,x", "Go doc.");
    Check(".go", "//go:wasmexport run\nfunc runImpl(id int32) int32 { return id }", "run", "id", "");
    var t = SourceNames.Parse("/// Doc. @traps\n/// @size 1024x512\n#[no_mangle]\npub extern \"C\" fn a(id: i32) {}\n// @size 100000\nint b(int id) { return 0; }", ".rs");
    var c = SourceNames.Parse("// @size 100000\nint b(int id) { return 0; }", ".c")["b"];
    if (t["a"].Width != 1024 || t["a"].Height != 512 || !t["a"].Traps || t["a"].Doc != "Doc." || c.Count != 100000 || c.Width != 0 || c.Traps)
      { fails++; Console.WriteLine("FAIL tags"); } else Console.WriteLine("ok   @size/@traps");
    if (SourceNames.Parse("func helper(a int32) {}", ".go").Count != 0) { fails++; Console.WriteLine("FAIL go: unexported func picked up"); }
    Console.WriteLine(fails == 0 ? "all ok" : fails + " failed");
    return fails;
  }
}
