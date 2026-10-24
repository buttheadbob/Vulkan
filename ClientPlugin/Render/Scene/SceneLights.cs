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
    private readonly Dictionary<uint, uint> parents = new();

    public int LastCount { get; private set; }

    public void Create(uint id) => lights.TryAdd(id, default);

    public void SetParent(uint id, uint parentId) => parents[id] = parentId;

    public void Remove(uint id)
    {
        lights.Remove(id);
        parents.Remove(id);
    }

    public void Update(UpdateRenderLightData data) => lights[data.ID] = data;

    public (ulong Address, uint Count) Build(GpuContext context, Vector3D cameraPosition, SceneActors actors,
        float maxDistance, IReadOnlyList<GpuLight> extra = null)
    {
        var candidates = new List<(float Distance, GpuLight Light)>(lights.Count * 2 + (extra?.Count ?? 0));
        if (extra != null)
        {
            foreach (var light in extra)
            {
                var distance = new Vector3(light.PositionRange.X, light.PositionRange.Y, light.PositionRange.Z).Length();
                if (distance <= maxDistance)
                    candidates.Add((distance, light));
            }
        }
        foreach (var data in lights.Values)
        {
            var forward = data.SpotLight.Direction;
            if (forward.LengthSquared() < 1e-8f)
                forward = -Vector3.Forward;

            var parentWorld = MatrixD.Identity;
            if (parents.TryGetValue(data.ID, out var parentId) && parentId != 0)
            {
                var parent = actors.Find(parentId);
                if (parent != null)
                    parentWorld = actors.GetWorldMatrix(parent);
            }

            var pointLocal = data.Position + (Vector3D)forward * data.PointOffset;
            var pointPosition = parentWorld == MatrixD.Identity
                ? pointLocal
                : Vector3D.Transform(pointLocal, parentWorld);
            var spotPosition = parentWorld == MatrixD.Identity
                ? data.Position
                : Vector3D.Transform(data.Position, parentWorld);
            var spotDirection = parentWorld == MatrixD.Identity
                ? forward
                : Vector3.TransformNormal(forward, parentWorld);

            var pointDistance = (float)(pointPosition - cameraPosition).Length();
            var spotDistance = (float)(spotPosition - cameraPosition).Length();
            if (pointDistance > maxDistance && spotDistance > maxDistance)
                continue;

            if (data.PointLightOn && data.PointLight.Range > 0.01f && pointDistance <= maxDistance)
            {
                var light = data.PointLight;
                candidates.Add((pointDistance, new GpuLight
                {
                    PositionRange = new Vector4((Vector3)(pointPosition - cameraPosition), light.Range),
                    ColorFalloff = new Vector4(light.Color, ConvertFalloff(light.Falloff)),
                    Params = new Vector4(light.GlossFactor, light.DiffuseFactor, 0f, 0f),
                    Direction = Vector4.Zero,
                }));
            }

            if (data.SpotLightOn && data.SpotLight.Light.Range > 0.01f && spotDistance <= maxDistance)
            {
                var light = data.SpotLight.Light;
                candidates.Add((spotDistance, new GpuLight
                {
                    PositionRange = new Vector4((Vector3)(spotPosition - cameraPosition), light.Range),
                    ColorFalloff = new Vector4(light.Color, ConvertFalloff(light.Falloff)),
                    Params = new Vector4(light.GlossFactor, light.DiffuseFactor,
                        data.SpotLight.ApertureCos, 1f),
                    Direction = new Vector4(spotDirection, 0f),
                }));
            }
        }

        candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var count = Math.Min(candidates.Count, MaxLights);
        LastCount = count;
        if (count == 0)
            return (0, 0);

        var allocation = context.Transient.Allocate<GpuLight>(count);
        var output = allocation.AsSpan<GpuLight>(count);
        for (var i = 0; i < count; i++)
            output[i] = candidates[i].Light;
        return (allocation.Address, (uint)count);
    }

    // MyLightComponent.UpdateData: falloff = 1 / (0.001 + (falloff / 1.5)^3)
    internal static float ConvertFalloff(float falloff)
    {
        var value = falloff / 1.5f;
        return 1f / (0.001f + value * value * value);
    }
}
