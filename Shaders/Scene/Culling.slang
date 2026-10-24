// GPU-driven opaque scene culling over persistent actor/part/LOD data. One thread per actor selects
// the camera LOD (stock hysteresis) for the forward buckets, then appends the LOD0 parts to the
// shadow caster buckets. The CPU submits one vkCmdDrawIndirectCount per bucket.
//
// Layouts must match ClientPlugin/Render/Scene/GpuSceneDraws.cs and Scene/Model.slang.

struct DrawMaterial
{
    float4 colorMulEmissive;
    uint colorMetal;
    uint normalGloss;
    uint extensions;
    uint alphamask;
    uint flags;
    float depthBias;
    uint pad0;
    uint pad1;
};

struct ModelVertex
{
    float4 position;
    float2 texcoord;
    uint normal;
    uint tangent;
};

// Must match GpuSceneActor (192 bytes)
struct SceneActor
{
    float4 world0;
    float4 world1;
    float4 world2;
    float4 world3;
    float4 colorMaskHsv;
    float4 bounds;       // xyz: absolute bounds center, w: radius
    float4 origin;       // xyz: absolute origin, w: scale
    float4 lodDistances0;
    float4 lodDistances1;
    uint64_t instances;
    uint64_t skinnedVertices; // deformed vertex buffer when flags has Skinned
    uint instanceStart;
    uint instanceCount;
    uint flags;          // 1: NoBackFaceCulling, 2: DrawOutsideViewDistance, 4: Visible, 8: Skinned
    uint rangeBase;
    uint lodStateIndex;
    uint lodCount;
    uint forcedLod;      // skinned actors use the LOD the skinning pass produced
    float maxViewDistance;
};

// Must match GpuScenePart (80 bytes)
struct ScenePart
{
    DrawMaterial material;
    ModelVertex* vertices;
    uint* indices;
    uint indexStart;
    uint indexCount;
    uint pad0;
    uint pad1;
};

// Must match GpuLodRange (8 bytes): [start, start + count) in the persistent part buffer.
struct LodRange
{
    uint start;
    uint count;
};

// Must match CullParams (GpuSceneDraws.cs)
struct CullParams
{
    float lodCoefficient;
    float objectDistanceAdd;
    float objectDistanceMult;
    float hysteresis;
    uint enableLodSelection;
    uint lodSelection;
    uint lodUpdate;
    uint actorCount;
    uint maxParts;
    uint pad0;
    uint64_t bucket0;
    uint64_t bucket1;
    uint64_t bucket2;
    uint64_t bucket3;
    uint64_t diagnostics;
    float4 cameraPosition;
    float4 skinnedOffset; // main-camera-relative -> probe-camera-relative
    uint drawAll;
    uint shadowCulling;   // 1: append shadow casters, 0: reflection probe
};

// Must match GpuDiagnosticsCounters (32 bytes); written with atomics from this pass.
struct GpuDiagnostics
{
    uint triangles;
    uint parts;
    uint instances;
    uint visibleActors;
    uint culledActors;
    uint pad0;
    uint pad1;
    uint pad2;
};

// Must match Scene/Model.slang (32 bytes of bones, packed ortho matrix, key color)
struct CubeInstance
{
    uint4 bones0;
    uint4 bones1;
    float4 translationAndRot;
    float4 colorMaskHsv;
};

struct DrawData
{
    float4 world0;
    float4 world1;
    float4 world2;
    float4 world3;
    float4 colorMaskHsv;
    DrawMaterial material;
    ModelVertex* vertices;
    uint* indices;
    uint indexStart;
    uint indexCount;
    uint pad0;
    uint pad1;
    CubeInstance* instances;
    uint instanceStart;
    uint instanceCount;
    uint pad2;
    uint pad3;
};

struct DrawCommand
{
    uint vertexCount;
    uint instanceCount;
    uint firstVertex;
    uint firstInstance;
};

struct PushConstants
{
    // View-projection columns. The shader derives the five cull planes on the GPU instead of
    // receiving them from the CPU.
    float4 viewProjection0;
    float4 viewProjection1;
    float4 viewProjection2;
    float4 viewProjection3;
    SceneActor* actors;
    ScenePart* parts;
    LodRange* ranges;
    uint* lodStates;
    CullParams* params;
};

[[vk::push_constant]] ConstantBuffer<PushConstants> push;

// Bucket layout (see GpuSceneDraws.cs): counter @0, commands @64 (16 B each), draw data @64 + capacity * 16.
static const uint64_t CommandOffset = 64;
static const uint DrawStride = 184;

