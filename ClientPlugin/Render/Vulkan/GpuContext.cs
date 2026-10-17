using System;
using System.Collections.Generic;

namespace ClientPlugin.Render.Vulkan;

// Everything resources need: the device, memory, bindless descriptors, uploads and deferred destruction.
internal sealed class GpuContext : IDisposable
{
    public VulkanInstance Instance { get; }
    public VulkanDevice Device { get; }
    public GpuMemory Memory { get; }
    public BindlessDescriptors Bindless { get; private set; }
    public UploadQueue Uploads { get; private set; }
    public TransientAllocator Transient { get; private set; }

    // Releases queued per frame slot, run once the GPU is done with that slot's previous frame
    private readonly List<Action>[] pendingReleases = new List<Action>[FrameLoop.FramesInFlight];
    private readonly object releaseSync = new();
    private int frameSlot;

    public GpuContext(VulkanInstance instance, VulkanDevice device)
    {
        Instance = instance;
        Device = device;
        Memory = new GpuMemory(instance, device);

        for (var i = 0; i < pendingReleases.Length; i++)
            pendingReleases[i] = new List<Action>();

        Uploads = new UploadQueue(this);
        Transient = new TransientAllocator(this);
        Bindless = new BindlessDescriptors(this);
    }

    // Destroys a GPU object after every frame that may still reference it has finished
    public void Release(Action destroy)
    {
        lock (releaseSync)
            pendingReleases[frameSlot].Add(destroy);
    }

    // Called at the start of a frame, after the fence of this frame slot has been waited on
    public void BeginFrame(int slot)
    {
        List<Action> due;
        lock (releaseSync)
        {
            frameSlot = slot;
            due = new List<Action>(pendingReleases[slot]);
            pendingReleases[slot].Clear();
        }

        foreach (var destroy in due)
            destroy();

        Uploads.BeginFrame(slot);
        Transient.BeginFrame(slot);
    }

    public void Dispose()
    {
        Device.WaitIdle();

        Bindless?.Dispose();
        Bindless = null;

        Uploads?.Dispose();
        Uploads = null;

        Transient?.Dispose();
        Transient = null;

        // Everything still queued can go now, the device is idle
        foreach (var list in pendingReleases)
        {
            foreach (var destroy in list)
                destroy();
            list.Clear();
        }

        Memory.Dispose();
    }
}
