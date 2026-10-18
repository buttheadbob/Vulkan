// Global bindless descriptor set (set 0), shared by every pipeline.
// Must match ClientPlugin/Render/Vulkan/BindlessDescriptors.cs.
module Bindless;

[[vk::binding(0, 0)]] public Texture2D textures[];
[[vk::binding(1, 0)]] public SamplerState samplers[];
[[vk::binding(2, 0)]] public TextureCube cubeTextures[];

// Persistent GPU-resident user settings (one element), updated only when the CPU settings change.
// Must match ClientPlugin/Render/Scene/GpuRenderSettings.cs (std430, 48 bytes).
public struct RenderSettings
{
    public float4 tuning0; // x: sun, y: ambient, z: direct specular, w: metalness
    public float4 tuning1; // x: emissive, y: dynamic lights, z: IBL reflection, w: screen
    public float4 post;    // x: exposure, y: bloom strength
};
[[vk::binding(3, 0)]] public StructuredBuffer<RenderSettings> renderSettings;

// Fixed sampler slots, see BindlessDescriptors.SamplerSlot
public enum SamplerSlot : uint
{
    LinearClamp = 0,
    LinearWrap = 1,
    PointClamp = 2,
    AnisotropicWrap = 3,
}

// Texture slots that always exist, see BindlessDescriptors.TextureSlot
public enum TextureSlot : uint
{
    White = 0,
    Black = 1,
    Missing = 2,
    Transparent = 3,
}

public float4 sampleTexture(uint texture, SamplerSlot sampler, float2 uv)
{
    return textures[NonUniformResourceIndex(texture)].Sample(samplers[uint(sampler)], uv);
}

public float4 sampleCube(uint texture, SamplerSlot sampler, float3 direction, float level)
{
    return cubeTextures[NonUniformResourceIndex(texture)].SampleLevel(samplers[uint(sampler)], direction, level);
}