float4 normalizePlane(float4 p)
{
    float lengthSq = dot(p.xyz, p.xyz);
    return lengthSq > 1e-12 ? p * rsqrt(lengthSq) : p;
}

float lodThreshold(SceneActor actor, uint lod)
{
    float4 distances0 = actor.lodDistances0;
    float4 distances1 = actor.lodDistances1;
    if (lod == 1) return distances0.x;
    if (lod == 2) return distances0.y;
    if (lod == 3) return distances0.z;
    if (lod == 4) return distances0.w;
    if (lod == 5) return distances1.x;
    if (lod == 6) return distances1.y;
    if (lod == 7) return distances1.z;
    return distances1.w;
}

uint selectLod(SceneActor actor, CullParams params)
{
    if (actor.lodCount <= 1)
        return 0;

    // The skinning pass produced vertices for one specific LOD; use exactly that LOD so the
    // index range and deformed vertex buffer match, and do not disturb the main LOD state.
    if ((actor.flags & 8u) != 0)
        return min(actor.forcedLod, actor.lodCount - 1);

    if (params.enableLodSelection != 0)
        return min(params.lodSelection, actor.lodCount - 1);

    uint lod = min(push.lodStates[actor.lodStateIndex], actor.lodCount - 1);
    if (params.lodUpdate == 0)
        return lod;

    float distance = length(actor.origin.xyz - params.cameraPosition.xyz);
    float scale = max(actor.origin.w, 1.0e-6);
    float d = (distance / scale * params.lodCoefficient + params.objectDistanceAdd)
              * params.objectDistanceMult;

    // MyLodStrategyInfo.GetTheBestLodWithHisteresis (10% dead zone by default)
    while (lod > 0)
    {
        float threshold = lodThreshold(actor, lod);
        if (d >= threshold - threshold * params.hysteresis)
            break;
        lod--;
    }

    while (lod < actor.lodCount - 1)
    {
        float threshold = lodThreshold(actor, lod + 1);
        if (d <= threshold + threshold * params.hysteresis)
            break;
        lod++;
    }

    push.lodStates[actor.lodStateIndex] = lod;
    return lod;
}

DrawData makeDrawData(SceneActor actor, ScenePart part, float3 camera, CullParams params)
{
    DrawData draw;
    if ((actor.flags & 8u) != 0)
    {
        // Skinned vertices were deformed relative to the main camera; shift them into the probe
        // camera's frame (zero for the main view). The skinning pass already applied the world
        // transform, so only this translation is needed.
        float3 offset = params.skinnedOffset.xyz;
        draw.world0 = float4(1.0, 0.0, 0.0, offset.x);
        draw.world1 = float4(0.0, 1.0, 0.0, offset.y);
        draw.world2 = float4(0.0, 0.0, 1.0, offset.z);
        draw.world3 = float4(0.0, 0.0, 0.0, 1.0);
        draw.vertices = (ModelVertex*)actor.skinnedVertices;
        draw.instances = (CubeInstance*)0;
        draw.instanceStart = 0;
        draw.instanceCount = 1;
    }
    else
    {
        // Records store absolute transforms; the forward pass is camera-relative.
        draw.world0 = actor.world0;
        draw.world1 = actor.world1;
        draw.world2 = actor.world2;
        draw.world3 = actor.world3;
        draw.world0.w -= camera.x;
        draw.world1.w -= camera.y;
        draw.world2.w -= camera.z;
        draw.vertices = part.vertices;
        draw.instances = (CubeInstance*)actor.instances;
        draw.instanceStart = actor.instanceStart;
        draw.instanceCount = actor.instanceCount;
    }

    draw.colorMaskHsv = actor.colorMaskHsv;
    draw.material = part.material;
    draw.indices = part.indices;
    draw.indexStart = part.indexStart;
    draw.indexCount = part.indexCount;
    draw.pad0 = 0;
    draw.pad1 = 0;
    draw.pad2 = 0;
    draw.pad3 = 0;
    return draw;
}

