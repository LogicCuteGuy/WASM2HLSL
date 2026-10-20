// ChromaFx's mixdown: one pixel per float of chroma.wasm's input, with every source row summed into source row 0.
// The FIR is linear, so filtering the sum equals summing the filtered sources, and `process` then runs on a single row:
// its GPU cost no longer grows with the number of sources. Layout must match ChromaFx.cs / chroma.rs:
// [2 * 4096 taps][output history, 2 * 8192][source 0 L, R: 2 * 8192][source 1 L, R]...  (RFloat, 4096 wide)
Shader "Hidden/WASM2HLSL/ChromaMix"
{
    Properties
    {
        _MainTex ("Raw input (RFloat)", 2D) = "black" {}
        _Rows ("Source rows", Integer) = 1
        _Gain ("Source gain (1 / send volume)", Float) = 1
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"
            Texture2D<float> _MainTex;
            int _Rows;
            float _Gain;
            float _Gains[16]; // per source row: the Juno voice's velocity * release gain (1 for other sources)
            static const uint Width = 4096u, Src = 2u * 4096u + 2u * 8192u, Row = 2u * 8192u;

            float frag(v2f_img i) : SV_Target
            {
                uint2 p = (uint2)i.pos.xy;
                uint id = p.y * Width + p.x;
                if (id < Src) return _MainTex.Load(int3(p, 0));
                float s = 0.0;
                for (int r = 0; r < _Rows; r++)
                {
                    uint k = id + (uint)r * Row;
                    s += _MainTex.Load(int3(k % Width, k / Width, 0)) * _Gains[r];
                }
                return clamp(s * _Gain, -8.0, 8.0); // the clamp only guards against a wrong gain
            }
            ENDCG
        }
    }
}
