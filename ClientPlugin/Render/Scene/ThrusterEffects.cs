using System;
using System.Collections.Generic;
using System.Diagnostics;
using ClientPlugin.Render.Scene;
using Sandbox.RenderDirect.ActorComponents;
using VRage.Game;
using VRage.Utils;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace ClientPlugin.Render.Scene;

// Emulates MyThrustFlameAnimator, which normally lives in the stock renderer's actor scene.
// The game sends the flame layout and strength through UpdateRenderComponent; this class turns
// them into the same camera-facing line/point billboards the stock animator adds each frame.
internal sealed class ThrusterEffects
{
    private const float ThrustIntensity = 10f;
    private const float ThrustLengthIntensity = 0.8f;
    private const float ThrustThickness = 1f;
    private const double UpdateInterval = 0.025;

    private sealed class State
    {
        public MyThrustFlameAnimator.FlameInfo[] Flames = [];
        public float GridScale = 1f;
        public Vector4 IdleColor;
        public Vector4 FullColor;
        public float FlameLengthScale = 1f;
        public MyStringId FlameLengthMaterial;
        public MyStringId FlamePointMaterial;

        public Vector3D LightPosition;
        public float Strength = -1f;
        public float StrengthPrevious = -1f;
        public float StrengthLerp;
        public double NextUpdate;
        public float RadiusRand = 1f;
        public float ThicknessRand = 1f;
        public float LengthRand;
        public Vector4 CurrentColor;
    }

    private readonly Dictionary<uint, State> states = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Random random = new();

    public void Process(MyRenderMessageUpdateComponent message)
    {
        if (message.Type == MyRenderMessageUpdateComponent.UpdateType.Delete)
        {
            if (message.Data?.ComponentType == typeof(MyThrustFlameAnimator))
                states.Remove(message.ID);
            return;
        }

        if (message.Data is FlameData flameData)
        {
            var state = Get(message.ID);
            state.GridScale = flameData.GridScale;
            state.IdleColor = flameData.IdleColor;
            state.FullColor = flameData.FullColor;
            state.FlameLengthScale = flameData.FlameLengthScale;
            state.LightPosition = flameData.LightPosition;
            state.FlameLengthMaterial = flameData.FlameLengthMaterial;
            state.FlamePointMaterial = flameData.FlamePointMaterial;
            state.Flames = new MyThrustFlameAnimator.FlameInfo[flameData.Flames.Count];
            for (var i = 0; i < state.Flames.Length; i++)
                state.Flames[i] = flameData.Flames[i];
        }
        else if (message.Data is FloatData floatData &&
                 message.Data.ComponentType == typeof(MyThrustFlameAnimator))
        {
            var state = Get(message.ID);
            state.StrengthPrevious = state.Strength;
            state.Strength = floatData.Float;
            state.StrengthLerp = 0f;
            state.NextUpdate = 0;
        }
    }

    public void Remove(uint id) => states.Remove(id);

    public List<GpuLight> BuildLights(Vector3D cameraPosition, SceneActors actors)
    {
        var result = new List<GpuLight>();
        foreach (var pair in states)
        {
            var state = pair.Value;
            if (state.Strength < 0f || state.Flames.Length == 0)
                continue;
            var actor = actors.Find(pair.Key);
            if (actor == null)
                continue;

            var flame = state.Flames[0];
            var radius = state.RadiusRand * flame.Radius * state.GridScale;
            var length = state.LengthRand * flame.Radius * state.GridScale;
            var world = actors.GetWorldMatrix(actor);
            var position = Vector3D.TransformNormal(state.LightPosition, world) + world.Translation;
            var intensity = 14f + length / Math.Max(radius, 1e-4f) * 4.5f;
            var color = state.CurrentColor * intensity;
            result.Add(new GpuLight
            {
                PositionRange = new Vector4((Vector3)(position - cameraPosition), radius * 2f + length),
                ColorFalloff = new Vector4(color.X, color.Y, color.Z, SceneLights.ConvertFalloff(0.5f)),
                Params = new Vector4(1f, 3.14f, 0f, 0f),
            });
        }

        return result;
    }

    public void Append(BillboardPass billboards, CameraState camera, SceneActors actors)
    {
        var now = clock.Elapsed.TotalSeconds;
        foreach (var pair in states)
        {
            var state = pair.Value;
            var forceActive = Environment.GetEnvironmentVariable("VULKAN_THRUSTERS_FORCE") == "1";
            var strength = forceActive ? 1f : state.Strength;
            // Stock keeps the animator running at zero strength: the line flame disappears but
            // the point glow stays until the component is disabled (-1).
            if (strength < 0f || state.Flames.Length == 0)
                continue;

            var actor = actors.Find(pair.Key);
            if (actor == null)
                continue;

            if (now >= state.NextUpdate)
            {
                var current = forceActive ? 1f : Math.Max(0f,
                    state.StrengthPrevious * (1f - state.StrengthLerp) + state.Strength * state.StrengthLerp);
                state.NextUpdate = now + UpdateInterval;
                state.RadiusRand = RandomRange(0.98f, 1.02f);
                state.ThicknessRand = RandomRange(state.RadiusRand * 0.9f, state.RadiusRand);
                state.LengthRand = current * 10f * RandomRange(0.9f, 1f) * state.FlameLengthScale;
                state.StrengthLerp = Math.Min(state.StrengthLerp + 0.15f, 1f);
            }

            var fullColor = Vector4.Lerp(state.IdleColor, state.FullColor,
                Math.Clamp(strength / Math.Max(MyConstants.MAX_THRUST, 1e-5f), 0f, 1f));
            state.CurrentColor = fullColor.ToLinearRGB();

            var lengthMaterial = MyTransparentMaterials.GetMaterial(state.FlameLengthMaterial);
            var pointMaterial = MyTransparentMaterials.GetMaterial(state.FlamePointMaterial);
            var lengthColor = state.CurrentColor * lengthMaterial.Color;
            var pointColor = state.CurrentColor * pointMaterial.Color;

            var world = actors.GetWorldMatrix(actor);
            foreach (var flame in state.Flames)
            {
                var position = (Vector3D)flame.Position;
                var direction = flame.Direction;
                position = Vector3D.TransformNormal(position, world) + world.Translation;
                direction = Vector3.TransformNormal(direction, world);

                var radius = state.RadiusRand * flame.Radius * state.GridScale;
                var length = state.LengthRand * flame.Radius * state.GridScale;
                var thickness = state.ThicknessRand * flame.Radius * state.GridScale;
                var intensity = ThrustIntensity * Math.Max(state.LengthRand * ThrustLengthIntensity, 1f);

                if (strength > 0f && length > 0f)
                {
                    var origin = position - (Vector3D)direction * ((double)length * 0.25);
                    billboards.AddLineBillboard(state.FlameLengthMaterial, lengthColor, intensity,
                        origin, direction, length, thickness * ThrustThickness);
                }

                if (radius > 0f)
                {
                    billboards.AddPointBillboard(state.FlamePointMaterial, pointColor, intensity,
                        position, radius * ThrustThickness, 0f);
                }
            }
        }
    }

    private State Get(uint id)
    {
        if (!states.TryGetValue(id, out var state))
        {
            state = new State();
            states[id] = state;
        }

        return state;
    }

    private float RandomRange(float min, float max) => min + (float)random.NextDouble() * (max - min);
}
