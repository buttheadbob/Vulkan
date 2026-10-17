using System;
using System.Collections.Generic;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ClientPlugin.Render.Vulkan;

// The global bindless descriptor set (set 0) and the pipeline layout every pipeline uses.
// Must match Shaders/Common/Bindless.slang.
//
// Texture slots are written once, when the texture is ready, and only reused after deferred destruction.
// A slot is therefore never updated while a frame in flight may read it (UPDATE_UNUSED_WHILE_PENDING).
internal sealed unsafe class BindlessDescriptors : IDisposable
{
    public const uint PushConstantSize = 128; // Guaranteed minimum of maxPushConstantsSize

    public enum SamplerSlot : uint
    {
        LinearClamp = 0,
        LinearWrap = 1,
        PointClamp = 2,
        AnisotropicWrap = 3,
    }

    public enum TextureSlot : uint
    {
        White = 0,
        Black = 1,
        Missing = 2,
        Transparent = 3,
    }

    private const uint FixedTextureSlots = 4;

    private readonly GpuContext context;
    private readonly VkSampler[] samplers = new VkSampler[4];
    private readonly GpuImage[] defaultTextures = new GpuImage[FixedTextureSlots];
    private readonly Stack<uint> freeSlots = new();
    private readonly object sync = new();
    private uint nextSlot = FixedTextureSlots;

    public uint MaxTextures { get; }
    public VkDescriptorSetLayout SetLayout { get; private set; }
    public VkDescriptorPool Pool { get; private set; }
    public VkDescriptorSet Set { get; private set; }
    public VkPipelineLayout PipelineLayout { get; private set; }

    public BindlessDescriptors(GpuContext context)
    {
        this.context = context;
        var api = context.Device.Api;

        VkPhysicalDeviceVulkan12Properties properties12 = new();
        var properties2 = new VkPhysicalDeviceProperties2 { pNext = &properties12 };
        context.Instance.Api.vkGetPhysicalDeviceProperties2(context.Device.PhysicalDevice, &properties2);
        MaxTextures = Math.Min(65536u, properties12.maxDescriptorSetUpdateAfterBindSampledImages);

        CreateSamplers(properties2.properties.limits.maxSamplerAnisotropy);
        CreateLayouts();
        CreateSet();
        CreateDefaultTextures();
    }

    private void CreateSamplers(float maxAnisotropy)
    {
        var api = context.Device.Api;

        VkSampler Create(VkFilter filter, VkSamplerMipmapMode mipmap, VkSamplerAddressMode address, float anisotropy)
        {
            var info = new VkSamplerCreateInfo
            {
                magFilter = filter,
                minFilter = filter,
                mipmapMode = mipmap,
                addressModeU = address,
                addressModeV = address,
                addressModeW = address,
                anisotropyEnable = anisotropy > 1,
                maxAnisotropy = anisotropy,
                minLod = 0,
                maxLod = VK_LOD_CLAMP_NONE,
            };
            VkSampler sampler;
            api.vkCreateSampler(&info, null, &sampler).CheckResult("vkCreateSampler");
            return sampler;
        }

        samplers[(int)SamplerSlot.LinearClamp] = Create(VkFilter.Linear, VkSamplerMipmapMode.Linear, VkSamplerAddressMode.ClampToEdge, 1);
        samplers[(int)SamplerSlot.LinearWrap] = Create(VkFilter.Linear, VkSamplerMipmapMode.Linear, VkSamplerAddressMode.Repeat, 1);
        samplers[(int)SamplerSlot.PointClamp] = Create(VkFilter.Nearest, VkSamplerMipmapMode.Nearest, VkSamplerAddressMode.ClampToEdge, 1);
        samplers[(int)SamplerSlot.AnisotropicWrap] = Create(VkFilter.Linear, VkSamplerMipmapMode.Linear, VkSamplerAddressMode.Repeat, Math.Min(16f, maxAnisotropy));
    }

    private void CreateLayouts()
    {
        var api = context.Device.Api;

        fixed (VkSampler* immutableSamplers = samplers)
        {
            var bindings = stackalloc VkDescriptorSetLayoutBinding[2];
            bindings[0] = new VkDescriptorSetLayoutBinding
            {
                binding = 0,
                descriptorType = VkDescriptorType.SampledImage,
                descriptorCount = MaxTextures,
                stageFlags = VkShaderStageFlags.All,
            };
            bindings[1] = new VkDescriptorSetLayoutBinding
            {
                binding = 1,
                descriptorType = VkDescriptorType.Sampler,
                descriptorCount = (uint)samplers.Length,
                stageFlags = VkShaderStageFlags.All,
                pImmutableSamplers = immutableSamplers,
            };

            var bindingFlags = stackalloc VkDescriptorBindingFlags[2];
            bindingFlags[0] = VkDescriptorBindingFlags.PartiallyBound
                              | VkDescriptorBindingFlags.UpdateAfterBind
                              | VkDescriptorBindingFlags.UpdateUnusedWhilePending;
            bindingFlags[1] = VkDescriptorBindingFlags.None;

            var flagsInfo = new VkDescriptorSetLayoutBindingFlagsCreateInfo
            {
                bindingCount = 2,
                pBindingFlags = bindingFlags,
            };
            var layoutInfo = new VkDescriptorSetLayoutCreateInfo
            {
                pNext = &flagsInfo,
                flags = VkDescriptorSetLayoutCreateFlags.UpdateAfterBindPool,
                bindingCount = 2,
                pBindings = bindings,
            };

            VkDescriptorSetLayout setLayout;
            api.vkCreateDescriptorSetLayout(&layoutInfo, null, &setLayout).CheckResult("vkCreateDescriptorSetLayout");
            SetLayout = setLayout;
        }

        var pushRange = new VkPushConstantRange
        {
            stageFlags = VkShaderStageFlags.All,
            offset = 0,
            size = PushConstantSize,
        };
        var layout = SetLayout;
        var pipelineLayoutInfo = new VkPipelineLayoutCreateInfo
        {
            setLayoutCount = 1,
            pSetLayouts = &layout,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &pushRange,
        };

        VkPipelineLayout pipelineLayout;
        api.vkCreatePipelineLayout(&pipelineLayoutInfo, null, &pipelineLayout).CheckResult("vkCreatePipelineLayout");
        PipelineLayout = pipelineLayout;
    }

