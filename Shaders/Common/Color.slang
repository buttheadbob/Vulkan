// Color space conversions
module Color;

// Exact sRGB transfer functions (IEC 61966-2-1)
public float3 srgbToLinear(float3 c)
{
    return select(c <= 0.04045, c / 12.92, pow((c + 0.055) / 1.055, 2.4));
}

public float3 linearToSrgb(float3 c)
{
    return select(c <= 0.0031308, c * 12.92, 1.055 * pow(c, 1.0 / 2.4) - 0.055);
}

// RGBA8 packed with R in the lowest byte (VRageMath.Color.PackedValue)
public float4 unpackRgba8(uint packed)
{
    return float4(packed & 0xFF, (packed >> 8) & 0xFF, (packed >> 16) & 0xFF, packed >> 24) / 255.0;
}
