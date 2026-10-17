// Global bindless descriptor set (set 0), shared by every pipeline.
// Must match ClientPlugin/Render/Vulkan/BindlessDescriptors.cs.
module Bindless;

[[vk::binding(0, 0)]] public Texture2D textures[];
[[vk::binding(1, 0)]] public SamplerState samplers[];

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
