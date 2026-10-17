using ClientPlugin.Render.Vulkan;

namespace ClientPlugin.Render;

// What is actually rendering and on which GPU, for the options screen and bug reports
internal static class RendererStatus
{
    public static bool VulkanActive { get; private set; }
    public static string FallbackReason { get; private set; }
    public static DeviceCapabilities Device { get; private set; }

    public static void SetFallback(string reason)
    {
        VulkanActive = false;
        FallbackReason = reason;
    }

    public static void SetVulkanInstalled()
    {
        VulkanActive = true;
        FallbackReason = null;
    }

    public static void SetDevice(DeviceCapabilities device) => Device = device;

    public static string ActiveRenderer => VulkanActive
        ? "Vulkan"
        : FallbackReason == null ? "DirectX 11" : $"DirectX 11 ({FallbackReason})";

    public static string Gpu => Device == null
        ? "-"
        : $"{Device.Name}, Vulkan {Device.ApiVersion.Major}.{Device.ApiVersion.Minor}.{Device.ApiVersion.Patch}, {Device.DeviceLocalMemory >> 20} MB";

    public static string Features => Device == null
        ? "-"
        : $"Ray tracing: {(Device.RayQuery ? "yes" : "no")}, mesh shaders: {(Device.MeshShader ? "yes" : "no")}";
}
