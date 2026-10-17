using System;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ClientPlugin.Render.Vulkan;

internal static unsafe class ShaderModule
{
    // code: SPIR-V words from ShaderBytecode (Tools/ShaderCompiler)
    public static VkShaderModule Create(VulkanDevice device, ReadOnlySpan<uint> code)
    {
        fixed (uint* words = code)
        {
            var createInfo = new VkShaderModuleCreateInfo
            {
                codeSize = (nuint)(code.Length * sizeof(uint)),
                pCode = words,
            };

            VkShaderModule module;
            device.Api.vkCreateShaderModule(&createInfo, null, &module).CheckResult("vkCreateShaderModule");
            return module;
        }
    }
}
