using System; using System.IO; using System.Runtime.InteropServices; using Vortice.Direct3D; using Vortice.Direct3D11; using Vortice.DXGI;
// usage: gpu <cs.cso | cs.hlsl> <count> <mode> [hi] -> prints results as JSON array of lo (or lo,hi pairs with a 4th arg)
// A .hlsl is compiled with the system d3dcompiler_47.dll (same compiler as fxc), entry "cs", cs_5_0.
class P {
  [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
  static extern int D3DCompile(byte[] src, IntPtr size, string name, IntPtr defines, IntPtr include, string entry, string target, uint f1, uint f2, out IntPtr code, out IntPtr errors);

  static byte[] Compile(string path) {
    var src = File.ReadAllBytes(path);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    int hr = D3DCompile(src, (IntPtr)src.Length, path, IntPtr.Zero, (IntPtr)1 /* standard include */, "cs", "cs_5_0", 1u << 14 /* OPTIMIZATION_LEVEL3 */, 0, out var codePtr, out var errPtr);
    if (errPtr != IntPtr.Zero) Console.Error.WriteLine(new Blob(errPtr).AsString());
    if (hr < 0) throw new Exception($"D3DCompile failed 0x{hr:X8}");
    Console.Error.WriteLine($"compiled {Path.GetFileName(path)} in {sw.Elapsed.TotalSeconds:F1}s");
    return new Blob(codePtr).AsBytes();
  }

  static unsafe int Main(string[] a) {
  int n = int.Parse(a[1]); int mode = int.Parse(a[2]);
  var bytecode = a[0].EndsWith(".hlsl") ? Compile(a[0]) : File.ReadAllBytes(a[0]);
  D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None, new[]{FeatureLevel.Level_11_0}, out ID3D11Device dev, out ID3D11DeviceContext ctx).CheckError();
  var cs = dev.CreateComputeShader(bytecode);
  var ob = dev.CreateBuffer(new BufferDescription((uint)n*16, BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 16));
  var uav = dev.CreateUnorderedAccessView(ob, new UnorderedAccessViewDescription(ob, Format.Unknown, 0, (uint)n));
  var cb = dev.CreateBuffer(new int[]{ n, mode, 0, 0 }, BindFlags.ConstantBuffer);
  var st = dev.CreateBuffer(new BufferDescription((uint)n*16, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, ResourceOptionFlags.BufferStructured, 16));
  ctx.CSSetShader(cs); ctx.CSSetConstantBuffer(0, cb); ctx.CSSetUnorderedAccessView(0, uav);
  var sw = System.Diagnostics.Stopwatch.StartNew(); ctx.Dispatch((uint)(n+63)/64, 1, 1); ctx.CopyResource(st, ob);
  var map = ctx.Map(st, 0, MapMode.Read); Console.Error.WriteLine($"gpu {sw.Elapsed.TotalMilliseconds:F1} ms"); var r = new int[n*4]; Marshal.Copy(map.DataPointer, r, 0, n*4); ctx.Unmap(st, 0);
  var sb = new System.Text.StringBuilder("["); for (int i=0;i<n;i++){ if(i>0) sb.Append(','); sb.Append(r[i*4]); if (a.Length > 3) { sb.Append(","); sb.Append(r[i*4+1]); } } sb.Append("]");
  int traps=0; for (int i=0;i<n;i++) traps += r[i*4+2]; Console.Error.WriteLine($"traps={traps}");
  Console.WriteLine(sb); return 0; } }
