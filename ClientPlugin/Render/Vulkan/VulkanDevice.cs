using System;
using System.Collections.Generic;
using System.Linq;
using Vortice.Vulkan;
using VRageRender;
using static Vortice.Vulkan.Vulkan;

namespace ClientPlugin.Render.Vulkan;

// The logical device and its graphics/present queue
internal sealed unsafe class VulkanDevice : IDisposable
{
    public VkPhysicalDevice PhysicalDevice { get; private set; }
    public DeviceCapabilities Caps { get; private set; }
    public VkDevice Handle { get; private set; }
    public VkDeviceApi Api { get; private set; }
    public uint QueueFamily { get; private set; }
    public VkQueue Queue { get; private set; }

    // Serializes queue submission and presentation, VkQueue access must be externally synchronized
    public readonly object QueueLock = new();

    public static VulkanDevice Create(VulkanInstance instance, VkSurfaceKHR surface, MyAdapterInfo? preferredAdapter)
    {
        var candidates = instance.EnumerateDevices();
        foreach (var (_, caps) in candidates)
            RenderLog.Info("Found GPU: " + caps);

        var (physicalDevice, selected, queueFamily) = Select(instance, surface, candidates, preferredAdapter);
        RenderLog.Info("Selected GPU: " + selected.Name);

        var extensions = new List<string> { VkNames.ToString(VK_KHR_SWAPCHAIN_EXTENSION_NAME) };

        var features13 = new VkPhysicalDeviceVulkan13Features
        {
            dynamicRendering = true,
            synchronization2 = true,
        };
        var features12 = new VkPhysicalDeviceVulkan12Features
        {
            pNext = &features13,
            timelineSemaphore = true,
            bufferDeviceAddress = true,
            descriptorIndexing = true,
            runtimeDescriptorArray = true,
            descriptorBindingPartiallyBound = true,
            descriptorBindingVariableDescriptorCount = true,
            shaderSampledImageArrayNonUniformIndexing = true,
            descriptorBindingSampledImageUpdateAfterBind = true,
        };
        var features11 = new VkPhysicalDeviceVulkan11Features
        {
            pNext = &features12,
            shaderDrawParameters = true, // SV_InstanceID / SV_VertexID with base instance/vertex
        };
        var features2 = new VkPhysicalDeviceFeatures2 { pNext = &features11 };
        features2.features.textureCompressionBC = true;
        features2.features.samplerAnisotropy = true;

        var priority = 1f;
        var queueInfo = new VkDeviceQueueCreateInfo
        {
            queueFamilyIndex = queueFamily,
            queueCount = 1,
            pQueuePriorities = &priority,
        };

        using var extensionNames = new VkStringArray(extensions);
        var createInfo = new VkDeviceCreateInfo
        {
            pNext = &features2,
            queueCreateInfoCount = 1,
            pQueueCreateInfos = &queueInfo,
            enabledExtensionCount = extensionNames.Length,
            ppEnabledExtensionNames = extensionNames,
        };

        VkDevice device;
        instance.Api.vkCreateDevice(physicalDevice, &createInfo, &device).CheckResult("vkCreateDevice");

        var api = GetApi(instance.Handle, device);
        VkQueue queue;
        api.vkGetDeviceQueue(queueFamily, 0, &queue);

        return new VulkanDevice
        {
            PhysicalDevice = physicalDevice,
            Caps = selected,
            Handle = device,
            Api = api,
            QueueFamily = queueFamily,
            Queue = queue,
        };
    }

    // Prefers the GPU the game selected (matched by PCI vendor and device ID), otherwise the best usable one
    private static (VkPhysicalDevice, DeviceCapabilities, uint) Select(
        VulkanInstance instance,
        VkSurfaceKHR surface,
        List<(VkPhysicalDevice Device, DeviceCapabilities Caps)> candidates,
        MyAdapterInfo? preferredAdapter)
    {
        var usable = new List<(VkPhysicalDevice Device, DeviceCapabilities Caps, uint QueueFamily)>();
        foreach (var (device, caps) in candidates)
        {
            if (!caps.MeetsRequirements(out var missing))
            {
                RenderLog.Info($"Skipping GPU {caps.Name}: missing {missing}");
                continue;
            }

            var queueFamily = FindQueueFamily(instance, device, surface);
            if (queueFamily == null)
            {
                RenderLog.Info($"Skipping GPU {caps.Name}: no queue can render and present to the game window");
                continue;
            }

            usable.Add((device, caps, queueFamily.Value));
        }

        if (usable.Count == 0)
            throw new NotSupportedException("No GPU supports the features required by the Vulkan renderer");

        if (preferredAdapter.HasValue)
        {
            var adapter = preferredAdapter.Value;
            var match = usable.FirstOrDefault(u => u.Caps.VendorId == (uint)adapter.VendorId && u.Caps.DeviceId == (uint)adapter.DeviceId);
            if (match.Caps != null)
                return (match.Device, match.Caps, match.QueueFamily);

            RenderLog.Info($"The adapter selected in the game ({adapter.Name}) has no matching Vulkan device, picking the best available");
        }

        var best = usable.OrderByDescending(u => u.Caps.Score).First();
        return (best.Device, best.Caps, best.QueueFamily);
    }

    private static uint? FindQueueFamily(VulkanInstance instance, VkPhysicalDevice device, VkSurfaceKHR surface)
    {
        uint count;
        instance.Api.vkGetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        var families = new VkQueueFamilyProperties[count];
        fixed (VkQueueFamilyProperties* p = families)
            instance.Api.vkGetPhysicalDeviceQueueFamilyProperties(device, &count, p);

        for (uint i = 0; i < count; i++)
        {
            const VkQueueFlags required = VkQueueFlags.Graphics | VkQueueFlags.Compute;
            if ((families[i].queueFlags & required) != required)
                continue;

            VkBool32 presentSupported;
            instance.Api.vkGetPhysicalDeviceSurfaceSupportKHR(device, i, surface, &presentSupported).CheckResult("vkGetPhysicalDeviceSurfaceSupportKHR");
            if (presentSupported)
                return i;
        }

        return null;
    }

    public void WaitIdle()
    {
        if (Handle.IsNull)
            return;

        lock (QueueLock)
            Api.vkDeviceWaitIdle();
    }

    public void Dispose()
    {
        if (Handle.IsNull)
            return;

        WaitIdle();
        Api.vkDestroyDevice(null);
        Handle = VkDevice.Null;
    }
}
