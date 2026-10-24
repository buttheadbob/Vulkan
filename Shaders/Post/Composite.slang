// Brings the HDR scene to the swapchain: fullscreen triangle, exposure and tonemapping.

import Common.Bindless;
import Common.Environment;

struct PushConstants
{
    uint sceneTexture;      // Bindless slot of the HDR scene color
    uint oitAccum;          // Bindless slot of the OIT accumulation target
    uint oitReveal;         // Bindless slot of the OIT revealage target
    uint exposureTexture;   // Bindless slot of the 1x1 auto-exposure target
    uint bloomTexture;      // Bindless slot of the blurred HDR bloom target
    uint shadowMap;         // Bindless slot of the sun shadow map
    uint shadowDebug;       // 1: show the raw shadow map instead of the scene
    uint depthTexture;      // Bindless slot of the reversed-Z scene depth
    uint gbufferTexture;    // Bindless slot of the normal/gloss buffer
    uint gbuffer2Texture;   // Bindless slot of the F0/AO buffer
    float whitePoint;       // Hable tonemapping white point
    uint pad;
    FrameConstants* frame;  // Camera matrices for screen-space reflection
};

[[vk::push_constant]] ConstantBuffer<PushConstants> push;

struct VertexOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
VertexOutput vertexMain(uint vertexId : SV_VertexID)
{
    // Triangle covering the screen: (0,0) (2,0) (0,2) in uv
    float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
    VertexOutput output;
    output.position = float4(uv * 2 - 1, 0, 1);
    output.uv = uv;
    return output;
}

// Shipped Postprocess/Tonemapping/Filters.hlsli: ToneMapFilmic_Hable
float hableCurve(float x)
{
    const float A = 0.15;
    const float B = 0.50;
    const float C = 0.10;
    const float D = 0.20;
    const float E = 0.02;
    const float F = 0.30;
    return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
}

float3 hableCurve3(float3 x)
{
    const float A = 0.15;
    const float B = 0.50;
    const float C = 0.10;
    const float D = 0.20;
    const float E = 0.02;
    const float F = 0.30;
    return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
}

float3 hable(float3 x, float whitePoint)
{
    float whiteScale = 1.0 / hableCurve(max(whitePoint, 0.01));
    return hableCurve3(x) * whiteScale;
}

float3 reconstructPosition(float2 uv, float depth, FrameConstants frame)
{
    float4 ndc = float4(uv * 2.0 - 1.0, depth, 1.0);
    float4 world = float4(dot(ndc, frame.invViewProj0),
                          dot(ndc, frame.invViewProj1),
                          dot(ndc, frame.invViewProj2),
                          dot(ndc, frame.invViewProj3));
    return world.xyz / max(world.w, 1e-6);
}


