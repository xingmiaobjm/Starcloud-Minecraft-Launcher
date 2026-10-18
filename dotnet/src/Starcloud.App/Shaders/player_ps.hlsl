// 像素着色器：采样皮肤贴图，按面明暗系数调光，透明像素剔除（直通 alpha，由混合阶段处理）
Texture2D SkinTex : register(t0);
SamplerState Samp : register(s0);

struct PSIn  { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float shade : SHADE; };

float4 PS(PSIn i) : SV_Target
{
    float4 c = SkinTex.Sample(Samp, i.uv);
    clip(c.a - 0.02);
    return float4(c.rgb * i.shade, c.a);
}
