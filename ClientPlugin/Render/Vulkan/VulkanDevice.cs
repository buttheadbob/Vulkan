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

    // Separate copy engine used for asynchronous asset uploads. Null when the device has no
    // transfer-capable family different from the graphics family; uploads then fall back to the
    // graphics queue (same behaviour, just without copy-engine overlap).
    public bool HasDedicatedTransfer { get; private set; }
    public uint TransferQueueFamily { get; private set; }
    public VkQueue TransferQueue { get; private set; }
    public bool DeviceFaultEnabled { get; private set; }

    // Serializes queue submission and presentation, VkQueue access must be externally synchronized
    public readonly object QueueLock = new();

    public static VulkanDevice Create(VulkanInstance instance, VkSurfaceKHR surface, MyAdapterInfo? preferredAdapter)
    {
        var candidates = instance.EnumerateDevices();
        foreach (var (_, caps) in candidates)
            RenderLog.Info("Found GPU: " + caps);

        var (physicalDevice, selected, queueFamily) = Select(instance, surface, candidates, preferredAdapter);
        RenderLog.Info("Selected GPU: " + selected.Name);

        var transferFamily = FindTransferFamily(instance, physicalDevice, queueFamily);
        if (transferFamily.HasValue)
            RenderLog.Info($"Dedicated transfer queue: family {transferFamily.Value} (graphics family {queueFamily})");
        else
            RenderLog.Info("No dedicated transfer queue family; uploads use the graphics queue");

        var extensions = new List<string> { VkNames.ToString(VK_KHR_SWAPCHAIN_EXTENSION_NAME) };
        var availableExtensions = VkNames.ToSet(instance.EnumerateDeviceExtensions(physicalDevice));
        var faultEnabled = availableExtensions.Contains("VK_EXT_device_fault");
        if (faultEnabled)
            extensions.Add("VK_EXT_device_fault");
        var faultFeatures = new VkPhysicalDeviceFaultFeaturesEXT
        {
            deviceFault = faultEnabled,
            deviceFaultVendorBinary = false,
        };

        var features13 = new VkPhysicalDeviceVulkan13Features
        {
            pNext = faultEnabled ? &faultFeatures : null,
            dynamicRendering = true,
            synchronization2 = true,
        };
        var features12 = new VkPhysicalDeviceVulkan12Features
        {
            pNext = &features13,
            timelineSemaphore = true,
            bufferDeviceAddress = true,
            drawIndirectCount = true,
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
        var queueInfos = new VkDeviceQueueCreateInfo[transferFamily.HasValue ? 2 : 1];
        queueInfos[0] = new VkDeviceQueueCreateInfo
        {
            queueFamilyIndex = queueFamily,
            queueCount = 1,
            pQueuePriorities = &priority,
        };
        if (transferFamily.HasValue)
        {
            queueInfos[1] = new VkDeviceQueueCreateInfo
            {
                queueFamilyIndex = transferFamily.Value,
                queueCount = 1,
                pQueuePriorities = &priority,
            };
        }

        using var extensionNames = new VkStringArray(extensions);
        fixed (VkDeviceQueueCreateInfo* pQueueInfos = queueInfos)
        {
            var createInfo = new VkDeviceCreateInfo
            {
                pNext = &features2,
                queueCreateInfoCount = (uint)queueInfos.Length,
                pQueueCreateInfos = pQueueInfos,
                enabledExtensionCount = extensionNames.Length,
                ppEnabledExtensionNames = extensionNames,
            };

            VkDevice device;
            instance.Api.vkCreateDevice(physicalDevice, &createInfo, &device).CheckResult("vkCreateDevice");

            var api = GetApi(instance.Handle, device);
            VkQueue queue;
            api.vkGetDeviceQueue(queueFamily, 0, &queue);
            VkQueue transferQueue = VkQueue.Null;
            if (transferFamily.HasValue)
                api.vkGetDeviceQueue(transferFamily.Value, 0, &transferQueue);

            return new VulkanDevice
            {
                PhysicalDevice = physicalDevice,
                Caps = selected,
                Handle = device,
                Api = api,
                QueueFamily = queueFamily,
                Queue = queue,
                HasDedicatedTransfer = transferFamily.HasValue,
                TransferQueueFamily = transferFamily ?? queueFamily,
                TransferQueue = transferQueue,
                DeviceFaultEnabled = faultEnabled,
            };
        }
    }

    // Prefers a queue family that only does transfers (a real copy engine). Falls back to any
    // transfer-capable family that is not the graphics family; null means no separate queue.
    private static uint? FindTransferFamily(VulkanInstance instance, VkPhysicalDevice device, uint graphicsFamily)
    {
        uint count;
        instance.Api.vkGetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        var families = new VkQueueFamilyProperties[count];
        fixed (VkQueueFamilyProperties* p = families)
            instance.Api.vkGetPhysicalDeviceQueueFamilyProperties(device, &count, p);

        uint? fallback = null;
        for (uint i = 0; i < count; i++)
        {
            if (i == graphicsFamily || (families[i].queueFlags & VkQueueFlags.Transfer) == 0)
                continue;

            if ((families[i].queueFlags & (VkQueueFlags.Graphics | VkQueueFlags.Compute)) == 0)
                return i;

            fallback ??= i;
        }

        return fallback;
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

    // VK_EXT_device_fault: call immediately after a device-lost result, before destroying the
    // device, to record the faulting GPU virtual address and vendor fault code.
    public void LogDeviceFault()
    {
        if (!DeviceFaultEnabled)
        {
            RenderLog.Warning("Device lost; VK_EXT_device_fault is not available");
            return;
        }

        try
        {
            var counts = new VkDeviceFaultCountsEXT
            {
                sType = VkStructureType.DeviceFaultCountsEXT,
            };
            Api.vkGetDeviceFaultInfoEXT(&counts, null);
            var addresses = new VkDeviceFaultAddressInfoKHR[counts.addressInfoCount];
            var vendors = new VkDeviceFaultVendorInfoKHR[counts.vendorInfoCount];
            var info = new VkDeviceFaultInfoEXT
            {
                sType = VkStructureType.DeviceFaultInfoEXT,
            };
            fixed (VkDeviceFaultAddressInfoKHR* addressPtr = addresses)
            fixed (VkDeviceFaultVendorInfoKHR* vendorPtr = vendors)
            {
                info.pAddressInfos = addressPtr;
                info.pVendorInfos = vendorPtr;
                var result = Api.vkGetDeviceFaultInfoEXT(&counts, &info);
                if (result != VkResult.Success)
                {
                    RenderLog.Warning($"vkGetDeviceFaultInfoEXT returned {result}");
                    return;
                }
                var description = VkNames.FromFixed((byte*)&info.description);
                RenderLog.Error($"GPU device fault: {description}");
                for (var i = 0; i < addresses.Length; i++)
                {
                    var a = addresses[i];
                    RenderLog.Error($"  GPU fault address {i}: {a.addressType}, " +
                                    $"0x{a.reportedAddress:X}, precision 0x{a.addressPrecision:X}");
                }
                for (var i = 0; i < vendors.Length; i++)
                {
                    var v = vendors[i];
                    var vendorDescription =
                        VkNames.FromFixed((byte*)&v.description);
                    RenderLog.Error($"  GPU vendor fault {i}: {vendorDescription}, " +
                                    $"code 0x{v.vendorFaultCode:X}, data 0x{v.vendorFaultData:X}");
                }
            }
        }
        catch (Exception e)
        {
            RenderLog.Warning($"Could not query GPU fault info: {e.Message}");
        }
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