// Screen-space reflection: marches the reflected ray through the depth buffer and samples the
// opaque scene color. This supplies block/ship reflections that a skybox-only IBL cannot.
float3 screenSpaceReflection(float2 uv, float3 viewPosition, float3 normal, float gloss, float3 f0,
    out float hit)
{
    hit = 0.0;
    if (renderSettings[0].post.z < -0.5)
        return 0.0;
    // Only trace surfaces that actually reflect: opaque dielectrics (f0 ~= 0.04) have a negligible
    // specular lobe, and tracing every glossy painted block was both wrong and expensive. Glass
    // uses the separate transparent SSR path.
    float metallic = smoothstep(0.15, 0.5, max(f0.r, max(f0.g, f0.b)));
    if (gloss < 0.55 || metallic < 0.05 || length(viewPosition) < 0.1)
        return 0.0;

    float3 start = viewPosition + normal * 0.05;
    float3 ray = normalize(reflect(normalize(viewPosition), normal));
    float previousError = 1e9;

    int steps = 16 + int(saturate(renderSettings[0].post.w)) * 12;
    [unroll]
    for (int i = 1; i <= 40; i++)
    {
        if (i > steps)
            break;
        float t = float(i) * 0.25;
        float3 p = start + ray * t;
        float4 clip = float4(dot(float4(p, 1.0), push.frame.viewProj0),
                             dot(float4(p, 1.0), push.frame.viewProj1),
                             dot(float4(p, 1.0), push.frame.viewProj2),
                             dot(float4(p, 1.0), push.frame.viewProj3));
        if (clip.w <= 1e-5)
            break;

        float2 sampleUv = clip.xy / clip.w * 0.5 + 0.5;
        if (any(sampleUv < 0.0) || any(sampleUv > 1.0))
            break;

        float storedDepth = sampleTexture(push.depthTexture, SamplerSlot.PointClamp, sampleUv).r;
        if (storedDepth <= 0.00001)
            break;

        float3 surface = reconstructPosition(sampleUv, storedDepth, *push.frame);
        float error = length(surface - p);
        // Tight tolerance: 0.8 m accepted hits on the wrong block when reflecting nearby ship parts.
        float tolerance = 0.12 + t * 0.012;
        if (error < tolerance)
        {
            float3 reflected = sampleTexture(push.sceneTexture, SamplerSlot.LinearClamp, sampleUv).rgb;
            float strength = saturate((gloss - 0.45) / 0.55);
            float weight = saturate(strength * metallic);
            float2 edge = smoothstep(0.0, 0.08, uv) * smoothstep(0.0, 0.08, 1.0 - uv);
            hit = edge.x * edge.y * weight;
            return reflected;
        }

        // Stop once the ray passes behind the depth surface between two samples.
        if (error > previousError * 2.5 && t > 1.0)
            break;
        previousError = error;
    }

    return 0.0;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target0
{
    // Validation view: show the raw sun shadow map (reverse-Z depth) stretched over the screen.
    // Empty texels are 0; casters are brighter towards the near plane.
    if (push.shadowDebug == 1)
    {
        float depth = sampleTexture(push.shadowMap, SamplerSlot.PointClamp, input.uv).r;
        float value = saturate(depth * 4.0);
        return float4(value.xxx, 1.0);
    }

    if (push.shadowDebug == 2 || push.shadowDebug == 3)
    {
        float d = sampleTexture(push.depthTexture, SamplerSlot.PointClamp, input.uv).r;
        float4 gb = sampleTexture(push.gbufferTexture, SamplerSlot.PointClamp, input.uv);
        if (d <= 0.00001 || dot(gb.xyz, gb.xyz) <= 0.01)
            return float4(0.0, 0.0, 0.0, 1.0);
        float3 vp = reconstructPosition(input.uv, d, *push.frame);
        float hit;
        float4 gb2Debug = sampleTexture(push.gbuffer2Texture, SamplerSlot.PointClamp, input.uv);
        float3 r = screenSpaceReflection(input.uv, vp, normalize(gb.xyz * 2.0 - 1.0), gb.w, gb2Debug.rgb, hit);
        if (push.shadowDebug == 3)
            return float4(saturate(r * 8.0), 1.0);
        return float4(hit > 0.5 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0), 1.0);
    }

    float4 sceneSample = sampleTexture(push.sceneTexture, SamplerSlot.LinearClamp, input.uv);
    float3 color = sceneSample.rgb;

    float depth = sampleTexture(push.depthTexture, SamplerSlot.PointClamp, input.uv).r;
    float4 gbuffer = sampleTexture(push.gbufferTexture, SamplerSlot.PointClamp, input.uv);
    float3 normal = normalize(gbuffer.xyz * 2.0 - 1.0);
    float3 viewPosition = 0.0;
    if (depth > 0.00001 && dot(gbuffer.xyz, gbuffer.xyz) > 0.01)
    {
        viewPosition = reconstructPosition(input.uv, depth, *push.frame);
        float4 gbuffer2 = sampleTexture(push.gbuffer2Texture, SamplerSlot.PointClamp, input.uv);
        float3 f0 = gbuffer2.rgb;
        float ao = gbuffer2.a;
        float gloss = gbuffer.w;
        float3 toCamera = normalize(-viewPosition);
        // Two-sided opaque surfaces must reflect the hemisphere they actually face.
        if (dot(normal, toCamera) < 0.0)
            normal = -normal;
        float nov = saturate(dot(normal, toCamera));
        float3 reflection = reflect(-toCamera, normal);
        // The probe faces are captured with the standard Vulkan cubemap orientation, so no axis
        // correction is applied here.
        float maxMip = max(push.frame.skyboxParams.y - 1.0, 0.0);
        // Specular antialiasing: normal-map/detail edges produce wildly different reflection
        // directions per pixel, which reads as glitter along block edges. Bias the lookup wider
        // where the normal changes quickly.
        float normalVariance = length(ddx(normal)) + length(ddy(normal));
        // The receiver distance selects the reflection quality tier smoothly. Near surfaces use
        // low mips; far surfaces blend into high mips of the same probe (no sky-only far cube).
        float mip = min(reflectionMipForDistance(*push.frame, gloss, length(viewPosition)) +
                        saturate(normalVariance * 6.0) * maxMip * 0.5, maxMip);
        float3 prefiltered = iblColor(*push.frame, reflection, mip);
        float2 brdf = sampleTexture(push.frame.brdfLut, SamplerSlot.LinearClamp,
                                    float2(gloss, nov)).rg;
        // Stock AmbientSpecular applies to all opaque materials, not only metals. For painted
        // dielectrics F0 ~= 0.04 this stays subtle but picks up the colour of the ground/planet
        // below, which is exactly the green/white underside lighting we want.
        float3 ibl = prefiltered * (f0 * brdf.x + brdf.y) * push.frame.skyboxParams.w * ao *
                     renderSettings[0].tuning1.z;
        // SSR remains a metal/high-gloss-only optional sharpening pass; it is off by default.
        float metallic = smoothstep(0.15, 0.5, max(f0.r, max(f0.g, f0.b)));
        float ssrMask = metallic * smoothstep(0.0, 0.45, saturate(sceneSample.a));
        float hit;
        float3 ssr = screenSpaceReflection(input.uv, viewPosition, normal, gloss, f0, hit) *
                     (renderSettings[0].tuning1.z * ssrMask);
        hit *= ssrMask;
        color += lerp(ibl, ssr, hit);
    }

    // Order-independent transparency: accum holds premultiplied color/alpha sums, reveal is the
    // remaining visibility of the opaque scene.
    float4 accum = sampleTexture(push.oitAccum, SamplerSlot.LinearClamp, input.uv);
    float reveal = saturate(sampleTexture(push.oitReveal, SamplerSlot.LinearClamp, input.uv).r);
    float3 oitColor = accum.rgb / max(accum.a, 1e-4);
    float oitAlpha = 1.0 - reveal;
    color = color * (1.0 - oitAlpha) + oitColor * oitAlpha;

    float3 bloom = sampleTexture(push.bloomTexture, SamplerSlot.LinearClamp, input.uv).rgb;
    color += bloom * renderSettings[0].post.y;

    float exposure = sampleTexture(push.exposureTexture, SamplerSlot.PointClamp, float2(0.5, 0.5)).r;
    return float4(saturate(hable(color * exposure, push.whitePoint)), 1);
}
