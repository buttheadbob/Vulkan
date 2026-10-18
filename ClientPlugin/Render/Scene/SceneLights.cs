using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ClientPlugin.Render.Vulkan;
using VRageMath;
using VRageRender.Messages;

namespace ClientPlugin.Render.Scene;

// Must match Shaders/Common/Environment.slang (64 bytes)
[StructLayout(LayoutKind.Sequential)]
internal struct GpuLight
{
    public Vector4 PositionRange; // xyz: camera-relative position, w: range
    public Vector4 ColorFalloff;  // rgb: linear color (intensity included), w: falloff
    public Vector4 Params;        // x: gloss factor, y: diffuse factor, z: aperture cos, w: 1 = spot
    public Vector4 Direction;     // xyz: spot direction
}

// Dynamic lights from CreateRenderLight/UpdateRenderLight. The game's light component sends the
// linear color with intensity already multiplied in; falloff is converted with the same formula as
// MyLightComponent. MaxLights keeps the forward loop bounded; stock rejects far more lights per tile.
internal sealed class SceneLights
{
    public const int MaxLights = 64;

    private readonly Dictionary<uint, UpdateRenderLightData> lights = new();

    public int LastCount { get; private set; }

    public void Create(uint id) => lights.TryAdd(id, default);

    public void Update(UpdateRenderLightData data) => lights[data.ID] = data;

    public (ulong Address, uint Count) Build(GpuContext context, Vector3D cameraPosition)
    {
        var candidates = 0;
        foreach (var data in lights.Values)
        {
            if (data.PointLightOn && data.PointLight.Range > 0.01f)
                candidates++;
            if (data.SpotLightOn && data.SpotLight.Light.Range > 0.01f)
                candidates++;
        }

        var count = Math.Min(candidates, MaxLights);
        LastCount = count;
        if (count == 0)
            return (0, 0);

        var allocation = context.Transient.Allocate<GpuLight>(count);
        var output = allocation.AsSpan<GpuLight>(count);
        var write = 0;

        foreach (var data in lights.Values)
        {
            if (write >= count)
                break;

            var position = (Vector3)(data.Position - cameraPosition);
            if (data.PointLightOn && data.PointLight.Range > 0.01f)
            {
                var light = data.PointLight;
                var falloff = ConvertFalloff(light.Falloff);
                output[write++] = new GpuLight
                {
                    PositionRange = new Vector4(position, light.Range),
                    ColorFalloff = new Vector4(light.Color, falloff),
                    Params = new Vector4(light.GlossFactor, light.DiffuseFactor, 0f, 0f),
                    Direction = Vector4.Zero,
                };
            }

            if (write >= count)
                break;

            if (data.SpotLightOn && data.SpotLight.Light.Range > 0.01f)
            {
                var light = data.SpotLight.Light;
                var falloff = ConvertFalloff(light.Falloff);
                output[write++] = new GpuLight
                {
                    PositionRange = new Vector4(position, light.Range),
                    ColorFalloff = new Vector4(light.Color, falloff),
                    Params = new Vector4(light.GlossFactor, light.DiffuseFactor,
                        data.SpotLight.ApertureCos, 1f),
                    Direction = new Vector4(data.SpotLight.Direction, 0f),
                };
            }
        }

        LastCount = write;
        return (allocation.Address, (uint)write);
    }

    // MyLightComponent.UpdateData: falloff = 1 / (0.001 + (falloff / 1.5)^3)
    private static float ConvertFalloff(float falloff)
    {
        var value = falloff / 1.5f;
        return 1f / (0.001f + value * value * value);
    }
}
