using System.Runtime.InteropServices;

namespace ClientPlugin.Render.Scene;

// GPU counters for the GPU-driven opaque path. Must match GpuDiagnostics in Scene/Culling.slang.
// The culling compute increments these atomics; the CPU reads the previous use of a frame slot
// (after its fence) for the diagnostics overlay.
[StructLayout(LayoutKind.Sequential)]
internal struct GpuDiagnosticsCounters
{
    public uint Triangles;
    public uint Parts;
    public uint Instances;
    public uint VisibleActors;
    public uint CulledActors;
    public uint Pad0;
    public uint Pad1;
    public uint Pad2;
}
