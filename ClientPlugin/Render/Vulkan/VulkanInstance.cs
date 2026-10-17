using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ClientPlugin.Render.Vulkan;

// VkInstance plus the optional validation messenger, and enumeration of the physical devices
internal sealed unsafe class VulkanInstance : IDisposable
{
    private const string ValidationLayer = "VK_LAYER_KHRONOS_validation";

    private static bool loaderInitialized;

    public VkInstance Handle { get; private set; }
    public VkInstanceApi Api { get; private set; }
    public bool ValidationEnabled { get; private set; }

    private VkDebugUtilsMessengerEXT messenger;

    // Loads the Vulkan loader (vulkan-1.dll / libvulkan.so.1) once per process
    public static bool TryInitializeLoader(out string reason)
    {
        if (!loaderInitialized)
        {
            try
            {
                if (vkInitialize() != VkResult.Success)
                {
                    reason = "Vulkan loader not found, install or update the GPU driver";
                    return false;
                }
            }
            catch (Exception e)
            {
                reason = "Vulkan loader failed to load: " + e.Message;
                return false;
            }

            loaderInitialized = true;
        }

        uint version;
        vkEnumerateInstanceVersion(&version).CheckResult("vkEnumerateInstanceVersion");
        if (version < VkVersion.Version_1_3)
        {
            reason = $"Vulkan loader is too old ({new VkVersion(version)}), version 1.3 is required";
            return false;
        }

        reason = null;
        return true;
    }

    public static VulkanInstance Create(IEnumerable<string> requiredExtensions, bool validation)
    {
        var available = VkNames.ToSet(EnumerateInstanceExtensions());
        var extensions = requiredExtensions.Distinct().ToList();

        var missing = extensions.Where(e => !available.Contains(e)).ToList();
        if (missing.Count != 0)
            throw new NotSupportedException("Missing Vulkan instance extensions: " + string.Join(", ", missing));

        var layers = new List<string>();
        var debugUtils = VkNames.ToString(VK_EXT_DEBUG_UTILS_EXTENSION_NAME);
        if (validation)
        {
            if (EnumerateLayers().Contains(ValidationLayer) && available.Contains(debugUtils))
            {
                layers.Add(ValidationLayer);
                extensions.Add(debugUtils);
            }
            else
            {
                RenderLog.Warning("Validation requested, but the Khronos validation layer is not installed");
                validation = false;
            }
        }

        using var appName = new VkStringArray(["Space Engineers"]);
        using var engineName = new VkStringArray([Plugin.Name]);
        using var extensionNames = new VkStringArray(extensions);
        using var layerNames = new VkStringArray(layers);

        var appInfo = new VkApplicationInfo
        {
            pApplicationName = ((byte**)appName)[0],
            applicationVersion = new VkVersion(1, 0, 0),
            pEngineName = ((byte**)engineName)[0],
            engineVersion = new VkVersion(1, 0, 0),
            apiVersion = VkVersion.Version_1_3,
        };

        var createInfo = new VkInstanceCreateInfo
        {
            pApplicationInfo = &appInfo,
            enabledExtensionCount = extensionNames.Length,
            ppEnabledExtensionNames = extensionNames,
            enabledLayerCount = layerNames.Length,
            ppEnabledLayerNames = layerNames,
        };

        VkInstance instance;
        vkCreateInstance(&createInfo, &instance).CheckResult("vkCreateInstance");

        var result = new VulkanInstance
        {
            Handle = instance,
            Api = GetApi(instance),
            ValidationEnabled = validation,
        };

        if (validation)
            result.CreateMessenger();

        RenderLog.Info($"Instance created, extensions: {string.Join(", ", extensions)}" + (validation ? ", validation enabled" : ""));
        return result;
    }

    public List<(VkPhysicalDevice Device, DeviceCapabilities Caps)> EnumerateDevices()
    {
        uint count;
        Api.vkEnumeratePhysicalDevices(&count, null).CheckResult("vkEnumeratePhysicalDevices");
        var devices = new VkPhysicalDevice[count];
        fixed (VkPhysicalDevice* p = devices)
            Api.vkEnumeratePhysicalDevices(&count, p).CheckResult("vkEnumeratePhysicalDevices");

        return devices.Select(d => (d, QueryCapabilities(d))).ToList();
    }