[numthreads(64, 1, 1)]
[shader("compute")]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint index = threadId.x;
    if (index >= push.params.actorCount)
        return;

    CullParams params = *push.params;
    SceneActor actor = push.actors[index];
    if ((actor.flags & 4u) == 0)
        return;

    float3 camera = params.cameraPosition.xyz;
    float3 center = actor.bounds.xyz - camera;
    float radius = actor.bounds.w;
    GpuDiagnostics* diag = (GpuDiagnostics*)params.diagnostics;


    if ((actor.flags & 2u) == 0 && actor.maxViewDistance < 3.4e38
        && length(center) - radius > actor.maxViewDistance)
    {
        InterlockedAdd(diag->culledActors, 1u);
        return;
    }

    // Keep the first-person self actor out of the probe when its bounds surround the camera.
    if (params.shadowCulling == 0 && (actor.flags & 8u) != 0)
    {
        float selfDistance = length(actor.origin.xyz - camera);
        if (selfDistance - radius < 0.5)
        {
            InterlockedAdd(diag->culledActors, 1u);
            return;
        }
    }

    if (params.drawAll == 0)
    {
        // Plane extraction from the transposed view-projection columns (same formula as the old
        // CPU Frustum helper). This keeps per-view cull setup on the GPU.
        float4 c0 = push.viewProjection0;
        float4 c1 = push.viewProjection1;
        float4 c2 = push.viewProjection2;
        float4 c3 = push.viewProjection3;
        float4 planes[5] =
        {
            normalizePlane(c3 + c0), // left
            normalizePlane(c3 - c0), // right
            normalizePlane(c3 + c1), // bottom
            normalizePlane(c3 - c1), // top
            normalizePlane(c3 - c2), // near
        };

        [unroll]
        for (uint i = 0; i < 5; i++)
        {
            float4 plane = planes[i];
            if (dot(plane.xyz, center) + plane.w < -radius)
            {
                InterlockedAdd(diag->culledActors, 1u);
                return;
            }
        }
    }

    uint lod = selectLod(actor, params);
    LodRange range = push.ranges[actor.rangeBase + lod];
    if (range.count == 0)
        return;

    InterlockedAdd(diag->visibleActors, 1u);
    uint64_t base = (actor.flags & 1u) != 0 ? params.bucket1 : params.bucket0;
    uint* counter = (uint*)base;
    DrawCommand* commands = (DrawCommand*)(base + CommandOffset);
    DrawData* draws = (DrawData*)(base + CommandOffset + (uint64_t)params.maxParts * 16);

    for (uint partIndex = 0; partIndex < range.count; partIndex++)
    {
        ScenePart part = push.parts[range.start + partIndex];
        if (part.indexCount == 0 || part.vertices == nullptr || part.indices == nullptr)
            continue;

        uint slot;
        InterlockedAdd(counter[0], 1, slot);

        uint instanceCount = (actor.flags & 8u) != 0 ? 1u : max(actor.instanceCount, 1u);
        InterlockedAdd(diag->triangles, (part.indexCount / 3u) * instanceCount);
        InterlockedAdd(diag->parts, 1u);
        InterlockedAdd(diag->instances, instanceCount);

        DrawCommand command;
        command.vertexCount = part.indexCount;
        command.instanceCount = instanceCount;
        command.firstVertex = 0;
        command.firstInstance = 0;
        commands[slot] = command;

        draws[slot] = makeDrawData(actor, part, camera, params);
    }

    // The reflection probe captures the full surrounding scene, not a shadow-caster list.
    if (params.shadowCulling == 0)
        return;

    // Skinned actors are not shadow casters yet: the shadow range is LOD0, while the skinned
    // vertex buffer matches the selected LOD.
    if ((actor.flags & 8u) != 0)
        return;

    // Shadow casters are a separate LOD0 draw list: the forward pass may use a higher LOD, but the
    // shadow map must keep a consistent silhouette at distance or shadows pop when crossing a LOD.
    LodRange shadowRange = push.ranges[actor.rangeBase];
    if (shadowRange.count == 0)
        return;

    uint64_t shadowBase = (actor.flags & 1u) != 0 ? params.bucket3 : params.bucket2;
    uint* shadowCounter = (uint*)shadowBase;
    DrawCommand* shadowCommands = (DrawCommand*)(shadowBase + CommandOffset);
    DrawData* shadowDraws = (DrawData*)(shadowBase + CommandOffset + (uint64_t)params.maxParts * 16);

    for (uint partIndex = 0; partIndex < shadowRange.count; partIndex++)
    {
        ScenePart part = push.parts[shadowRange.start + partIndex];

        uint slot;
        InterlockedAdd(shadowCounter[0], 1, slot);

        uint instanceCount = max(actor.instanceCount, 1u);
        DrawCommand command;
        command.vertexCount = part.indexCount;
        command.instanceCount = instanceCount;
        command.firstVertex = 0;
        command.firstInstance = 0;
        shadowCommands[slot] = command;

        DrawData draw = makeDrawData(actor, part, camera, params);
        shadowDraws[slot] = draw;
    }
}