    private void CreateSet()
    {
        var api = context.Device.Api;

        var poolSizes = stackalloc VkDescriptorPoolSize[2];
        poolSizes[0] = new VkDescriptorPoolSize { type = VkDescriptorType.SampledImage, descriptorCount = MaxTextures };
        poolSizes[1] = new VkDescriptorPoolSize { type = VkDescriptorType.Sampler, descriptorCount = (uint)samplers.Length };

        var poolInfo = new VkDescriptorPoolCreateInfo
        {
            flags = VkDescriptorPoolCreateFlags.UpdateAfterBind,
            maxSets = 1,
            poolSizeCount = 2,
            pPoolSizes = poolSizes,
        };

        VkDescriptorPool pool;
        api.vkCreateDescriptorPool(&poolInfo, null, &pool).CheckResult("vkCreateDescriptorPool");
        Pool = pool;

        var layout = SetLayout;
        var allocInfo = new VkDescriptorSetAllocateInfo
        {
            descriptorPool = pool,
            descriptorSetCount = 1,
            pSetLayouts = &layout,
        };

        VkDescriptorSet set;
        api.vkAllocateDescriptorSets(&allocInfo, &set).CheckResult("vkAllocateDescriptorSets");
        Set = set;
    }

    private void CreateDefaultTextures()
    {
        // RGBA8, sRGB: white, black, magenta/black checker (missing), transparent
        Create(TextureSlot.White, 1, [0xFF, 0xFF, 0xFF, 0xFF]);
        Create(TextureSlot.Black, 1, [0x00, 0x00, 0x00, 0xFF]);
        Create(TextureSlot.Missing, 2, [0xFF, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0xFF, 0xFF]);
        Create(TextureSlot.Transparent, 1, [0x00, 0x00, 0x00, 0x00]);

        void Create(TextureSlot slot, uint size, byte[] pixels)
        {
            var image = GpuImage.Create2D(context, size, size, 1, 1, VkFormat.R8G8B8A8Srgb, VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst);
            var region = new VkBufferImageCopy
            {
                imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
                imageExtent = new VkExtent3D(size, size, 1),
            };
            context.Uploads.UploadImage(image, pixels, [region]);
            defaultTextures[(int)slot] = image;
            WriteTexture((uint)slot, image.View);
        }
    }

    // Reserves a slot for a texture. Write it with WriteTexture once the image is ready.
    public uint AllocateTextureSlot()
    {
        lock (sync)
        {
            if (freeSlots.Count != 0)
                return freeSlots.Pop();

            if (nextSlot >= MaxTextures)
                throw new InvalidOperationException($"Out of bindless texture slots ({MaxTextures})");

            return nextSlot++;
        }
    }

    // Returns a slot once no frame in flight can reference it anymore
    public void FreeTextureSlot(uint slot)
    {
        if (slot < FixedTextureSlots)
            return;

        context.Release(() =>
        {
            lock (sync)
                freeSlots.Push(slot);
        });
    }

    public void WriteTexture(uint slot, VkImageView view)
    {
        var imageInfo = new VkDescriptorImageInfo
        {
            imageView = view,
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };
        var write = new VkWriteDescriptorSet
        {
            dstSet = Set,
            dstBinding = 0,
            dstArrayElement = slot,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.SampledImage,
            pImageInfo = &imageInfo,
        };
        context.Device.Api.vkUpdateDescriptorSets(1, &write, 0, null);
    }

    public void Bind(VkCommandBuffer commands, VkPipelineBindPoint bindPoint)
    {
        var set = Set;
        context.Device.Api.vkCmdBindDescriptorSets(commands, bindPoint, PipelineLayout, 0, 1, &set, 0, null);
    }

    public void Dispose()
    {
        var api = context.Device.Api;
        foreach (var texture in defaultTextures)
            texture?.Dispose();

        api.vkDestroyPipelineLayout(PipelineLayout, null);
        api.vkDestroyDescriptorPool(Pool, null);
        api.vkDestroyDescriptorSetLayout(SetLayout, null);
        foreach (var sampler in samplers)
            api.vkDestroySampler(sampler, null);
    }
}