    private DeviceCapabilities QueryCapabilities(VkPhysicalDevice device)
    {
        VkPhysicalDeviceProperties properties;
        Api.vkGetPhysicalDeviceProperties(device, &properties);

        var caps = new DeviceCapabilities
        {
            Name = VkNames.FromFixed(properties.deviceName),
            VendorId = properties.vendorID,
            DeviceId = properties.deviceID,
            Type = properties.deviceType,
            ApiVersion = properties.apiVersion,
            DriverVersion = properties.driverVersion,
        };

        VkPhysicalDeviceMemoryProperties memory;
        Api.vkGetPhysicalDeviceMemoryProperties(device, &memory);
        for (var i = 0; i < memory.memoryHeapCount; i++)
        {
            var heap = memory.memoryHeaps[i];
            if ((heap.flags & VkMemoryHeapFlags.DeviceLocal) != 0)
                caps.DeviceLocalMemory = Math.Max(caps.DeviceLocalMemory, heap.size);
        }

        caps.Vulkan13 = properties.apiVersion >= VkVersion.Version_1_3;
        if (!caps.Vulkan13)
            return caps;

        var extensions = VkNames.ToSet(EnumerateDeviceExtensions(device));
        caps.Swapchain = extensions.Contains(VkNames.ToString(VK_KHR_SWAPCHAIN_EXTENSION_NAME));
        caps.AccelerationStructure = extensions.Contains(VkNames.ToString(VK_KHR_ACCELERATION_STRUCTURE_EXTENSION_NAME))
                                     && extensions.Contains(VkNames.ToString(VK_KHR_DEFERRED_HOST_OPERATIONS_EXTENSION_NAME));
        caps.RayQuery = caps.AccelerationStructure && extensions.Contains(VkNames.ToString(VK_KHR_RAY_QUERY_EXTENSION_NAME));
        caps.MeshShader = extensions.Contains(VkNames.ToString(VK_EXT_MESH_SHADER_EXTENSION_NAME));

        var features13 = new VkPhysicalDeviceVulkan13Features();
        var features12 = new VkPhysicalDeviceVulkan12Features { pNext = &features13 };
        var features11 = new VkPhysicalDeviceVulkan11Features { pNext = &features12 };
        var features2 = new VkPhysicalDeviceFeatures2 { pNext = &features11 };
        Api.vkGetPhysicalDeviceFeatures2(device, &features2);

        caps.DynamicRendering = features13.dynamicRendering;
        caps.Synchronization2 = features13.synchronization2;
        caps.ShaderDrawParameters = features11.shaderDrawParameters;
        caps.TextureCompressionBC = features2.features.textureCompressionBC;
        caps.SamplerAnisotropy = features2.features.samplerAnisotropy;
        caps.TimelineSemaphore = features12.timelineSemaphore;
        caps.BufferDeviceAddress = features12.bufferDeviceAddress;
        caps.DescriptorIndexing = features12.descriptorIndexing
                                  && features12.runtimeDescriptorArray
                                  && features12.descriptorBindingPartiallyBound
                                  && features12.descriptorBindingVariableDescriptorCount
                                  && features12.shaderSampledImageArrayNonUniformIndexing
                                  && features12.descriptorBindingSampledImageUpdateAfterBind;
        return caps;
    }

    public List<string> EnumerateDeviceExtensions(VkPhysicalDevice device)
    {
        uint count;
        Api.vkEnumerateDeviceExtensionProperties(device, null, &count, null).CheckResult("vkEnumerateDeviceExtensionProperties");
        var properties = new VkExtensionProperties[count];
        fixed (VkExtensionProperties* p = properties)
        {
            Api.vkEnumerateDeviceExtensionProperties(device, null, &count, p).CheckResult("vkEnumerateDeviceExtensionProperties");
            var names = new List<string>((int)count);
            for (var i = 0; i < count; i++)
                names.Add(VkNames.FromFixed(p[i].extensionName));
            return names;
        }
    }

    private static List<string> EnumerateInstanceExtensions()
    {
        uint count;
        vkEnumerateInstanceExtensionProperties(null, &count, null).CheckResult("vkEnumerateInstanceExtensionProperties");
        var properties = new VkExtensionProperties[count];
        fixed (VkExtensionProperties* p = properties)
        {
            vkEnumerateInstanceExtensionProperties(null, &count, p).CheckResult("vkEnumerateInstanceExtensionProperties");
            var names = new List<string>((int)count);
            for (var i = 0; i < count; i++)
                names.Add(VkNames.FromFixed(p[i].extensionName));
            return names;
        }
    }

    private static List<string> EnumerateLayers()
    {
        uint count;
        vkEnumerateInstanceLayerProperties(&count, null).CheckResult("vkEnumerateInstanceLayerProperties");
        var properties = new VkLayerProperties[count];
        fixed (VkLayerProperties* p = properties)
        {
            vkEnumerateInstanceLayerProperties(&count, p).CheckResult("vkEnumerateInstanceLayerProperties");
            var names = new List<string>((int)count);
            for (var i = 0; i < count; i++)
                names.Add(VkNames.FromFixed(p[i].layerName));
            return names;
        }
    }

    private void CreateMessenger()
    {
        var createInfo = new VkDebugUtilsMessengerCreateInfoEXT
        {
            messageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.Warning | VkDebugUtilsMessageSeverityFlagsEXT.Error,
            messageType = VkDebugUtilsMessageTypeFlagsEXT.General | VkDebugUtilsMessageTypeFlagsEXT.Validation | VkDebugUtilsMessageTypeFlagsEXT.Performance,
            pfnUserCallback = &DebugCallback,
        };

        VkDebugUtilsMessengerEXT handle;
        Api.vkCreateDebugUtilsMessengerEXT(&createInfo, null, &handle).CheckResult("vkCreateDebugUtilsMessengerEXT");
        messenger = handle;
    }

    [UnmanagedCallersOnly]
    private static uint DebugCallback(VkDebugUtilsMessageSeverityFlagsEXT severity, VkDebugUtilsMessageTypeFlagsEXT types, VkDebugUtilsMessengerCallbackDataEXT* data, void* userData)
    {
        var message = $"Validation [{types}]: {Marshal.PtrToStringUTF8((IntPtr)data->pMessage)}";
        if ((severity & VkDebugUtilsMessageSeverityFlagsEXT.Error) != 0)
            RenderLog.Error(message);
        else
            RenderLog.Warning(message);
        return 0; // VK_FALSE: do not abort the call
    }

    public void Dispose()
    {
        if (Handle.IsNull)
            return;

        if (messenger.IsNotNull)
            Api.vkDestroyDebugUtilsMessengerEXT(messenger, null);

        Api.vkDestroyInstance(null);
        Handle = VkInstance.Null;
    }
}
