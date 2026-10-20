// Shows a WasmGpuRunner's R32 int render texture directly: no readback, no per-pixel Udon loop.
// Assign runner.target as _MainTex. Packed RGBA8: the export returns r | g << 8 | b << 16. Iteration palette: value / 256 iterations, -1 = black.
Shader "WASM2HLSL/Int Display"
{
    Properties
    {
        _MainTex ("Result (R32 int render texture)", 2D) = "black" {}
        [Enum(Packed RGBA8, 0, Iteration Palette, 1)] _Mode ("Mode", Float) = 0
        _Cycle ("Palette Cycle (iterations)", Float) = 48
        _Phase ("Palette Phase", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            Texture2D<int> _MainTex;
            float4 _MainTex_TexelSize;
            float _Mode, _Cycle, _Phase;

            fixed4 frag(v2f_img i) : SV_Target
            {
                int v = _MainTex.Load(int3(i.uv * _MainTex_TexelSize.zw, 0));
                float3 c;
                if (_Mode < 0.5) { uint u = asuint(v); c = float3(u & 255u, (u >> 8) & 255u, (u >> 16) & 255u) / 255.0; }
                else c = v < 0 ? 0 : 0.5 + 0.5 * cos(6.2831853 * (v / 256.0 / _Cycle + _Phase + float3(0.0, 0.15, 0.3)));
                #ifndef UNITY_COLORSPACE_GAMMA
                c = GammaToLinearSpace(c);
                #endif
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
}
