using Vortice.Vulkan;

namespace ClientPlugin.Render.Vulkan;

// What a physical device can do. Used to pick the device, to decide which optional
// features (ray tracing, mesh shaders, HDR, ...) can be offered in the options, and
// to choose the defaults of the graphics presets.
internal sealed class DeviceCapabilities
{
    public string Name;
    public uint VendorId;
    public uint DeviceId;
    public VkPhysicalDeviceType Type;
    public VkVersion ApiVersion;
    public uint DriverVersion;
    public ulong DeviceLocalMemory;

    // Required (the device is rejected without them)
    public bool Vulkan13;
    public bool DynamicRendering;
    public bool Synchronization2;
    public bool TimelineSemaphore;
    public bool BufferDeviceAddress;
    public bool DescriptorIndexing;
    public bool ShaderDrawParameters;
    public bool TextureCompressionBC;
    public bool SamplerAnisotropy;
    public bool Swapchain;

    // Optional
    public bool RayQuery;
    public bool AccelerationStructure;
    public bool MeshShader;

    public bool MeetsRequirements(out string missing)
    {
        missing = Type == VkPhysicalDeviceType.Cpu ? "hardware acceleration (software rasterizer)"
            : !Vulkan13 ? "Vulkan 1.3"
            : !DynamicRendering ? "dynamicRendering"
            : !Synchronization2 ? "synchronization2"
            : !TimelineSemaphore ? "timelineSemaphore"
            : !BufferDeviceAddress ? "bufferDeviceAddress"
            : !DescriptorIndexing ? "descriptor indexing"
            : !ShaderDrawParameters ? "shaderDrawParameters"
            : !TextureCompressionBC ? "textureCompressionBC"
            : !SamplerAnisotropy ? "samplerAnisotropy"
            : !Swapchain ? "VK_KHR_swapchain"
            : null;
        return missing == null;
    }

    // Higher is better, used when the game's selected adapter cannot be matched
    public int Score => Type switch
    {
        VkPhysicalDeviceType.DiscreteGpu => 3000,
        VkPhysicalDeviceType.IntegratedGpu => 2000,
        VkPhysicalDeviceType.VirtualGpu => 1000,
        _ => 0
    } + (int)(DeviceLocalMemory >> 30);

    public override string ToString() =>
        $"{Name} ({Type}, vendor 0x{VendorId:X4}, device 0x{DeviceId:X4}, Vulkan {ApiVersion.Major}.{ApiVersion.Minor}.{ApiVersion.Patch}, " +
        $"{DeviceLocalMemory >> 20} MB, rayQuery={RayQuery}, meshShader={MeshShader})";
}
