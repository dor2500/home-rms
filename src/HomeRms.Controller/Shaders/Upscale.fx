// Catmull-Rom (sharp bicubic) upscaler for the remote-desktop picture.
//
// WPF hands this shader the Image as rendered on screen. The Image is drawn with
// nearest-neighbour scaling, so every source pixel survives as a solid block; we read
// each source pixel back from the centre of its block and rebuild the picture with a
// 4x4 Catmull-Rom filter, which keeps text and edges much crisper than bilinear.
//
// Compile: fxc /T ps_3_0 /E main /O3 /Fo Upscale.ps Upscale.fx

sampler2D input : register(s0);

// xy = remote frame size in pixels
float4 sourceSize : register(c0);

float4 Fetch(float2 texel)
{
    texel = clamp(texel, float2(0, 0), sourceSize.xy - 1);
    float2 uv = (texel + 0.5) / sourceSize.xy;
    return tex2Dlod(input, float4(uv, 0, 0));
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 pos = uv * sourceSize.xy - 0.5;
    float2 base = floor(pos);
    float2 f = pos - base;

    // Catmull-Rom weights
    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);

    float4 row0 = Fetch(base + float2(-1, -1)) * w0.x + Fetch(base + float2(0, -1)) * w1.x + Fetch(base + float2(1, -1)) * w2.x + Fetch(base + float2(2, -1)) * w3.x;
    float4 row1 = Fetch(base + float2(-1,  0)) * w0.x + Fetch(base + float2(0,  0)) * w1.x + Fetch(base + float2(1,  0)) * w2.x + Fetch(base + float2(2,  0)) * w3.x;
    float4 row2 = Fetch(base + float2(-1,  1)) * w0.x + Fetch(base + float2(0,  1)) * w1.x + Fetch(base + float2(1,  1)) * w2.x + Fetch(base + float2(2,  1)) * w3.x;
    float4 row3 = Fetch(base + float2(-1,  2)) * w0.x + Fetch(base + float2(0,  2)) * w1.x + Fetch(base + float2(1,  2)) * w2.x + Fetch(base + float2(2,  2)) * w3.x;

    float4 c = row0 * w0.y + row1 * w1.y + row2 * w2.y + row3 * w3.y;
    c = saturate(c);
    c.rgb = min(c.rgb, c.a); // keep premultiplied alpha valid
    return c;
}
