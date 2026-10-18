// GPU-driven opaque scene culling over persistent actor/part/LOD data. One thread per actor selects
// the LOD (stock hysteresis), then appends one command + draw data per visible part into one of two
// buckets (cull/no-cull). The CPU submits one vkCmdDrawIndirectCount per bucket.
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

// Must match GpuSceneActor (184 bytes)
struct SceneActor
{
    float4 world0;
    float4 world1;
    float4 world2;
    float4 world3;
    float4 colorMaskHsv;
    float4 bounds;       // xyz: camera-relative bounds center, w: radius
    float4 origin;       // xyz: camera-relative origin, w: scale
    float4 lodDistances0;
    float4 lodDistances1;
    uint64_t instances;
    uint instanceStart;
    uint instanceCount;
    uint flags;          // 1: NoBackFaceCulling, 2: DrawOutsideViewDistance
    uint rangeBase;
    uint lodStateIndex;
    uint lodCount;
    float maxViewDistance;
    uint pad0;
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

// Must match CullParams (64 bytes)
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
    uint64_t diagnostics;
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
    float4 planes[5]; // normalized, xyz + d
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

    if (params.enableLodSelection != 0)
        return min(params.lodSelection, actor.lodCount - 1);

    uint lod = min(push.lodStates[actor.lodStateIndex], actor.lodCount - 1);
    if (params.lodUpdate == 0)
        return lod;

    float distance = length(actor.origin.xyz);
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

[numthreads(64, 1, 1)]
[shader("compute")]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint index = threadId.x;
    if (index >= push.params.actorCount)
        return;

    CullParams params = *push.params;
    SceneActor actor = push.actors[index];
    float3 center = actor.bounds.xyz;
    float radius = actor.bounds.w;
    GpuDiagnostics* diag = (GpuDiagnostics*)params.diagnostics;

    if ((actor.flags & 2u) == 0 && actor.maxViewDistance < 3.4e38
        && length(center) - radius > actor.maxViewDistance)
    {
        InterlockedAdd(diag->culledActors, 1u);
        return;
    }

    [unroll]
    for (uint i = 0; i < 5; i++)
    {
        float4 plane = push.planes[i];
        if (dot(plane.xyz, center) + plane.w < -radius)
        {
            InterlockedAdd(diag->culledActors, 1u);
            return;
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

        uint slot;
        InterlockedAdd(counter[0], 1, slot);

        uint instanceCount = max(actor.instanceCount, 1u);
        InterlockedAdd(diag->triangles, (part.indexCount / 3u) * instanceCount);
        InterlockedAdd(diag->parts, 1u);
        InterlockedAdd(diag->instances, instanceCount);

        DrawCommand command;
        command.vertexCount = part.indexCount;
        command.instanceCount = instanceCount;
        command.firstVertex = 0;
        command.firstInstance = 0;
        commands[slot] = command;

        DrawData draw;
        draw.world0 = actor.world0;
        draw.world1 = actor.world1;
        draw.world2 = actor.world2;
        draw.world3 = actor.world3;
        draw.colorMaskHsv = actor.colorMaskHsv;
        draw.material = part.material;
        draw.vertices = part.vertices;
        draw.indices = part.indices;
        draw.indexStart = part.indexStart;
        draw.indexCount = part.indexCount;
        draw.pad0 = 0;
        draw.pad1 = 0;
        draw.instances = (CubeInstance*)actor.instances;
        draw.instanceStart = actor.instanceStart;
        draw.instanceCount = actor.instanceCount;
        draw.pad2 = 0;
        draw.pad3 = 0;
        draws[slot] = draw;
    }
}
