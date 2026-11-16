// Contrast-adaptive sharpening (after AMD FidelityFX CAS, MIT licence).
// Runs on the final on-screen picture: sharpens soft edges strongly and already-sharp
// edges only a little, so upscaled text gets crisp without halos or noise.
//
// Compile: fxc /T ps_3_0 /E main /O3 /Fo Sharpen.ps Sharpen.fx

sampler2D input : register(s0);

// x = sharpness 0..1
float4 settings : register(c0);

// Filled by WPF (DdxUvDdyUvRegisterIndex = 1): size of one screen pixel in UV space
float4 ddxDdy : register(c1);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 px = float2(length(ddxDdy.xy), length(ddxDdy.zw));

    float4 e4 = tex2D(input, uv);
    float3 a = tex2D(input, uv + float2(-px.x, -px.y)).rgb;
    float3 b = tex2D(input, uv + float2(0, -px.y)).rgb;
    float3 c = tex2D(input, uv + float2(px.x, -px.y)).rgb;
    float3 d = tex2D(input, uv + float2(-px.x, 0)).rgb;
    float3 e = e4.rgb;
    float3 f = tex2D(input, uv + float2(px.x, 0)).rgb;
    float3 g = tex2D(input, uv + float2(-px.x, px.y)).rgb;
    float3 h = tex2D(input, uv + float2(0, px.y)).rgb;
    float3 i = tex2D(input, uv + float2(px.x, px.y)).rgb;

    // Soft min/max over the cross and the full 3x3 window
    float3 mn = min(min(min(d, e), min(f, b)), h);
    float3 mn2 = min(mn, min(min(a, c), min(g, i)));
    mn += mn2;
    float3 mx = max(max(max(d, e), max(f, b)), h);
    float3 mx2 = max(mx, max(max(a, c), max(g, i)));
    mx += mx2;

    // Amount of sharpening, lower where contrast is already high
    float3 amp = saturate(min(mn, 2.0 - mx) / max(mx, 0.0001));
    amp = sqrt(amp);

    float peak = -1.0 / lerp(8.0, 5.0, saturate(settings.x));
    float3 w = amp * peak;
    float3 result = saturate(((b + d + f + h) * w + e) / (1.0 + 4.0 * w));

    return float4(min(result, e4.a), e4.a);
}
