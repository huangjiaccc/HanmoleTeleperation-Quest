#define VK_USE_PLATFORM_ANDROID_KHR
#include <jni.h>
#include <android/hardware_buffer_jni.h>
#include <android/hardware_buffer.h>
#include <android/log.h>
#include <vulkan/vulkan.h>

#include <unistd.h>
#include <poll.h>

#include <chrono>
#include <condition_variable>
#include <atomic>
#include <cstring>
#include <cstdint>
#include <memory>
#include <mutex>
#include <vector>

#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsVulkan.h"

#include "shaders/ycbcr_fullscreen_vert_spv.h"
#include "shaders/ycbcr_fullscreen_frag_spv.h"

#define LOG_TAG "QuestVulkanExt"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

#define QUEST_VULKANEXT_BUILD_TAG "ycbcr_manual_yuv_v6"

#ifndef UNITY_INTERFACE_EXPORT
#define UNITY_INTERFACE_EXPORT
#endif

namespace
{
    IUnityInterfaces* g_unityInterfaces = nullptr;
    IUnityGraphics* g_graphics = nullptr;
    IUnityGraphicsVulkan* g_graphicsVulkan = nullptr;
    UnityVulkanInstance g_vulkanInstance = {};
    bool g_vulkanReady = false;

    PFN_vkGetDeviceProcAddr g_vkGetDeviceProcAddr = nullptr;
    PFN_vkGetPhysicalDeviceMemoryProperties g_vkGetPhysicalDeviceMemoryProperties = nullptr;
    PFN_vkCreateImage g_vkCreateImage = nullptr;
    PFN_vkDestroyImage g_vkDestroyImage = nullptr;
    PFN_vkGetImageMemoryRequirements g_vkGetImageMemoryRequirements = nullptr;
    PFN_vkAllocateMemory g_vkAllocateMemory = nullptr;
    PFN_vkFreeMemory g_vkFreeMemory = nullptr;
    PFN_vkBindImageMemory g_vkBindImageMemory = nullptr;
    PFN_vkCreateImageView g_vkCreateImageView = nullptr;
    PFN_vkDestroyImageView g_vkDestroyImageView = nullptr;
    PFN_vkCmdPipelineBarrier g_vkCmdPipelineBarrier = nullptr;
    PFN_vkGetAndroidHardwareBufferPropertiesANDROID g_vkGetAndroidHardwareBufferPropertiesANDROID = nullptr;
    PFN_vkCreateSampler g_vkCreateSampler = nullptr;
    PFN_vkDestroySampler g_vkDestroySampler = nullptr;
    PFN_vkCreateSamplerYcbcrConversion g_vkCreateSamplerYcbcrConversion = nullptr;
    PFN_vkDestroySamplerYcbcrConversion g_vkDestroySamplerYcbcrConversion = nullptr;
    PFN_vkCreateShaderModule g_vkCreateShaderModule = nullptr;
    PFN_vkDestroyShaderModule g_vkDestroyShaderModule = nullptr;
    PFN_vkCreateDescriptorSetLayout g_vkCreateDescriptorSetLayout = nullptr;
    PFN_vkDestroyDescriptorSetLayout g_vkDestroyDescriptorSetLayout = nullptr;
    PFN_vkCreateDescriptorPool g_vkCreateDescriptorPool = nullptr;
    PFN_vkDestroyDescriptorPool g_vkDestroyDescriptorPool = nullptr;
    PFN_vkAllocateDescriptorSets g_vkAllocateDescriptorSets = nullptr;
    PFN_vkUpdateDescriptorSets g_vkUpdateDescriptorSets = nullptr;
    PFN_vkCreatePipelineLayout g_vkCreatePipelineLayout = nullptr;
    PFN_vkDestroyPipelineLayout g_vkDestroyPipelineLayout = nullptr;
    PFN_vkCreateRenderPass g_vkCreateRenderPass = nullptr;
    PFN_vkDestroyRenderPass g_vkDestroyRenderPass = nullptr;
    PFN_vkCreateFramebuffer g_vkCreateFramebuffer = nullptr;
    PFN_vkDestroyFramebuffer g_vkDestroyFramebuffer = nullptr;
    PFN_vkCreateGraphicsPipelines g_vkCreateGraphicsPipelines = nullptr;
    PFN_vkDestroyPipeline g_vkDestroyPipeline = nullptr;
    PFN_vkCmdBeginRenderPass g_vkCmdBeginRenderPass = nullptr;
    PFN_vkCmdEndRenderPass g_vkCmdEndRenderPass = nullptr;
    PFN_vkCmdBindPipeline g_vkCmdBindPipeline = nullptr;
    PFN_vkCmdBindDescriptorSets g_vkCmdBindDescriptorSets = nullptr;
    PFN_vkCmdSetViewport g_vkCmdSetViewport = nullptr;
    PFN_vkCmdSetScissor g_vkCmdSetScissor = nullptr;
    PFN_vkCmdDraw g_vkCmdDraw = nullptr;
    PFN_vkCmdCopyImage g_vkCmdCopyImage = nullptr;
    PFN_vkCmdPushConstants g_vkCmdPushConstants = nullptr;
    PFN_vkCreateSemaphore g_vkCreateSemaphore = nullptr;
    PFN_vkDestroySemaphore g_vkDestroySemaphore = nullptr;
    PFN_vkImportSemaphoreFdKHR g_vkImportSemaphoreFdKHR = nullptr;
    PFN_vkWaitSemaphores g_vkWaitSemaphores = nullptr;

    enum class TexturePendingOp
    {
        None,
        Create,
        BindUnityTexture,
        ImportHardwareBuffer,
        Destroy,
    };

    struct ExternalTexture
    {
        int width = 0;
        int height = 0;
        AHardwareBuffer* buffer = nullptr;
        AHardwareBuffer* pendingBuffer = nullptr;
        int pendingFenceFd = -1;
        UnityVulkanImage unityImage{};
        VkDeviceMemory memory = VK_NULL_HANDLE;
        VkImageView imageView = VK_NULL_HANDLE;
        VkFramebuffer framebuffer = VK_NULL_HANDLE;
        VkDescriptorPool descriptorPool = VK_NULL_HANDLE;
        VkDescriptorSet descriptorSet = VK_NULL_HANDLE;
        void* unityTexturePtr = nullptr;
        VkFormat unityTextureFormat = VK_FORMAT_UNDEFINED;
        bool unityTextureFormatLogged = false;
        UnityVulkanImage originalUnityImage{};
        bool hasOriginalUnityImage = false;
        bool unityTextureBound = false;

        std::mutex stateMutex;
        std::condition_variable stateCondition;
        TexturePendingOp pendingOp = TexturePendingOp::None;
        bool gpuReady = false;
        bool alive = true;
        bool lastResult = false;
    };

    struct DeferredYcbcrSourceRelease
    {
        uint64_t usedFrame = 0;
        VkImage image = VK_NULL_HANDLE;
        VkImageView view = VK_NULL_HANDLE;
        VkDeviceMemory memory = VK_NULL_HANDLE;
        AHardwareBuffer* buffer = nullptr;
    };

    std::mutex g_deferredReleaseMutex;
    std::vector<DeferredYcbcrSourceRelease> g_deferredYcbcrReleases;

    void PurgeDeferredYcbcrReleases(uint64_t safeFrameNumber)
    {
        if (!g_vulkanReady)
        {
            return;
        }

        std::lock_guard<std::mutex> lock(g_deferredReleaseMutex);
        size_t writeIndex = 0;
        for (size_t i = 0; i < g_deferredYcbcrReleases.size(); ++i)
        {
            DeferredYcbcrSourceRelease item = g_deferredYcbcrReleases[i];
            if (item.usedFrame <= safeFrameNumber)
            {
                if (item.view != VK_NULL_HANDLE && g_vkDestroyImageView)
                {
                    g_vkDestroyImageView(g_vulkanInstance.device, item.view, nullptr);
                }
                if (item.image != VK_NULL_HANDLE && g_vkDestroyImage)
                {
                    g_vkDestroyImage(g_vulkanInstance.device, item.image, nullptr);
                }
                if (item.memory != VK_NULL_HANDLE && g_vkFreeMemory)
                {
                    g_vkFreeMemory(g_vulkanInstance.device, item.memory, nullptr);
                }
                if (item.buffer)
                {
                    AHardwareBuffer_release(item.buffer);
                }
                continue;
            }

            g_deferredYcbcrReleases[writeIndex++] = item;
        }
        g_deferredYcbcrReleases.resize(writeIndex);
    }

    void DestroyNativeImage(ExternalTexture& texture);
    void ClearDeferredYcbcrReleases();
    bool TransitionImageToShaderRead(VkCommandBuffer commandBuffer, ExternalTexture& texture);
    bool CreateNativeImage(ExternalTexture& texture, VkCommandBuffer commandBuffer, VkFormat desiredFormat);
    void RestoreUnityTexture(ExternalTexture& texture);
    bool BindUnityTexture(ExternalTexture& texture);

    const char* VkFormatName(VkFormat format)
    {
        switch (format)
        {
            case VK_FORMAT_R8G8B8A8_UNORM:
                return "VK_FORMAT_R8G8B8A8_UNORM";
            case VK_FORMAT_R8G8B8A8_SRGB:
                return "VK_FORMAT_R8G8B8A8_SRGB";
            case VK_FORMAT_B8G8R8A8_UNORM:
                return "VK_FORMAT_B8G8R8A8_UNORM";
            case VK_FORMAT_B8G8R8A8_SRGB:
                return "VK_FORMAT_B8G8R8A8_SRGB";
            default:
                return "VK_FORMAT_(other)";
        }
    }

    struct YcbcrPipelineState
    {
        VkFormat outputFormat = VK_FORMAT_UNDEFINED;
        VkRenderPass renderPass = VK_NULL_HANDLE;
        VkDescriptorSetLayout descriptorSetLayout = VK_NULL_HANDLE;
        VkPipelineLayout pipelineLayout = VK_NULL_HANDLE;
        VkPipeline pipeline = VK_NULL_HANDLE;
        VkShaderModule vertModule = VK_NULL_HANDLE;
        VkShaderModule fragModule = VK_NULL_HANDLE;
    };

    struct YcbcrSamplerState
    {
        uint64_t externalFormat = 0;
        uint32_t overrideGeneration = 0;
        VkSamplerYcbcrConversion conversion = VK_NULL_HANDLE;
        VkSampler sampler = VK_NULL_HANDLE;
        VkSamplerYcbcrModelConversion model = VK_SAMPLER_YCBCR_MODEL_CONVERSION_RGB_IDENTITY;
        VkSamplerYcbcrRange range = VK_SAMPLER_YCBCR_RANGE_ITU_FULL;
        VkComponentMapping components{};
        VkChromaLocation xChromaOffset = VK_CHROMA_LOCATION_COSITED_EVEN;
        VkChromaLocation yChromaOffset = VK_CHROMA_LOCATION_COSITED_EVEN;
    };

    YcbcrPipelineState g_ycbcrPipeline{};
    YcbcrSamplerState g_ycbcrSampler{};

    struct YcbcrOverrideState
    {
        std::atomic<int> modelOverride{-1}; // Vulkan enum value; -1 = use suggested/heuristic
        std::atomic<int> rangeOverride{-1}; // Vulkan enum value; -1 = use suggested/heuristic
        // 0 = none, 1 = swap G/B (legacy), 2 = swap R/B
        std::atomic<int> swizzleMode{0};
        // -1 = use suggested, else VkChromaLocation enum value
        std::atomic<int> xChromaOffsetOverride{-1};
        std::atomic<int> yChromaOffsetOverride{-1};
        // Manual YUV->RGB conversion in shader (forces sampler model to YCBCR_IDENTITY)
        std::atomic<int> manualYuv{1};
        // Swap U/V before matrix conversion
        std::atomic<int> swapUv{0};
        // Invert U or V (u = 1-u), useful when chroma is flipped.
        std::atomic<int> invertU{0};
        std::atomic<int> invertV{0};
        // Which sampled channel is Y/U/V (0..5 permutation index)
        std::atomic<int> channelOrder{0};
        // Debug output: 0=normal, 1=show Y, 2=show U, 3=show V
        std::atomic<int> debugMode{0};
        // 0=normalized, 1=byte-narrow (Java-like), 2=byte-full
        std::atomic<int> inputMode{0};
        // When enabled, decoder-provided color info overrides Unity/C# overrides.
        std::atomic<int> useDecoderColorInfo{0};
        std::atomic<int> decoderColorStandard{-1};
        std::atomic<int> decoderColorRange{-1};
        std::atomic<int> decoderColorTransfer{-1};
        std::atomic<int> decoderColorFormat{-1};
        std::atomic<uint32_t> generation{1};
    };

    YcbcrOverrideState g_ycbcrOverride{};
    struct ColorTransformState
    {
        std::atomic<float> mulR{1.0f};
        std::atomic<float> mulG{1.0f};
        std::atomic<float> mulB{1.0f};
        std::atomic<float> mulA{1.0f};
        std::atomic<float> addR{0.0f};
        std::atomic<float> addG{0.0f};
        std::atomic<float> addB{0.0f};
        std::atomic<float> addA{0.0f};
    };
    ColorTransformState g_colorTransform{};
    uint64_t g_lastLoggedExternalFormat = 0;
    std::atomic<uint64_t> g_lastImportDescLogMs{0};
    std::atomic<uint64_t> g_lastImportFormatLogMs{0};
    std::atomic<uint64_t> g_lastExternalFormatConvertLogMs{0};
    std::atomic<uint64_t> g_lastRenderEventLogMs{0};

    uint64_t GetMonotonicTimeMs()
    {
        return static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now().time_since_epoch())
                .count());
    }

    bool ShouldLogThrottled(std::atomic<uint64_t>& lastLogMs, uint64_t intervalMs)
    {
        const uint64_t now = GetMonotonicTimeMs();
        const uint64_t last = lastLogMs.load(std::memory_order_relaxed);
        if (last != 0 && now > last && (now - last) < intervalMs)
        {
            return false;
        }

        lastLogMs.store(now, std::memory_order_relaxed);
        return true;
    }

    int MapAndroidColorStandardToVk(int standard)
    {
        // Android MediaFormat COLOR_STANDARD_* values:
        // 1=BT709, 2=BT601_PAL, 4=BT601_NTSC, 6=BT2020.
        switch (standard)
        {
            case 1:
                return static_cast<int>(VK_SAMPLER_YCBCR_MODEL_CONVERSION_YCBCR_709);
            case 2:
            case 4:
                return static_cast<int>(VK_SAMPLER_YCBCR_MODEL_CONVERSION_YCBCR_601);
            case 6:
                return static_cast<int>(VK_SAMPLER_YCBCR_MODEL_CONVERSION_YCBCR_2020);
            default:
                return -1;
        }
    }

    int MapAndroidColorRangeToVk(int range)
    {
        // Android MediaFormat COLOR_RANGE_* values: 1=FULL, 2=LIMITED.
        switch (range)
        {
            case 1:
                return static_cast<int>(VK_SAMPLER_YCBCR_RANGE_ITU_FULL);
            case 2:
                return static_cast<int>(VK_SAMPLER_YCBCR_RANGE_ITU_NARROW);
            default:
                return -1;
        }
    }

    int MapAndroidRangeToInputMode(int range)
    {
        // For manual YUV conversion: narrow->Java byte narrow, full->byte full.
        switch (range)
        {
            case 1:
                return 2; // byte-full
            case 2:
                return 1; // byte-narrow (Java)
            default:
                return 0; // normalized
        }
    }


    int SwizzleToChannelIndex(VkComponentSwizzle swizzle)
    {
        switch (swizzle)
        {
            case VK_COMPONENT_SWIZZLE_R:
                return 0;
            case VK_COMPONENT_SWIZZLE_G:
                return 1;
            case VK_COMPONENT_SWIZZLE_B:
                return 2;
            default:
                return -1;
        }
    }

    int ComputeChannelOrderFromComponents(VkComponentMapping components)
    {
        // In manual YUV mode we expect the sampler to return the conversion result channels
        // (R,G,B) carrying (Y,U,V) respectively when model is YCBCR_IDENTITY.
        // components.{r,g,b} can reorder those channels; map it to the shader's channelOrder permutation.

        // Expand IDENTITY to explicit R/G/B to make the mapping deterministic.
        auto canonical = [](VkComponentSwizzle value, VkComponentSwizzle identityReplacement) {
            return value == VK_COMPONENT_SWIZZLE_IDENTITY ? identityReplacement : value;
        };

        components.r = canonical(components.r, VK_COMPONENT_SWIZZLE_R);
        components.g = canonical(components.g, VK_COMPONENT_SWIZZLE_G);
        components.b = canonical(components.b, VK_COMPONENT_SWIZZLE_B);

        const int rSrc = SwizzleToChannelIndex(components.r);
        const int gSrc = SwizzleToChannelIndex(components.g);
        const int bSrc = SwizzleToChannelIndex(components.b);
        if (rSrc < 0 || gSrc < 0 || bSrc < 0)
        {
            return -1;
        }

        // Each source channel index corresponds to Y/U/V in order: 0=Y, 1=U, 2=V.
        int sampleFrom[3] = {rSrc, gSrc, bSrc};

        int posY = -1;
        int posU = -1;
        int posV = -1;
        for (int i = 0; i < 3; ++i)
        {
            if (sampleFrom[i] == 0) posY = i;
            else if (sampleFrom[i] == 1) posU = i;
            else if (sampleFrom[i] == 2) posV = i;
        }
        if (posY < 0 || posU < 0 || posV < 0)
        {
            return -1;
        }

        // Shader channelOrder enum:
        // 0=YUV (y=c0 u=c1 v=c2)
        // 1=YVU (y=c0 u=c2 v=c1)
        // 2=UYV (y=c1 u=c0 v=c2)
        // 3=UVY (y=c2 u=c0 v=c1)
        // 4=VYU (y=c1 u=c2 v=c0)
        // 5=VUY (y=c2 u=c1 v=c0)
        if (posY == 0 && posU == 1 && posV == 2) return 0;
        if (posY == 0 && posU == 2 && posV == 1) return 1;
        if (posY == 1 && posU == 0 && posV == 2) return 2;
        if (posY == 2 && posU == 0 && posV == 1) return 3;
        if (posY == 1 && posU == 2 && posV == 0) return 4;
        if (posY == 2 && posU == 1 && posV == 0) return 5;
        return -1;
    }

    void ApplyDecoderColorInfo(int standard, int range, int transfer, int format)
    {
        const int mappedModel = MapAndroidColorStandardToVk(standard);
        const int mappedRange = MapAndroidColorRangeToVk(range);
        const int manualYuv = g_ycbcrOverride.manualYuv.load(std::memory_order_relaxed);

        g_ycbcrOverride.useDecoderColorInfo.store(1, std::memory_order_relaxed);
        g_ycbcrOverride.decoderColorStandard.store(standard, std::memory_order_relaxed);
        g_ycbcrOverride.decoderColorRange.store(range, std::memory_order_relaxed);
        g_ycbcrOverride.decoderColorTransfer.store(transfer, std::memory_order_relaxed);
        g_ycbcrOverride.decoderColorFormat.store(format, std::memory_order_relaxed);

        g_ycbcrOverride.modelOverride.store(mappedModel, std::memory_order_relaxed);
        g_ycbcrOverride.rangeOverride.store(mappedRange, std::memory_order_relaxed);
        if (manualYuv != 0)
        {
            int inputMode = MapAndroidRangeToInputMode(range);
            g_ycbcrOverride.inputMode.store(inputMode, std::memory_order_relaxed);
        }

        g_ycbcrOverride.generation.fetch_add(1, std::memory_order_relaxed);

        LOGI("QuestVulkan_SetDecoderColorInfo standard=%d range=%d transfer=%d format=%d -> model=%d range=%d manualYuv=%d inputMode=%d",
             standard,
             range,
             transfer,
             format,
             mappedModel,
             mappedRange,
             manualYuv,
             g_ycbcrOverride.inputMode.load(std::memory_order_relaxed));
    }

    void DestroyYcbcrPipeline()
    {
        if (!g_vulkanReady)
        {
            g_ycbcrPipeline = {};
            return;
        }

        if (g_ycbcrPipeline.pipeline != VK_NULL_HANDLE && g_vkDestroyPipeline)
        {
            g_vkDestroyPipeline(g_vulkanInstance.device, g_ycbcrPipeline.pipeline, nullptr);
        }
        if (g_ycbcrPipeline.pipelineLayout != VK_NULL_HANDLE && g_vkDestroyPipelineLayout)
        {
            g_vkDestroyPipelineLayout(g_vulkanInstance.device, g_ycbcrPipeline.pipelineLayout, nullptr);
        }
        if (g_ycbcrPipeline.descriptorSetLayout != VK_NULL_HANDLE && g_vkDestroyDescriptorSetLayout)
        {
            g_vkDestroyDescriptorSetLayout(g_vulkanInstance.device, g_ycbcrPipeline.descriptorSetLayout, nullptr);
        }
        if (g_ycbcrPipeline.renderPass != VK_NULL_HANDLE && g_vkDestroyRenderPass)
        {
            g_vkDestroyRenderPass(g_vulkanInstance.device, g_ycbcrPipeline.renderPass, nullptr);
        }
        if (g_ycbcrPipeline.vertModule != VK_NULL_HANDLE && g_vkDestroyShaderModule)
        {
            g_vkDestroyShaderModule(g_vulkanInstance.device, g_ycbcrPipeline.vertModule, nullptr);
        }
        if (g_ycbcrPipeline.fragModule != VK_NULL_HANDLE && g_vkDestroyShaderModule)
        {
            g_vkDestroyShaderModule(g_vulkanInstance.device, g_ycbcrPipeline.fragModule, nullptr);
        }

        g_ycbcrPipeline = {};
    }

    void DestroyYcbcrSampler()
    {
        if (!g_vulkanReady)
        {
            g_ycbcrSampler = {};
            return;
        }

        if (g_ycbcrSampler.sampler != VK_NULL_HANDLE && g_vkDestroySampler)
        {
            g_vkDestroySampler(g_vulkanInstance.device, g_ycbcrSampler.sampler, nullptr);
        }
        if (g_ycbcrSampler.conversion != VK_NULL_HANDLE && g_vkDestroySamplerYcbcrConversion)
        {
            g_vkDestroySamplerYcbcrConversion(g_vulkanInstance.device, g_ycbcrSampler.conversion, nullptr);
        }
        g_ycbcrSampler = {};
    }

    bool TransitionImageLayout(
        VkCommandBuffer commandBuffer,
        VkImage image,
        VkImageLayout oldLayout,
        VkImageLayout newLayout,
        VkAccessFlags srcAccessMask,
        VkAccessFlags dstAccessMask,
        VkPipelineStageFlags srcStage,
        VkPipelineStageFlags dstStage)
    {
        if (commandBuffer == VK_NULL_HANDLE || !g_vkCmdPipelineBarrier)
        {
            return false;
        }
        if (image == VK_NULL_HANDLE)
        {
            return false;
        }

        VkImageMemoryBarrier barrier{VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER};
        barrier.srcAccessMask = srcAccessMask;
        barrier.dstAccessMask = dstAccessMask;
        barrier.oldLayout = oldLayout;
        barrier.newLayout = newLayout;
        barrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        barrier.image = image;
        barrier.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        barrier.subresourceRange.baseMipLevel = 0;
        barrier.subresourceRange.levelCount = 1;
        barrier.subresourceRange.baseArrayLayer = 0;
        barrier.subresourceRange.layerCount = 1;

        g_vkCmdPipelineBarrier(commandBuffer,
                               srcStage,
                               dstStage,
                               0,
                               0,
                               nullptr,
                               0,
                               nullptr,
                               1,
                               &barrier);
        return true;
    }

    bool EnsureYcbcrPipeline(VkFormat outputFormat)
    {
        if (!g_vulkanReady ||
            !g_vkCreateShaderModule ||
            !g_vkDestroyShaderModule ||
            !g_vkCreateDescriptorSetLayout ||
            !g_vkDestroyDescriptorSetLayout ||
            !g_vkCreatePipelineLayout ||
            !g_vkDestroyPipelineLayout ||
            !g_vkCreateRenderPass ||
            !g_vkDestroyRenderPass ||
            !g_vkCreateGraphicsPipelines ||
            !g_vkDestroyPipeline)
        {
            LOGE("EnsureYcbcrPipeline: missing Vulkan functions.");
            return false;
        }

        if (g_ycbcrPipeline.pipeline != VK_NULL_HANDLE &&
            g_ycbcrPipeline.outputFormat == outputFormat)
        {
            return true;
        }

        DestroyYcbcrPipeline();
        g_ycbcrPipeline.outputFormat = outputFormat;

        VkDescriptorSetLayoutBinding binding{};
        binding.binding = 0;
        binding.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
        binding.descriptorCount = 1;
        binding.stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;

        VkDescriptorSetLayoutCreateInfo setLayoutInfo{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO};
        setLayoutInfo.bindingCount = 1;
        setLayoutInfo.pBindings = &binding;
        if (g_vkCreateDescriptorSetLayout(g_vulkanInstance.device, &setLayoutInfo, nullptr, &g_ycbcrPipeline.descriptorSetLayout) != VK_SUCCESS)
        {
            LOGE("EnsureYcbcrPipeline: vkCreateDescriptorSetLayout failed.");
            DestroyYcbcrPipeline();
            return false;
        }

        VkPushConstantRange pushRange{};
        pushRange.stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
        pushRange.offset = 0;
        pushRange.size = sizeof(int32_t) * 4 + sizeof(float) * 8;

        VkPipelineLayoutCreateInfo pipelineLayoutInfo{VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO};
        pipelineLayoutInfo.setLayoutCount = 1;
        pipelineLayoutInfo.pSetLayouts = &g_ycbcrPipeline.descriptorSetLayout;
        pipelineLayoutInfo.pushConstantRangeCount = 1;
        pipelineLayoutInfo.pPushConstantRanges = &pushRange;
        if (g_vkCreatePipelineLayout(g_vulkanInstance.device, &pipelineLayoutInfo, nullptr, &g_ycbcrPipeline.pipelineLayout) != VK_SUCCESS)
        {
            LOGE("EnsureYcbcrPipeline: vkCreatePipelineLayout failed.");
            DestroyYcbcrPipeline();
            return false;
        }

        VkAttachmentDescription colorAttachment{};
        colorAttachment.format = outputFormat;
        colorAttachment.samples = VK_SAMPLE_COUNT_1_BIT;
        colorAttachment.loadOp = VK_ATTACHMENT_LOAD_OP_CLEAR;
        colorAttachment.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
        colorAttachment.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
        colorAttachment.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
        colorAttachment.initialLayout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;
        colorAttachment.finalLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;

        VkAttachmentReference colorRef{};
        colorRef.attachment = 0;
        colorRef.layout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;

        VkSubpassDescription subpass{};
        subpass.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
        subpass.colorAttachmentCount = 1;
        subpass.pColorAttachments = &colorRef;

        VkSubpassDependency deps[2]{};
        deps[0].srcSubpass = VK_SUBPASS_EXTERNAL;
        deps[0].dstSubpass = 0;
        deps[0].srcStageMask = VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT;
        deps[0].dstStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
        deps[0].srcAccessMask = VK_ACCESS_SHADER_READ_BIT;
        deps[0].dstAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
        deps[0].dependencyFlags = VK_DEPENDENCY_BY_REGION_BIT;

        deps[1].srcSubpass = 0;
        deps[1].dstSubpass = VK_SUBPASS_EXTERNAL;
        deps[1].srcStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
        deps[1].dstStageMask = VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT;
        deps[1].srcAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
        deps[1].dstAccessMask = VK_ACCESS_SHADER_READ_BIT;
        deps[1].dependencyFlags = VK_DEPENDENCY_BY_REGION_BIT;

        VkRenderPassCreateInfo rpInfo{VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO};
        rpInfo.attachmentCount = 1;
        rpInfo.pAttachments = &colorAttachment;
        rpInfo.subpassCount = 1;
        rpInfo.pSubpasses = &subpass;
        rpInfo.dependencyCount = 2;
        rpInfo.pDependencies = deps;

        if (g_vkCreateRenderPass(g_vulkanInstance.device, &rpInfo, nullptr, &g_ycbcrPipeline.renderPass) != VK_SUCCESS)
        {
            LOGE("EnsureYcbcrPipeline: vkCreateRenderPass failed.");
            DestroyYcbcrPipeline();
            return false;
        }

        VkShaderModuleCreateInfo vsInfo{VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO};
        vsInfo.codeSize = static_cast<size_t>(kYcbcrFullscreenVertSpvWordCount) * sizeof(uint32_t);
        vsInfo.pCode = kYcbcrFullscreenVertSpv;
        if (g_vkCreateShaderModule(g_vulkanInstance.device, &vsInfo, nullptr, &g_ycbcrPipeline.vertModule) != VK_SUCCESS)
        {
            LOGE("EnsureYcbcrPipeline: vkCreateShaderModule(vert) failed.");
            DestroyYcbcrPipeline();
            return false;
        }

        VkShaderModuleCreateInfo fsInfo{VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO};
        fsInfo.codeSize = static_cast<size_t>(kYcbcrFullscreenFragSpvWordCount) * sizeof(uint32_t);
        fsInfo.pCode = kYcbcrFullscreenFragSpv;
        if (g_vkCreateShaderModule(g_vulkanInstance.device, &fsInfo, nullptr, &g_ycbcrPipeline.fragModule) != VK_SUCCESS)
        {
            LOGE("EnsureYcbcrPipeline: vkCreateShaderModule(frag) failed.");
            DestroyYcbcrPipeline();
            return false;
        }

        VkPipelineShaderStageCreateInfo stages[2]{};
        stages[0].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
        stages[0].stage = VK_SHADER_STAGE_VERTEX_BIT;
        stages[0].module = g_ycbcrPipeline.vertModule;
        stages[0].pName = "main";
        stages[1].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
        stages[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT;
        stages[1].module = g_ycbcrPipeline.fragModule;
        stages[1].pName = "main";

        VkPipelineVertexInputStateCreateInfo vertexInput{VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO};

        VkPipelineInputAssemblyStateCreateInfo inputAssembly{VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO};
        inputAssembly.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;

        VkPipelineViewportStateCreateInfo viewportState{VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO};
        viewportState.viewportCount = 1;
        viewportState.scissorCount = 1;

        VkPipelineRasterizationStateCreateInfo raster{VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO};
        raster.polygonMode = VK_POLYGON_MODE_FILL;
        raster.cullMode = VK_CULL_MODE_NONE;
        raster.frontFace = VK_FRONT_FACE_CLOCKWISE;
        raster.lineWidth = 1.0f;

        VkPipelineMultisampleStateCreateInfo msaa{VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO};
        msaa.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;

        VkPipelineColorBlendAttachmentState blendAttachment{};
        blendAttachment.colorWriteMask =
            VK_COLOR_COMPONENT_R_BIT | VK_COLOR_COMPONENT_G_BIT | VK_COLOR_COMPONENT_B_BIT | VK_COLOR_COMPONENT_A_BIT;

        VkPipelineColorBlendStateCreateInfo blend{VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO};
        blend.attachmentCount = 1;
        blend.pAttachments = &blendAttachment;

        VkDynamicState dynStates[] = {VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR};
        VkPipelineDynamicStateCreateInfo dyn{VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO};
        dyn.dynamicStateCount = 2;
        dyn.pDynamicStates = dynStates;

        VkGraphicsPipelineCreateInfo pipeInfo{VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO};
        pipeInfo.stageCount = 2;
        pipeInfo.pStages = stages;
        pipeInfo.pVertexInputState = &vertexInput;
        pipeInfo.pInputAssemblyState = &inputAssembly;
        pipeInfo.pViewportState = &viewportState;
        pipeInfo.pRasterizationState = &raster;
        pipeInfo.pMultisampleState = &msaa;
        pipeInfo.pColorBlendState = &blend;
        pipeInfo.pDynamicState = &dyn;
        pipeInfo.layout = g_ycbcrPipeline.pipelineLayout;
        pipeInfo.renderPass = g_ycbcrPipeline.renderPass;
        pipeInfo.subpass = 0;

        if (g_vkCreateGraphicsPipelines(g_vulkanInstance.device, VK_NULL_HANDLE, 1, &pipeInfo, nullptr, &g_ycbcrPipeline.pipeline) != VK_SUCCESS)
        {
            LOGE("EnsureYcbcrPipeline: vkCreateGraphicsPipelines failed.");
            DestroyYcbcrPipeline();
            return false;
        }

        LOGI("EnsureYcbcrPipeline: created (format=%d)", static_cast<int>(outputFormat));
        return true;
    }

    bool EnsureYcbcrSampler(const VkAndroidHardwareBufferFormatPropertiesANDROID& formatProps)
    {
        if (!g_vulkanReady ||
            !g_vkCreateSampler ||
            !g_vkDestroySampler ||
            !g_vkCreateSamplerYcbcrConversion ||
            !g_vkDestroySamplerYcbcrConversion)
        {
            LOGE("EnsureYcbcrSampler: missing Vulkan functions.");
            return false;
        }

        const uint64_t externalFormat = static_cast<uint64_t>(formatProps.externalFormat);
        const uint32_t overrideGeneration = g_ycbcrOverride.generation.load(std::memory_order_relaxed);
        if (g_ycbcrSampler.sampler != VK_NULL_HANDLE &&
            g_ycbcrSampler.externalFormat == externalFormat &&
            g_ycbcrSampler.overrideGeneration == overrideGeneration)
        {
            return true;
        }

        DestroyYcbcrSampler();
        g_ycbcrSampler.externalFormat = externalFormat;
        g_ycbcrSampler.overrideGeneration = overrideGeneration;

        VkSamplerYcbcrModelConversion model = formatProps.suggestedYcbcrModel;
        VkSamplerYcbcrRange range = formatProps.suggestedYcbcrRange;
        VkComponentMapping components = formatProps.samplerYcbcrConversionComponents;
        VkChromaLocation xChromaOffset = formatProps.suggestedXChromaOffset;
        VkChromaLocation yChromaOffset = formatProps.suggestedYChromaOffset;

        const int userModel = g_ycbcrOverride.modelOverride.load(std::memory_order_relaxed);
        const int userRange = g_ycbcrOverride.rangeOverride.load(std::memory_order_relaxed);
        const int swizzleMode = g_ycbcrOverride.swizzleMode.load(std::memory_order_relaxed);
        const int userXChroma = g_ycbcrOverride.xChromaOffsetOverride.load(std::memory_order_relaxed);
        const int userYChroma = g_ycbcrOverride.yChromaOffsetOverride.load(std::memory_order_relaxed);
        const bool manualYuv = g_ycbcrOverride.manualYuv.load(std::memory_order_relaxed) != 0;

        // Heuristic: some drivers report RGB_IDENTITY / YCBCR_IDENTITY for external-format-only buffers,
        // which would pass through YUV components and produce obvious tinting (e.g. magenta/pink).
        if (userModel < 0)
        {
            if (model == VK_SAMPLER_YCBCR_MODEL_CONVERSION_RGB_IDENTITY ||
                model == VK_SAMPLER_YCBCR_MODEL_CONVERSION_YCBCR_IDENTITY)
            {
                model = VK_SAMPLER_YCBCR_MODEL_CONVERSION_YCBCR_709;
            }
        }
        else
        {
            model = static_cast<VkSamplerYcbcrModelConversion>(userModel);
        }

        if (userRange >= 0)
        {
            range = static_cast<VkSamplerYcbcrRange>(userRange);
        }

        if (userXChroma >= 0)
        {
            xChromaOffset = static_cast<VkChromaLocation>(userXChroma);
        }
        if (userYChroma >= 0)
        {
            yChromaOffset = static_cast<VkChromaLocation>(userYChroma);
        }
        if (manualYuv)
        {
            // Force identity so the shader can perform its own YUV->RGB conversion (allows UV swap, etc).
            model = VK_SAMPLER_YCBCR_MODEL_CONVERSION_YCBCR_IDENTITY;

            // IMPORTANT: inputMode controls whether the shader expects already-range-expanded normalized YUV (0)
            // or raw byte-like values (1/2). Only force FULL range when the shader performs byte-style range math.
            const int inputMode = g_ycbcrOverride.inputMode.load(std::memory_order_relaxed);
            if (inputMode != 0)
            {
                // Avoid double range expansion; manual shader handles narrow-range offsets/scaling.
                range = VK_SAMPLER_YCBCR_RANGE_ITU_FULL;
            }
        }
        if (swizzleMode != 0)
        {
            // If the driver reports IDENTITY swizzles (common), swapping IDENTITY<->IDENTITY is a no-op.
            // Expand IDENTITY to explicit R/G/B/A so we can actually swap the chroma channels.
            auto canonical = [](VkComponentSwizzle value, VkComponentSwizzle identityReplacement) {
                return value == VK_COMPONENT_SWIZZLE_IDENTITY ? identityReplacement : value;
            };

            VkComponentSwizzle r = canonical(components.r, VK_COMPONENT_SWIZZLE_R);
            VkComponentSwizzle g = canonical(components.g, VK_COMPONENT_SWIZZLE_G);
            VkComponentSwizzle b = canonical(components.b, VK_COMPONENT_SWIZZLE_B);
            VkComponentSwizzle a = canonical(components.a, VK_COMPONENT_SWIZZLE_A);

            if (swizzleMode == 2)
            {
                auto tmp = r;
                r = b;
                b = tmp;
            }
            else
            {
                // Legacy mode: swap G/B.
                auto tmp = g;
                g = b;
                b = tmp;
            }

            components.r = r;
            components.g = g;
            components.b = b;
            components.a = a;

        }

        // Auto-detect sampled channel order for manual YUV conversion.
        // Some drivers expose external-format images with component mappings that reorder the identity Y/U/V triplet.
        //
        // IMPORTANT: do not override an explicit user/channelOrder choice.
        // Unity's auto-calibration probes multiple channel orders; forcing the order back to an "auto" result
        // makes the search ineffective and can lock in a wrong interpretation (often seen as a pink/blue tint).
        if (manualYuv && g_ycbcrOverride.useDecoderColorInfo.load(std::memory_order_relaxed) != 0)
        {
            const int currentOrder = g_ycbcrOverride.channelOrder.load(std::memory_order_relaxed);
            if (currentOrder == 0)
            {
                const int autoOrder = ComputeChannelOrderFromComponents(components);
                if (autoOrder >= 0 && autoOrder != currentOrder)
                {
                    g_ycbcrOverride.channelOrder.store(autoOrder, std::memory_order_relaxed);
                    LOGI("Auto channelOrder set from sampler components: order=%d (prev=%d, comps r=%d g=%d b=%d)",
                         autoOrder,
                         currentOrder,
                         static_cast<int>(components.r),
                         static_cast<int>(components.g),
                         static_cast<int>(components.b));
                }
            }
        }

        g_ycbcrSampler.model = model;
        g_ycbcrSampler.range = range;
        g_ycbcrSampler.components = components;
        g_ycbcrSampler.xChromaOffset = xChromaOffset;
        g_ycbcrSampler.yChromaOffset = yChromaOffset;

        VkExternalFormatANDROID externalFormatInfo{VK_STRUCTURE_TYPE_EXTERNAL_FORMAT_ANDROID};
        externalFormatInfo.externalFormat = formatProps.externalFormat;

        VkSamplerYcbcrConversionCreateInfo convInfo{VK_STRUCTURE_TYPE_SAMPLER_YCBCR_CONVERSION_CREATE_INFO};
        convInfo.pNext = &externalFormatInfo;
        convInfo.format = VK_FORMAT_UNDEFINED;
        convInfo.ycbcrModel = model;
        convInfo.ycbcrRange = range;
        convInfo.components = components;
        convInfo.xChromaOffset = xChromaOffset;
        convInfo.yChromaOffset = yChromaOffset;
        convInfo.chromaFilter = VK_FILTER_LINEAR;
        convInfo.forceExplicitReconstruction = VK_FALSE;

        if (g_vkCreateSamplerYcbcrConversion(g_vulkanInstance.device, &convInfo, nullptr, &g_ycbcrSampler.conversion) != VK_SUCCESS)
        {
            LOGE("EnsureYcbcrSampler: vkCreateSamplerYcbcrConversion failed.");
            DestroyYcbcrSampler();
            return false;
        }

        VkSamplerYcbcrConversionInfo convInfo2{VK_STRUCTURE_TYPE_SAMPLER_YCBCR_CONVERSION_INFO};
        convInfo2.conversion = g_ycbcrSampler.conversion;

        VkSamplerCreateInfo samplerInfo{VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO};
        samplerInfo.pNext = &convInfo2;
        samplerInfo.magFilter = VK_FILTER_LINEAR;
        samplerInfo.minFilter = VK_FILTER_LINEAR;
        samplerInfo.mipmapMode = VK_SAMPLER_MIPMAP_MODE_NEAREST;
        samplerInfo.addressModeU = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
        samplerInfo.addressModeV = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
        samplerInfo.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
        samplerInfo.minLod = 0.0f;
        samplerInfo.maxLod = 0.0f;
        samplerInfo.maxAnisotropy = 1.0f;

        if (g_vkCreateSampler(g_vulkanInstance.device, &samplerInfo, nullptr, &g_ycbcrSampler.sampler) != VK_SUCCESS)
        {
            LOGE("EnsureYcbcrSampler: vkCreateSampler failed.");
            DestroyYcbcrSampler();
            return false;
        }

        LOGI("EnsureYcbcrSampler: created externalFormat=0x%llx model=%d range=%d comps=%d,%d,%d,%d xOff=%d yOff=%d swizzleMode=%d manualYuv=%d (userModel=%d userRange=%d userX=%d userY=%d)",
             static_cast<unsigned long long>(externalFormat),
             static_cast<int>(model),
             static_cast<int>(range),
             static_cast<int>(components.r),
             static_cast<int>(components.g),
             static_cast<int>(components.b),
             static_cast<int>(components.a),
             static_cast<int>(xChromaOffset),
             static_cast<int>(yChromaOffset),
             swizzleMode,
             manualYuv ? 1 : 0,
             userModel,
             userRange,
             userXChroma,
             userYChroma);
        return true;
    }

    bool EnsurePerTextureConvertResources(ExternalTexture& texture)
    {
        if (!g_vulkanReady ||
            !g_vkCreateFramebuffer ||
            !g_vkDestroyFramebuffer ||
            !g_vkCreateDescriptorPool ||
            !g_vkDestroyDescriptorPool ||
            !g_vkAllocateDescriptorSets)
        {
            LOGE("EnsurePerTextureConvertResources: missing Vulkan functions.");
            return false;
        }

        if (texture.descriptorSet == VK_NULL_HANDLE)
        {
            VkDescriptorPoolSize poolSize{};
            poolSize.type = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
            poolSize.descriptorCount = 1;

            VkDescriptorPoolCreateInfo poolInfo{VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO};
            poolInfo.maxSets = 1;
            poolInfo.poolSizeCount = 1;
            poolInfo.pPoolSizes = &poolSize;

            if (g_vkCreateDescriptorPool(g_vulkanInstance.device, &poolInfo, nullptr, &texture.descriptorPool) != VK_SUCCESS)
            {
                LOGE("EnsurePerTextureConvertResources: vkCreateDescriptorPool failed.");
                return false;
            }

            VkDescriptorSetAllocateInfo allocInfo{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO};
            allocInfo.descriptorPool = texture.descriptorPool;
            allocInfo.descriptorSetCount = 1;
            allocInfo.pSetLayouts = &g_ycbcrPipeline.descriptorSetLayout;

            if (g_vkAllocateDescriptorSets(g_vulkanInstance.device, &allocInfo, &texture.descriptorSet) != VK_SUCCESS)
            {
                LOGE("EnsurePerTextureConvertResources: vkAllocateDescriptorSets failed.");
                return false;
            }
        }

        if (texture.framebuffer == VK_NULL_HANDLE)
        {
            VkImageView attachment = texture.imageView;
            VkFramebufferCreateInfo fbInfo{VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO};
            fbInfo.renderPass = g_ycbcrPipeline.renderPass;
            fbInfo.attachmentCount = 1;
            fbInfo.pAttachments = &attachment;
            fbInfo.width = static_cast<uint32_t>(texture.width);
            fbInfo.height = static_cast<uint32_t>(texture.height);
            fbInfo.layers = 1;
            if (g_vkCreateFramebuffer(g_vulkanInstance.device, &fbInfo, nullptr, &texture.framebuffer) != VK_SUCCESS)
            {
                LOGE("EnsurePerTextureConvertResources: vkCreateFramebuffer failed.");
                return false;
            }
        }

        return true;
    }
    void CompleteGpuOperation(ExternalTexture& texture, bool ready, bool alive, bool result)
    {
        {
            std::lock_guard<std::mutex> lock(texture.stateMutex);
            texture.gpuReady = ready;
            texture.alive = alive;
            texture.lastResult = result;
            texture.pendingOp = TexturePendingOp::None;
        }
        texture.stateCondition.notify_all();
    }

    enum class RenderEventId : int
    {
        CreateTexture = 0x5151,
        BindTexture = 0x5152,
        ImportHardwareBuffer = 0x5154,
        DestroyTexture = 0x5153,
    };

    void UNITY_INTERFACE_API OnRenderEvent(int eventId, void* data);

    bool IsVulkanRenderer()
    {
        return g_graphics && g_graphics->GetRenderer() == kUnityGfxRendererVulkan;
    }

    void LoadVulkanFunctions()
    {
        if (!g_graphicsVulkan || !g_vkGetDeviceProcAddr)
        {
            return;
        }

        auto loadDevice = [&](const char* name) -> PFN_vkVoidFunction {
            return g_vkGetDeviceProcAddr(g_vulkanInstance.device, name);
        };

        g_vkCreateImage = reinterpret_cast<PFN_vkCreateImage>(loadDevice("vkCreateImage"));
        g_vkDestroyImage = reinterpret_cast<PFN_vkDestroyImage>(loadDevice("vkDestroyImage"));
        g_vkGetImageMemoryRequirements =
            reinterpret_cast<PFN_vkGetImageMemoryRequirements>(loadDevice("vkGetImageMemoryRequirements"));
        g_vkAllocateMemory = reinterpret_cast<PFN_vkAllocateMemory>(loadDevice("vkAllocateMemory"));
        g_vkFreeMemory = reinterpret_cast<PFN_vkFreeMemory>(loadDevice("vkFreeMemory"));
        g_vkBindImageMemory = reinterpret_cast<PFN_vkBindImageMemory>(loadDevice("vkBindImageMemory"));
        g_vkCreateImageView = reinterpret_cast<PFN_vkCreateImageView>(loadDevice("vkCreateImageView"));
        g_vkDestroyImageView = reinterpret_cast<PFN_vkDestroyImageView>(loadDevice("vkDestroyImageView"));
        g_vkCmdPipelineBarrier = reinterpret_cast<PFN_vkCmdPipelineBarrier>(loadDevice("vkCmdPipelineBarrier"));
        g_vkGetAndroidHardwareBufferPropertiesANDROID =
            reinterpret_cast<PFN_vkGetAndroidHardwareBufferPropertiesANDROID>(
                loadDevice("vkGetAndroidHardwareBufferPropertiesANDROID"));

        g_vkCreateSampler = reinterpret_cast<PFN_vkCreateSampler>(loadDevice("vkCreateSampler"));
        g_vkDestroySampler = reinterpret_cast<PFN_vkDestroySampler>(loadDevice("vkDestroySampler"));

        g_vkCreateSamplerYcbcrConversion = reinterpret_cast<PFN_vkCreateSamplerYcbcrConversion>(
            loadDevice("vkCreateSamplerYcbcrConversion"));
        if (!g_vkCreateSamplerYcbcrConversion)
        {
            g_vkCreateSamplerYcbcrConversion = reinterpret_cast<PFN_vkCreateSamplerYcbcrConversion>(
                loadDevice("vkCreateSamplerYcbcrConversionKHR"));
        }
        g_vkDestroySamplerYcbcrConversion = reinterpret_cast<PFN_vkDestroySamplerYcbcrConversion>(
            loadDevice("vkDestroySamplerYcbcrConversion"));
        if (!g_vkDestroySamplerYcbcrConversion)
        {
            g_vkDestroySamplerYcbcrConversion = reinterpret_cast<PFN_vkDestroySamplerYcbcrConversion>(
                loadDevice("vkDestroySamplerYcbcrConversionKHR"));
        }

        g_vkCreateShaderModule = reinterpret_cast<PFN_vkCreateShaderModule>(loadDevice("vkCreateShaderModule"));
        g_vkDestroyShaderModule = reinterpret_cast<PFN_vkDestroyShaderModule>(loadDevice("vkDestroyShaderModule"));

        g_vkCreateDescriptorSetLayout =
            reinterpret_cast<PFN_vkCreateDescriptorSetLayout>(loadDevice("vkCreateDescriptorSetLayout"));
        g_vkDestroyDescriptorSetLayout =
            reinterpret_cast<PFN_vkDestroyDescriptorSetLayout>(loadDevice("vkDestroyDescriptorSetLayout"));
        g_vkCreateDescriptorPool =
            reinterpret_cast<PFN_vkCreateDescriptorPool>(loadDevice("vkCreateDescriptorPool"));
        g_vkDestroyDescriptorPool =
            reinterpret_cast<PFN_vkDestroyDescriptorPool>(loadDevice("vkDestroyDescriptorPool"));
        g_vkAllocateDescriptorSets =
            reinterpret_cast<PFN_vkAllocateDescriptorSets>(loadDevice("vkAllocateDescriptorSets"));
        g_vkUpdateDescriptorSets =
            reinterpret_cast<PFN_vkUpdateDescriptorSets>(loadDevice("vkUpdateDescriptorSets"));

        g_vkCreatePipelineLayout =
            reinterpret_cast<PFN_vkCreatePipelineLayout>(loadDevice("vkCreatePipelineLayout"));
        g_vkDestroyPipelineLayout =
            reinterpret_cast<PFN_vkDestroyPipelineLayout>(loadDevice("vkDestroyPipelineLayout"));
        g_vkCreateRenderPass =
            reinterpret_cast<PFN_vkCreateRenderPass>(loadDevice("vkCreateRenderPass"));
        g_vkDestroyRenderPass =
            reinterpret_cast<PFN_vkDestroyRenderPass>(loadDevice("vkDestroyRenderPass"));
        g_vkCreateFramebuffer =
            reinterpret_cast<PFN_vkCreateFramebuffer>(loadDevice("vkCreateFramebuffer"));
        g_vkDestroyFramebuffer =
            reinterpret_cast<PFN_vkDestroyFramebuffer>(loadDevice("vkDestroyFramebuffer"));

        g_vkCreateGraphicsPipelines =
            reinterpret_cast<PFN_vkCreateGraphicsPipelines>(loadDevice("vkCreateGraphicsPipelines"));
        g_vkDestroyPipeline =
            reinterpret_cast<PFN_vkDestroyPipeline>(loadDevice("vkDestroyPipeline"));

        g_vkCmdBeginRenderPass =
            reinterpret_cast<PFN_vkCmdBeginRenderPass>(loadDevice("vkCmdBeginRenderPass"));
        g_vkCmdEndRenderPass =
            reinterpret_cast<PFN_vkCmdEndRenderPass>(loadDevice("vkCmdEndRenderPass"));
        g_vkCmdBindPipeline =
            reinterpret_cast<PFN_vkCmdBindPipeline>(loadDevice("vkCmdBindPipeline"));
        g_vkCmdBindDescriptorSets =
            reinterpret_cast<PFN_vkCmdBindDescriptorSets>(loadDevice("vkCmdBindDescriptorSets"));
        g_vkCmdSetViewport =
            reinterpret_cast<PFN_vkCmdSetViewport>(loadDevice("vkCmdSetViewport"));
        g_vkCmdSetScissor =
            reinterpret_cast<PFN_vkCmdSetScissor>(loadDevice("vkCmdSetScissor"));
        g_vkCmdDraw =
            reinterpret_cast<PFN_vkCmdDraw>(loadDevice("vkCmdDraw"));
        g_vkCmdCopyImage =
            reinterpret_cast<PFN_vkCmdCopyImage>(loadDevice("vkCmdCopyImage"));
        g_vkCmdPushConstants =
            reinterpret_cast<PFN_vkCmdPushConstants>(loadDevice("vkCmdPushConstants"));

        g_vkCreateSemaphore =
            reinterpret_cast<PFN_vkCreateSemaphore>(loadDevice("vkCreateSemaphore"));
        g_vkDestroySemaphore =
            reinterpret_cast<PFN_vkDestroySemaphore>(loadDevice("vkDestroySemaphore"));
        g_vkImportSemaphoreFdKHR =
            reinterpret_cast<PFN_vkImportSemaphoreFdKHR>(loadDevice("vkImportSemaphoreFdKHR"));
        g_vkWaitSemaphores =
            reinterpret_cast<PFN_vkWaitSemaphores>(loadDevice("vkWaitSemaphores"));
        if (!g_vkWaitSemaphores)
        {
            g_vkWaitSemaphores =
                reinterpret_cast<PFN_vkWaitSemaphores>(loadDevice("vkWaitSemaphoresKHR"));
        }
    }

    void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType)
    {
        if (eventType == kUnityGfxDeviceEventInitialize)
        {
            if (!IsVulkanRenderer())
            {
                LOGE("QuestVulkanExt requires Vulkan renderer.");
                return;
            }

            g_graphicsVulkan = g_unityInterfaces->Get<IUnityGraphicsVulkan>();
            g_vulkanInstance = g_graphicsVulkan->Instance();

            auto getInstanceProcAddr = g_vulkanInstance.getInstanceProcAddr;
            g_vkGetDeviceProcAddr = reinterpret_cast<PFN_vkGetDeviceProcAddr>(
                getInstanceProcAddr(g_vulkanInstance.instance, "vkGetDeviceProcAddr"));
            g_vkGetPhysicalDeviceMemoryProperties =
                reinterpret_cast<PFN_vkGetPhysicalDeviceMemoryProperties>(
                    getInstanceProcAddr(g_vulkanInstance.instance, "vkGetPhysicalDeviceMemoryProperties"));

            LoadVulkanFunctions();
            if (g_graphicsVulkan)
            {
                UnityVulkanPluginEventConfig eventConfig{};
                eventConfig.renderPassPrecondition = kUnityVulkanRenderPass_EnsureOutside;
                eventConfig.graphicsQueueAccess = kUnityVulkanGraphicsQueueAccess_DontCare;
                eventConfig.flags = kUnityVulkanEventConfigFlag_EnsurePreviousFrameSubmission |
                                    kUnityVulkanEventConfigFlag_ModifiesCommandBuffersState;
                g_graphicsVulkan->ConfigureEvent(static_cast<int>(RenderEventId::CreateTexture), &eventConfig);
                g_graphicsVulkan->ConfigureEvent(static_cast<int>(RenderEventId::BindTexture), &eventConfig);
                g_graphicsVulkan->ConfigureEvent(static_cast<int>(RenderEventId::ImportHardwareBuffer), &eventConfig);
                g_graphicsVulkan->ConfigureEvent(static_cast<int>(RenderEventId::DestroyTexture), &eventConfig);
            }
            g_vulkanReady = true;
            LOGI("QuestVulkanExt initialized (device=%p, build=%s)", g_vulkanInstance.device, QUEST_VULKANEXT_BUILD_TAG);
        }
        else if (eventType == kUnityGfxDeviceEventShutdown)
        {
            ClearDeferredYcbcrReleases();
            DestroyYcbcrPipeline();
            DestroyYcbcrSampler();

            g_vulkanReady = false;
            g_graphicsVulkan = nullptr;
            g_vkGetDeviceProcAddr = nullptr;
            g_vkGetPhysicalDeviceMemoryProperties = nullptr;
            g_vkCreateImage = nullptr;
            g_vkDestroyImage = nullptr;
            g_vkGetImageMemoryRequirements = nullptr;
            g_vkAllocateMemory = nullptr;
            g_vkFreeMemory = nullptr;
            g_vkBindImageMemory = nullptr;
            g_vkCreateImageView = nullptr;
            g_vkDestroyImageView = nullptr;
            g_vkCmdPipelineBarrier = nullptr;
            g_vkGetAndroidHardwareBufferPropertiesANDROID = nullptr;
            g_vkCreateSampler = nullptr;
            g_vkDestroySampler = nullptr;
            g_vkCreateSamplerYcbcrConversion = nullptr;
            g_vkDestroySamplerYcbcrConversion = nullptr;
            g_vkCreateShaderModule = nullptr;
            g_vkDestroyShaderModule = nullptr;
            g_vkCreateDescriptorSetLayout = nullptr;
            g_vkDestroyDescriptorSetLayout = nullptr;
            g_vkCreateDescriptorPool = nullptr;
            g_vkDestroyDescriptorPool = nullptr;
            g_vkAllocateDescriptorSets = nullptr;
            g_vkUpdateDescriptorSets = nullptr;
            g_vkCreatePipelineLayout = nullptr;
            g_vkDestroyPipelineLayout = nullptr;
            g_vkCreateRenderPass = nullptr;
            g_vkDestroyRenderPass = nullptr;
            g_vkCreateFramebuffer = nullptr;
            g_vkDestroyFramebuffer = nullptr;
            g_vkCreateGraphicsPipelines = nullptr;
            g_vkDestroyPipeline = nullptr;
            g_vkCmdBeginRenderPass = nullptr;
            g_vkCmdEndRenderPass = nullptr;
            g_vkCmdBindPipeline = nullptr;
            g_vkCmdBindDescriptorSets = nullptr;
            g_vkCmdSetViewport = nullptr;
            g_vkCmdSetScissor = nullptr;
            g_vkCmdDraw = nullptr;
            g_vkCmdCopyImage = nullptr;
            g_vkCmdPushConstants = nullptr;
            g_vkCreateSemaphore = nullptr;
            g_vkDestroySemaphore = nullptr;
            g_vkImportSemaphoreFdKHR = nullptr;
            g_vkWaitSemaphores = nullptr;
            g_vulkanInstance = {};
        }
    }

    uint32_t FindMemoryType(uint32_t memoryTypeBits, VkMemoryPropertyFlags properties)
    {
        VkPhysicalDeviceMemoryProperties memProperties{};
        if (g_vkGetPhysicalDeviceMemoryProperties)
        {
            g_vkGetPhysicalDeviceMemoryProperties(g_vulkanInstance.physicalDevice, &memProperties);
        }

        for (uint32_t i = 0; i < memProperties.memoryTypeCount; ++i)
        {
            if ((memoryTypeBits & (1u << i)) &&
                (memProperties.memoryTypes[i].propertyFlags & properties) == properties)
            {
                return i;
            }
        }

        return 0;
    }

    bool CreateNativeImage(ExternalTexture& texture, VkCommandBuffer commandBuffer, VkFormat desiredFormat)
    {
        if (commandBuffer == VK_NULL_HANDLE || !g_vkCmdPipelineBarrier)
        {
            LOGE("CreateNativeImage requires a valid Vulkan command buffer.");
            return false;
        }

        if (!g_graphicsVulkan ||
            !g_vkCreateImage ||
            !g_vkGetImageMemoryRequirements ||
            !g_vkAllocateMemory ||
            !g_vkBindImageMemory ||
            !g_vkCreateImageView)
        {
            LOGE("Vulkan function table incomplete - did Unity initialize Vulkan?");
            return false;
        }

        LOGI("CreateNativeImage(%p) start, size=%dx%d, cmdBuffer=%p",
             &texture,
             texture.width,
             texture.height,
             commandBuffer);

        VkFormat outputFormat = desiredFormat != VK_FORMAT_UNDEFINED ? desiredFormat : VK_FORMAT_R8G8B8A8_UNORM;

        VkImageCreateInfo imageInfo{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
        imageInfo.flags = VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT;
        imageInfo.imageType = VK_IMAGE_TYPE_2D;
        imageInfo.extent = {static_cast<uint32_t>(texture.width),
                            static_cast<uint32_t>(texture.height), 1};
        imageInfo.mipLevels = 1;
        imageInfo.arrayLayers = 1;
        imageInfo.format = outputFormat;
        imageInfo.tiling = VK_IMAGE_TILING_OPTIMAL;
        imageInfo.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
        imageInfo.usage = VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
        imageInfo.samples = VK_SAMPLE_COUNT_1_BIT;
        imageInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE;

        VkImage image = VK_NULL_HANDLE;
        if (g_vkCreateImage(g_vulkanInstance.device, &imageInfo, nullptr, &image) != VK_SUCCESS)
        {
            LOGE("vkCreateImage failed");
            DestroyNativeImage(texture);
            return false;
        }

        VkMemoryDedicatedAllocateInfo memoryDedicatedInfo{
            VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO};
        memoryDedicatedInfo.image = image;

        VkMemoryRequirements memReq{};
        g_vkGetImageMemoryRequirements(g_vulkanInstance.device, image, &memReq);

        VkMemoryAllocateInfo allocInfo{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
        allocInfo.pNext = &memoryDedicatedInfo;
        allocInfo.memoryTypeIndex = FindMemoryType(memReq.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
        allocInfo.allocationSize = memReq.size;

        VkDeviceMemory memory = VK_NULL_HANDLE;
        if (g_vkAllocateMemory(g_vulkanInstance.device, &allocInfo, nullptr, &memory) != VK_SUCCESS)
        {
            LOGE("vkAllocateMemory failed");
            g_vkDestroyImage(g_vulkanInstance.device, image, nullptr);
            DestroyNativeImage(texture);
            return false;
        }

        if (g_vkBindImageMemory(g_vulkanInstance.device, image, memory, 0) != VK_SUCCESS)
        {
            LOGE("vkBindImageMemory failed");
            g_vkDestroyImage(g_vulkanInstance.device, image, nullptr);
            g_vkFreeMemory(g_vulkanInstance.device, memory, nullptr);
            DestroyNativeImage(texture);
            return false;
        }

        VkImageViewCreateInfo viewInfo{VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO};
        viewInfo.image = image;
        viewInfo.viewType = VK_IMAGE_VIEW_TYPE_2D;
        viewInfo.format = imageInfo.format;
        viewInfo.components.r = VK_COMPONENT_SWIZZLE_R;
        viewInfo.components.g = VK_COMPONENT_SWIZZLE_G;
        viewInfo.components.b = VK_COMPONENT_SWIZZLE_B;
        viewInfo.components.a = VK_COMPONENT_SWIZZLE_A;
        viewInfo.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        viewInfo.subresourceRange.baseMipLevel = 0;
        viewInfo.subresourceRange.levelCount = 1;
        viewInfo.subresourceRange.baseArrayLayer = 0;
        viewInfo.subresourceRange.layerCount = 1;

        VkImageView imageView = VK_NULL_HANDLE;
        LOGI("vkCreateImageView (image=%p layout=%d usage=0x%x)", image, imageInfo.initialLayout, imageInfo.usage);
        if (g_vkCreateImageView(g_vulkanInstance.device, &viewInfo, nullptr, &imageView) != VK_SUCCESS)
        {
            LOGE("vkCreateImageView failed");
            LOGE("ViewInfo: aspect=0x%x format=%d mip=%d layers=%d",
                 viewInfo.subresourceRange.aspectMask,
                 viewInfo.format,
                 viewInfo.subresourceRange.levelCount,
                 viewInfo.subresourceRange.layerCount);
            g_vkDestroyImage(g_vulkanInstance.device, image, nullptr);
            g_vkFreeMemory(g_vulkanInstance.device, memory, nullptr);
            DestroyNativeImage(texture);
            return false;
        }

        texture.memory = memory;
        texture.imageView = imageView;
        texture.buffer = nullptr;
        texture.unityImage.memory.memory = memory;
        texture.unityImage.memory.offset = 0;
        texture.unityImage.memory.size = memReq.size;
        texture.unityImage.memory.mapped = nullptr;
        texture.unityImage.memory.flags = VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT;
        texture.unityImage.memory.memoryTypeIndex = allocInfo.memoryTypeIndex;
        texture.unityImage.image = image;
        texture.unityImage.layout = VK_IMAGE_LAYOUT_UNDEFINED;
        texture.unityImage.aspect = VK_IMAGE_ASPECT_COLOR_BIT;
        texture.unityImage.usage = imageInfo.usage;
        texture.unityImage.format = imageInfo.format;
        texture.unityImage.extent = imageInfo.extent;
        texture.unityImage.tiling = imageInfo.tiling;
        texture.unityImage.type = imageInfo.imageType;
        texture.unityImage.samples = imageInfo.samples;
        texture.unityImage.layers = 1;
        texture.unityImage.mipCount = 1;
        texture.unityImage.reserved[0] = imageView;

        if (!TransitionImageToShaderRead(commandBuffer, texture))
        {
            LOGE("Failed to transition imported image to shader-readable layout.");
            DestroyNativeImage(texture);
            return false;
        }

        LOGI("CreateNativeImage(%p) success (image=%p view=%p buffer=%p)",
             &texture,
             texture.unityImage.image,
             texture.imageView,
             texture.buffer);
        return true;
    }

    void CloseFenceFd(int fenceFd)
    {
        if (fenceFd >= 0)
        {
            close(fenceFd);
        }
    }

    bool WaitForFenceFd(int fenceFd)
    {
        if (fenceFd < 0)
        {
            return true;
        }

        const uint64_t timeoutNs = 5ull * 1000ull * 1000ull;

        if (g_vkCreateSemaphore && g_vkDestroySemaphore && g_vkImportSemaphoreFdKHR && g_vkWaitSemaphores)
        {
            VkSemaphore semaphore = VK_NULL_HANDLE;
            VkSemaphoreCreateInfo semInfo{VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO};
            if (g_vkCreateSemaphore(g_vulkanInstance.device, &semInfo, nullptr, &semaphore) != VK_SUCCESS)
            {
                LOGE("Fence wait: vkCreateSemaphore failed.");
                CloseFenceFd(fenceFd);
                return false;
            }

            VkImportSemaphoreFdInfoKHR importInfo{VK_STRUCTURE_TYPE_IMPORT_SEMAPHORE_FD_INFO_KHR};
            importInfo.semaphore = semaphore;
            importInfo.flags = VK_SEMAPHORE_IMPORT_TEMPORARY_BIT;
            importInfo.handleType = VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_SYNC_FD_BIT;
            importInfo.fd = fenceFd;

            if (g_vkImportSemaphoreFdKHR(g_vulkanInstance.device, &importInfo) != VK_SUCCESS)
            {
                LOGE("Fence wait: vkImportSemaphoreFdKHR failed.");
                g_vkDestroySemaphore(g_vulkanInstance.device, semaphore, nullptr);
                CloseFenceFd(fenceFd);
                return false;
            }

            VkSemaphoreWaitInfo waitInfo{VK_STRUCTURE_TYPE_SEMAPHORE_WAIT_INFO};
            waitInfo.flags = 0;
            waitInfo.semaphoreCount = 1;
            waitInfo.pSemaphores = &semaphore;
            waitInfo.pValues = nullptr;

            VkResult waitRes = g_vkWaitSemaphores(g_vulkanInstance.device, &waitInfo, timeoutNs);
            g_vkDestroySemaphore(g_vulkanInstance.device, semaphore, nullptr);
            if (waitRes == VK_SUCCESS)
            {
                return true;
            }
            if (waitRes == VK_TIMEOUT)
            {
                LOGW("Fence wait: timeout after %llu ns.", static_cast<unsigned long long>(timeoutNs));
            }
            else
            {
                LOGE("Fence wait: vkWaitSemaphores failed (%d).", static_cast<int>(waitRes));
            }
            return false;
        }

        struct pollfd pfd{};
        pfd.fd = fenceFd;
        pfd.events = POLLIN;
        int res = poll(&pfd, 1, 5);
        CloseFenceFd(fenceFd);
        if (res > 0)
        {
            return true;
        }
        if (res == 0)
        {
            LOGW("Fence wait: poll timeout.");
        }
        else
        {
            LOGE("Fence wait: poll error.");
        }
        return false;
    }

    bool ImportHardwareBufferImage(
        ExternalTexture& texture,
        VkCommandBuffer commandBuffer,
        AHardwareBuffer* hardwareBuffer,
        int fenceFd,
        uint64_t currentFrameNumber)
    {
        if (hardwareBuffer == nullptr)
        {
            LOGE("ImportHardwareBufferImage called with null buffer");
            CloseFenceFd(fenceFd);
            return false;
        }

        if (commandBuffer == VK_NULL_HANDLE || !g_vkCmdPipelineBarrier)
        {
            LOGE("ImportHardwareBufferImage requires a valid Vulkan command buffer.");
            CloseFenceFd(fenceFd);
            return false;
        }

        if (!g_graphicsVulkan ||
            !g_vkCreateImage ||
            !g_vkAllocateMemory ||
            !g_vkBindImageMemory ||
            !g_vkCreateImageView ||
            !g_vkGetAndroidHardwareBufferPropertiesANDROID)
        {
            LOGE("Vulkan function table incomplete - did Unity initialize Vulkan?");
            CloseFenceFd(fenceFd);
            return false;
        }

        if (!WaitForFenceFd(fenceFd))
        {
            AHardwareBuffer_release(hardwareBuffer);
            return false;
        }

        AHardwareBuffer_Desc desc{};
        AHardwareBuffer_describe(hardwareBuffer, &desc);
        if (desc.width == 0 || desc.height == 0)
        {
            LOGE("AHardwareBuffer_describe returned invalid size.");
            return false;
        }

        if (ShouldLogThrottled(g_lastImportDescLogMs, 5000))
        {
            LOGI("ImportHardwareBufferImage: AHB desc format=0x%x usage=0x%llx size=%ux%u stride=%u",
                 desc.format,
                 static_cast<unsigned long long>(desc.usage),
                 desc.width,
                 desc.height,
                 desc.stride);
        }

        VkAndroidHardwareBufferPropertiesANDROID bufferProps{
            VK_STRUCTURE_TYPE_ANDROID_HARDWARE_BUFFER_PROPERTIES_ANDROID};
        VkAndroidHardwareBufferFormatPropertiesANDROID formatProps{
            VK_STRUCTURE_TYPE_ANDROID_HARDWARE_BUFFER_FORMAT_PROPERTIES_ANDROID};
        bufferProps.pNext = &formatProps;

        if (g_vkGetAndroidHardwareBufferPropertiesANDROID(
                g_vulkanInstance.device, hardwareBuffer, &bufferProps) != VK_SUCCESS)
        {
            LOGE("vkGetAndroidHardwareBufferPropertiesANDROID failed (import path)");
            AHardwareBuffer_release(hardwareBuffer);
            return false;
        }

        if (ShouldLogThrottled(g_lastImportFormatLogMs, 5000))
        {
            LOGI("ImportHardwareBufferImage: vk format=%d externalFormat=0x%llx features=0x%x",
                 static_cast<int>(formatProps.format),
                 static_cast<unsigned long long>(formatProps.externalFormat),
                 static_cast<unsigned int>(formatProps.formatFeatures));
        }

        const uint64_t extFmt = static_cast<uint64_t>(formatProps.externalFormat);
        if (extFmt != 0 && extFmt != g_lastLoggedExternalFormat)
        {
            g_lastLoggedExternalFormat = extFmt;
            LOGI("ImportHardwareBufferImage: suggested ycbcrModel=%d range=%d comps=%d,%d,%d,%d xOff=%d yOff=%d",
                 static_cast<int>(formatProps.suggestedYcbcrModel),
                 static_cast<int>(formatProps.suggestedYcbcrRange),
                 static_cast<int>(formatProps.samplerYcbcrConversionComponents.r),
                 static_cast<int>(formatProps.samplerYcbcrConversionComponents.g),
                 static_cast<int>(formatProps.samplerYcbcrConversionComponents.b),
                 static_cast<int>(formatProps.samplerYcbcrConversionComponents.a),
                 static_cast<int>(formatProps.suggestedXChromaOffset),
                 static_cast<int>(formatProps.suggestedYChromaOffset));
        }

        const bool formatUndefined = formatProps.format == VK_FORMAT_UNDEFINED;
        if (formatUndefined)
        {
            if (!g_vkCmdBeginRenderPass ||
                !g_vkCmdEndRenderPass ||
                !g_vkCmdBindPipeline ||
                !g_vkCmdBindDescriptorSets ||
                !g_vkCmdSetViewport ||
                !g_vkCmdSetScissor ||
                !g_vkCmdDraw ||
                !g_vkCmdCopyImage ||
                !g_vkUpdateDescriptorSets)
            {
                LOGE("ImportHardwareBufferImage: missing Vulkan draw/descriptor functions (YCbCr path).");
                AHardwareBuffer_release(hardwareBuffer);
                return false;
            }

            const int incomingWidth = static_cast<int>(desc.width);
            const int incomingHeight = static_cast<int>(desc.height);
            if (incomingWidth <= 0 || incomingHeight <= 0)
            {
                LOGE("ImportHardwareBufferImage: invalid incoming size for externalFormat-only buffer.");
                AHardwareBuffer_release(hardwareBuffer);
                return false;
            }

            // Preflight: ask Unity for its destination texture format so our output matches byte layout / sRGB.
            UnityVulkanImage unityDst{};
            bool haveUnityDst = false;
            const bool shouldProbeUnityDst = texture.unityTexturePtr != nullptr && !texture.unityTextureBound;
            if (shouldProbeUnityDst)
            {
                if (!g_graphicsVulkan->AccessTexture(
                        texture.unityTexturePtr,
                        UnityVulkanWholeImage,
                        VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                        VK_PIPELINE_STAGE_TRANSFER_BIT,
                        VK_ACCESS_TRANSFER_WRITE_BIT,
                        kUnityVulkanResourceAccess_PipelineBarrier,
                        &unityDst))
                {
                    LOGE("ImportHardwareBufferImage: AccessTexture failed for Unity dst texture (preflight).");
                }
                else if (unityDst.image == VK_NULL_HANDLE)
                {
                    LOGE("ImportHardwareBufferImage: Unity dst texture VkImage is null (preflight).");
                }
                else
                {
                    haveUnityDst = true;
                    texture.unityTextureFormat = unityDst.format;
                    if (!texture.unityTextureFormatLogged)
                    {
                        texture.unityTextureFormatLogged = true;
                        LOGI("ImportHardwareBufferImage: Unity dst format=%d (%s)",
                             static_cast<int>(unityDst.format),
                             VkFormatName(unityDst.format));
                    }
                }
            }

            const bool needsOutputCreate = texture.unityImage.image == VK_NULL_HANDLE;
            const bool needsOutputResize = !needsOutputCreate &&
                                           (texture.width != incomingWidth || texture.height != incomingHeight);
            if (needsOutputResize)
            {
                if (texture.unityTextureBound)
                {
                    RestoreUnityTexture(texture);
                }
                LOGI("ImportHardwareBufferImage: output size changed %dx%d -> %dx%d, recreating output.",
                     texture.width,
                     texture.height,
                     incomingWidth,
                     incomingHeight);
                DestroyNativeImage(texture);
            }

            texture.width = incomingWidth;
            texture.height = incomingHeight;

            if (texture.unityImage.image == VK_NULL_HANDLE)
            {
                VkFormat desiredFormat = texture.unityTextureFormat != VK_FORMAT_UNDEFINED
                                             ? texture.unityTextureFormat
                                             : VK_FORMAT_R8G8B8A8_UNORM;
                if (!CreateNativeImage(texture, commandBuffer, desiredFormat))
                {
                    LOGE("ImportHardwareBufferImage: failed to create output image for YCbCr conversion.");
                    AHardwareBuffer_release(hardwareBuffer);
                    return false;
                }
            }

            if (!EnsureYcbcrPipeline(texture.unityImage.format))
            {
                LOGE("ImportHardwareBufferImage: failed to init YCbCr conversion pipeline.");
                AHardwareBuffer_release(hardwareBuffer);
                return false;
            }
            if (!EnsureYcbcrSampler(formatProps))
            {
                LOGE("ImportHardwareBufferImage: failed to init YCbCr sampler conversion.");
                AHardwareBuffer_release(hardwareBuffer);
                return false;
            }
            if (!EnsurePerTextureConvertResources(texture))
            {
                LOGE("ImportHardwareBufferImage: failed to init per-texture conversion resources.");
                AHardwareBuffer_release(hardwareBuffer);
                return false;
            }

            VkExternalFormatANDROID externalFormatInfo{VK_STRUCTURE_TYPE_EXTERNAL_FORMAT_ANDROID};
            externalFormatInfo.externalFormat = formatProps.externalFormat;

            VkExternalMemoryImageCreateInfo externalCreateInfo{VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO};
            externalCreateInfo.pNext = &externalFormatInfo;
            externalCreateInfo.handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_ANDROID_HARDWARE_BUFFER_BIT_ANDROID;

            VkImageCreateInfo srcImageInfo{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
            srcImageInfo.pNext = &externalCreateInfo;
            srcImageInfo.flags = VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT;
            srcImageInfo.imageType = VK_IMAGE_TYPE_2D;
            srcImageInfo.extent = {static_cast<uint32_t>(incomingWidth), static_cast<uint32_t>(incomingHeight), 1};
            srcImageInfo.mipLevels = 1;
            srcImageInfo.arrayLayers = 1;
            srcImageInfo.format = VK_FORMAT_UNDEFINED;
            srcImageInfo.tiling = VK_IMAGE_TILING_OPTIMAL;
            srcImageInfo.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
            srcImageInfo.usage = VK_IMAGE_USAGE_SAMPLED_BIT;
            srcImageInfo.samples = VK_SAMPLE_COUNT_1_BIT;
            srcImageInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE;

            VkImage srcImage = VK_NULL_HANDLE;
            if (g_vkCreateImage(g_vulkanInstance.device, &srcImageInfo, nullptr, &srcImage) != VK_SUCCESS)
            {
                LOGE("ImportHardwareBufferImage: vkCreateImage failed (externalFormat source).");
                AHardwareBuffer_release(hardwareBuffer);
                return false;
            }

            VkImportAndroidHardwareBufferInfoANDROID importInfo{VK_STRUCTURE_TYPE_IMPORT_ANDROID_HARDWARE_BUFFER_INFO_ANDROID};
            importInfo.buffer = hardwareBuffer;

            VkMemoryDedicatedAllocateInfo memoryDedicatedInfo{VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO};
            memoryDedicatedInfo.image = srcImage;

            VkMemoryAllocateInfo allocInfo{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
            allocInfo.pNext = &importInfo;
            importInfo.pNext = &memoryDedicatedInfo;
            allocInfo.memoryTypeIndex = FindMemoryType(bufferProps.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
            allocInfo.allocationSize = bufferProps.allocationSize;

            VkDeviceMemory srcMemory = VK_NULL_HANDLE;
            if (g_vkAllocateMemory(g_vulkanInstance.device, &allocInfo, nullptr, &srcMemory) != VK_SUCCESS)
            {
                LOGE("ImportHardwareBufferImage: vkAllocateMemory failed (externalFormat source).");
                g_vkDestroyImage(g_vulkanInstance.device, srcImage, nullptr);
                AHardwareBuffer_release(hardwareBuffer);
                return false;
            }

            if (g_vkBindImageMemory(g_vulkanInstance.device, srcImage, srcMemory, 0) != VK_SUCCESS)
            {
                LOGE("ImportHardwareBufferImage: vkBindImageMemory failed (externalFormat source).");
                g_vkDestroyImage(g_vulkanInstance.device, srcImage, nullptr);
                g_vkFreeMemory(g_vulkanInstance.device, srcMemory, nullptr);
                AHardwareBuffer_release(hardwareBuffer);
                return false;
            }

            VkSamplerYcbcrConversionInfo viewConvInfo{VK_STRUCTURE_TYPE_SAMPLER_YCBCR_CONVERSION_INFO};
            viewConvInfo.conversion = g_ycbcrSampler.conversion;

            VkImageViewCreateInfo srcViewInfo{VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO};
            srcViewInfo.pNext = &viewConvInfo;
            srcViewInfo.image = srcImage;
            srcViewInfo.viewType = VK_IMAGE_VIEW_TYPE_2D;
            srcViewInfo.format = VK_FORMAT_UNDEFINED;
            srcViewInfo.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
            srcViewInfo.subresourceRange.baseMipLevel = 0;
            srcViewInfo.subresourceRange.levelCount = 1;
            srcViewInfo.subresourceRange.baseArrayLayer = 0;
            srcViewInfo.subresourceRange.layerCount = 1;

            VkImageView srcView = VK_NULL_HANDLE;
            if (g_vkCreateImageView(g_vulkanInstance.device, &srcViewInfo, nullptr, &srcView) != VK_SUCCESS)
            {
                LOGE("ImportHardwareBufferImage: vkCreateImageView failed (externalFormat source).");
                g_vkDestroyImage(g_vulkanInstance.device, srcImage, nullptr);
                g_vkFreeMemory(g_vulkanInstance.device, srcMemory, nullptr);
                AHardwareBuffer_release(hardwareBuffer);
                return false;
            }

            if (!TransitionImageLayout(commandBuffer,
                                      srcImage,
                                      VK_IMAGE_LAYOUT_UNDEFINED,
                                      VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
                                      0,
                                      VK_ACCESS_SHADER_READ_BIT,
                                      VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
                                      VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT))
            {
                LOGE("ImportHardwareBufferImage: failed to transition externalFormat source to shader-read.");
                g_vkDestroyImageView(g_vulkanInstance.device, srcView, nullptr);
                g_vkDestroyImage(g_vulkanInstance.device, srcImage, nullptr);
                g_vkFreeMemory(g_vulkanInstance.device, srcMemory, nullptr);
                AHardwareBuffer_release(hardwareBuffer);
                return false;
            }

            VkDescriptorImageInfo descriptorImage{};
            descriptorImage.sampler = g_ycbcrSampler.sampler;
            descriptorImage.imageView = srcView;
            descriptorImage.imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;

            VkWriteDescriptorSet write{VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET};
            write.dstSet = texture.descriptorSet;
            write.dstBinding = 0;
            write.dstArrayElement = 0;
            write.descriptorCount = 1;
            write.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
            write.pImageInfo = &descriptorImage;
            g_vkUpdateDescriptorSets(g_vulkanInstance.device, 1, &write, 0, nullptr);

            if (texture.unityImage.layout != VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL)
            {
                if (!TransitionImageLayout(commandBuffer,
                                          texture.unityImage.image,
                                          texture.unityImage.layout,
                                          VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                                          VK_ACCESS_SHADER_READ_BIT,
                                          VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
                                          VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
                                          VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT))
                {
                    LOGE("ImportHardwareBufferImage: failed to transition output image to color attachment.");
                    g_vkDestroyImageView(g_vulkanInstance.device, srcView, nullptr);
                    g_vkDestroyImage(g_vulkanInstance.device, srcImage, nullptr);
                    g_vkFreeMemory(g_vulkanInstance.device, srcMemory, nullptr);
                    AHardwareBuffer_release(hardwareBuffer);
                    return false;
                }
                texture.unityImage.layout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;
            }

            VkClearValue clear{};
            clear.color.float32[0] = 0.0f;
            clear.color.float32[1] = 0.0f;
            clear.color.float32[2] = 0.0f;
            clear.color.float32[3] = 1.0f;

            VkRenderPassBeginInfo rpBegin{VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO};
            rpBegin.renderPass = g_ycbcrPipeline.renderPass;
            rpBegin.framebuffer = texture.framebuffer;
            rpBegin.renderArea.offset = {0, 0};
            rpBegin.renderArea.extent = {static_cast<uint32_t>(incomingWidth), static_cast<uint32_t>(incomingHeight)};
            rpBegin.clearValueCount = 1;
            rpBegin.pClearValues = &clear;

            g_vkCmdBeginRenderPass(commandBuffer, &rpBegin, VK_SUBPASS_CONTENTS_INLINE);
            g_vkCmdBindPipeline(commandBuffer, VK_PIPELINE_BIND_POINT_GRAPHICS, g_ycbcrPipeline.pipeline);

            VkViewport viewport{};
            viewport.x = 0.0f;
            viewport.y = 0.0f;
            viewport.width = static_cast<float>(incomingWidth);
            viewport.height = static_cast<float>(incomingHeight);
            viewport.minDepth = 0.0f;
            viewport.maxDepth = 1.0f;
            g_vkCmdSetViewport(commandBuffer, 0, 1, &viewport);

            VkRect2D scissor{};
            scissor.offset = {0, 0};
            scissor.extent = {static_cast<uint32_t>(incomingWidth), static_cast<uint32_t>(incomingHeight)};
            g_vkCmdSetScissor(commandBuffer, 0, 1, &scissor);

            g_vkCmdBindDescriptorSets(commandBuffer,
                                     VK_PIPELINE_BIND_POINT_GRAPHICS,
                                     g_ycbcrPipeline.pipelineLayout,
                                     0,
                                     1,
                                     &texture.descriptorSet,
                                     0,
                                     nullptr);

            // Push constants: (debugMode, matrix, inputMode, flags) + color mul/add.
            // matrix: 0=BT.601, 1=BT.709. inputMode: 0=normalized, 1=byte-narrow (Java-like), 2=byte-full, -1=passthrough.
            struct alignas(16) PushConstants
            {
                int32_t p[4];
                float mul[4];
                float add[4];
            };
            PushConstants push{};
            push.p[0] = g_ycbcrOverride.debugMode.load(std::memory_order_relaxed);

            // Map model override to conversion matrix in shader.
            const int userModel = g_ycbcrOverride.modelOverride.load(std::memory_order_relaxed);
            if (userModel == static_cast<int>(VK_SAMPLER_YCBCR_MODEL_CONVERSION_YCBCR_601))
            {
                push.p[1] = 0;
            }
            else
            {
                // Default to 709 (also covers Auto and other values).
                push.p[1] = 1;
            }

            const bool manualYuv = g_ycbcrOverride.manualYuv.load(std::memory_order_relaxed) != 0;
            if (manualYuv)
            {
                push.p[2] = g_ycbcrOverride.inputMode.load(std::memory_order_relaxed);
            }
            else
            {
                // Hardware YCbCr conversion already produced RGB; skip manual conversion.
                push.p[2] = -1;
            }
            int flags = 0;
            if (g_ycbcrOverride.swapUv.load(std::memory_order_relaxed) != 0)
            {
                flags |= 1;
            }
            if (g_ycbcrOverride.invertU.load(std::memory_order_relaxed) != 0)
            {
                flags |= 2;
            }
            if (g_ycbcrOverride.invertV.load(std::memory_order_relaxed) != 0)
            {
                flags |= 4;
            }
            const int order = g_ycbcrOverride.channelOrder.load(std::memory_order_relaxed) & 7;
            flags |= (order << 3);
            push.p[3] = flags;

            push.mul[0] = g_colorTransform.mulR.load(std::memory_order_relaxed);
            push.mul[1] = g_colorTransform.mulG.load(std::memory_order_relaxed);
            push.mul[2] = g_colorTransform.mulB.load(std::memory_order_relaxed);
            push.mul[3] = g_colorTransform.mulA.load(std::memory_order_relaxed);
            push.add[0] = g_colorTransform.addR.load(std::memory_order_relaxed);
            push.add[1] = g_colorTransform.addG.load(std::memory_order_relaxed);
            push.add[2] = g_colorTransform.addB.load(std::memory_order_relaxed);
            push.add[3] = g_colorTransform.addA.load(std::memory_order_relaxed);

            if (g_vkCmdPushConstants)
            {
                g_vkCmdPushConstants(commandBuffer,
                                     g_ycbcrPipeline.pipelineLayout,
                                     VK_SHADER_STAGE_FRAGMENT_BIT,
                                     0,
                                     sizeof(push),
                                     &push);
            }
            g_vkCmdDraw(commandBuffer, 3, 1, 0, 0);
            g_vkCmdEndRenderPass(commandBuffer);

            texture.unityImage.layout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;

            if (ShouldLogThrottled(g_lastExternalFormatConvertLogMs, 5000))
            {
                LOGI("ImportHardwareBufferImage: externalFormat-only frame converted to RGBA output (%dx%d externalFormat=0x%llx)",
                     incomingWidth,
                     incomingHeight,
                     static_cast<unsigned long long>(formatProps.externalFormat));
            }

            // Directly recreating Unity's texture binding inside the import event is not stable on this Quest runtime.
            // Stay on the copy path here so decoded frames remain visible on the target material.
            if (true)
            {
                if (texture.unityTexturePtr == nullptr)
                {
                    LOGE("ImportHardwareBufferImage: Unity texture pointer not assigned; cannot present converted frame.");
                    // Still return success for conversion, but Unity will show stale/empty texture.
                }
                else if (!haveUnityDst || unityDst.image == VK_NULL_HANDLE)
                {
                    LOGE("ImportHardwareBufferImage: Unity dst texture not accessible; skipping copy.");
                }
                else
                {
                    UnityVulkanRecordingState recordingStateAfterAccess{};
                    VkCommandBuffer cmd2 = VK_NULL_HANDLE;
                    if (g_graphicsVulkan->CommandRecordingState(
                            &recordingStateAfterAccess, kUnityVulkanGraphicsQueueAccess_DontCare))
                    {
                        cmd2 = recordingStateAfterAccess.commandBuffer;
                    }

                    if (cmd2 == VK_NULL_HANDLE)
                    {
                        LOGE("ImportHardwareBufferImage: no command buffer available for copy.");
                    }
                    else
                    {
                        // Transition plugin output to transfer src.
                        if (!TransitionImageLayout(
                                cmd2,
                                texture.unityImage.image,
                                texture.unityImage.layout,
                                VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                                VK_ACCESS_SHADER_READ_BIT,
                                VK_ACCESS_TRANSFER_READ_BIT,
                                VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
                                VK_PIPELINE_STAGE_TRANSFER_BIT))
                        {
                            LOGE("ImportHardwareBufferImage: failed to transition output to transfer-src.");
                        }
                        else
                        {
                            texture.unityImage.layout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;

                            VkImageCopy region{};
                            region.srcSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
                            region.srcSubresource.mipLevel = 0;
                            region.srcSubresource.baseArrayLayer = 0;
                            region.srcSubresource.layerCount = 1;
                            region.dstSubresource = region.srcSubresource;
                            region.extent = {static_cast<uint32_t>(incomingWidth), static_cast<uint32_t>(incomingHeight), 1};

                            g_vkCmdCopyImage(cmd2,
                                             texture.unityImage.image,
                                             VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                                             unityDst.image,
                                             VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                                             1,
                                             &region);

                            // Transition plugin output back to shader-read for the next frame (and for any debug sampling).
                            if (!TransitionImageLayout(
                                    cmd2,
                                    texture.unityImage.image,
                                    VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                                    VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
                                    VK_ACCESS_TRANSFER_READ_BIT,
                                    VK_ACCESS_SHADER_READ_BIT,
                                    VK_PIPELINE_STAGE_TRANSFER_BIT,
                                    VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT))
                            {
                                LOGE("ImportHardwareBufferImage: failed to transition output back to shader-read.");
                            }
                            else
                            {
                                texture.unityImage.layout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
                            }

                            // Transition Unity dst texture to shader-read so Unity can sample it safely.
                            UnityVulkanImage unityDstRead{};
                            if (!g_graphicsVulkan->AccessTexture(
                                    texture.unityTexturePtr,
                                    UnityVulkanWholeImage,
                                    VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
                                    VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
                                    VK_ACCESS_SHADER_READ_BIT,
                                    kUnityVulkanResourceAccess_PipelineBarrier,
                                    &unityDstRead))
                            {
                                LOGE("ImportHardwareBufferImage: AccessTexture failed to transition Unity dst to shader-read.");
                            }
                        }
                    }
                }
            }

            // IMPORTANT: We must not destroy/release source resources immediately. The command buffer is only recorded
            // here; Unity submits it later. Releasing AHB / VkImage / VkImageView / VkDeviceMemory too early will crash.
            {
                std::lock_guard<std::mutex> lock(g_deferredReleaseMutex);
                DeferredYcbcrSourceRelease deferred{};
                deferred.usedFrame = currentFrameNumber;
                deferred.image = srcImage;
                deferred.view = srcView;
                deferred.memory = srcMemory;
                deferred.buffer = hardwareBuffer;
                g_deferredYcbcrReleases.push_back(deferred);
            }

            return true;
        }

        VkExternalMemoryImageCreateInfo externalCreateInfo{
            VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO};
        externalCreateInfo.handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_ANDROID_HARDWARE_BUFFER_BIT_ANDROID;

        VkImageCreateInfo imageInfo{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
        imageInfo.pNext = &externalCreateInfo;
        imageInfo.flags = VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT;
        imageInfo.imageType = VK_IMAGE_TYPE_2D;
        imageInfo.extent = {static_cast<uint32_t>(desc.width),
                            static_cast<uint32_t>(desc.height), 1};
        imageInfo.mipLevels = 1;
        imageInfo.arrayLayers = 1;
        imageInfo.format = formatProps.format;
        imageInfo.tiling = VK_IMAGE_TILING_OPTIMAL;
        imageInfo.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
        imageInfo.usage = VK_IMAGE_USAGE_SAMPLED_BIT;
        imageInfo.samples = VK_SAMPLE_COUNT_1_BIT;
        imageInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE;

        VkImage image = VK_NULL_HANDLE;
        if (g_vkCreateImage(g_vulkanInstance.device, &imageInfo, nullptr, &image) != VK_SUCCESS)
        {
            LOGE("vkCreateImage failed (import path)");
            AHardwareBuffer_release(hardwareBuffer);
            return false;
        }

        VkImportAndroidHardwareBufferInfoANDROID importInfo{
            VK_STRUCTURE_TYPE_IMPORT_ANDROID_HARDWARE_BUFFER_INFO_ANDROID};
        importInfo.buffer = hardwareBuffer;

        VkMemoryDedicatedAllocateInfo memoryDedicatedInfo{
            VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO};
        memoryDedicatedInfo.image = image;

        VkMemoryAllocateInfo allocInfo{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
        allocInfo.pNext = &importInfo;
        importInfo.pNext = &memoryDedicatedInfo;
        allocInfo.memoryTypeIndex = FindMemoryType(
            bufferProps.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
        allocInfo.allocationSize = bufferProps.allocationSize;

        VkDeviceMemory memory = VK_NULL_HANDLE;
        if (g_vkAllocateMemory(g_vulkanInstance.device, &allocInfo, nullptr, &memory) != VK_SUCCESS)
        {
            LOGE("vkAllocateMemory failed (import path)");
            g_vkDestroyImage(g_vulkanInstance.device, image, nullptr);
            AHardwareBuffer_release(hardwareBuffer);
            return false;
        }

        if (g_vkBindImageMemory(g_vulkanInstance.device, image, memory, 0) != VK_SUCCESS)
        {
            LOGE("vkBindImageMemory failed (import path)");
            g_vkDestroyImage(g_vulkanInstance.device, image, nullptr);
            g_vkFreeMemory(g_vulkanInstance.device, memory, nullptr);
            AHardwareBuffer_release(hardwareBuffer);
            return false;
        }

        VkImageViewCreateInfo viewInfo{VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO};
        viewInfo.image = image;
        viewInfo.viewType = VK_IMAGE_VIEW_TYPE_2D;
        viewInfo.format = imageInfo.format;
        viewInfo.components.r = VK_COMPONENT_SWIZZLE_R;
        viewInfo.components.g = VK_COMPONENT_SWIZZLE_G;
        viewInfo.components.b = VK_COMPONENT_SWIZZLE_B;
        viewInfo.components.a = VK_COMPONENT_SWIZZLE_A;
        viewInfo.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        viewInfo.subresourceRange.baseMipLevel = 0;
        viewInfo.subresourceRange.levelCount = 1;
        viewInfo.subresourceRange.baseArrayLayer = 0;
        viewInfo.subresourceRange.layerCount = 1;

        VkImageView imageView = VK_NULL_HANDLE;
        if (g_vkCreateImageView(g_vulkanInstance.device, &viewInfo, nullptr, &imageView) != VK_SUCCESS)
        {
            LOGE("vkCreateImageView failed (import path)");
            g_vkDestroyImage(g_vulkanInstance.device, image, nullptr);
            g_vkFreeMemory(g_vulkanInstance.device, memory, nullptr);
            AHardwareBuffer_release(hardwareBuffer);
            return false;
        }

        ExternalTexture temp;
        temp.unityImage.memory.memory = memory;
        temp.unityImage.memory.offset = 0;
        temp.unityImage.memory.size = bufferProps.allocationSize;
        temp.unityImage.memory.mapped = nullptr;
        temp.unityImage.memory.flags = VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT;
        temp.unityImage.memory.memoryTypeIndex = allocInfo.memoryTypeIndex;
        temp.unityImage.image = image;
        temp.unityImage.layout = VK_IMAGE_LAYOUT_UNDEFINED;
        temp.unityImage.aspect = VK_IMAGE_ASPECT_COLOR_BIT;
        temp.unityImage.usage = imageInfo.usage;
        temp.unityImage.format = imageInfo.format;
        temp.unityImage.extent = imageInfo.extent;
        temp.unityImage.tiling = imageInfo.tiling;
        temp.unityImage.type = imageInfo.imageType;
        temp.unityImage.samples = imageInfo.samples;
        temp.unityImage.layers = 1;
        temp.unityImage.mipCount = 1;
        temp.unityImage.reserved[0] = imageView;

        if (!TransitionImageToShaderRead(commandBuffer, temp))
        {
            LOGE("Failed to transition imported HardwareBuffer image.");
            if (imageView != VK_NULL_HANDLE && g_vkDestroyImageView)
            {
                g_vkDestroyImageView(g_vulkanInstance.device, imageView, nullptr);
            }
            if (image != VK_NULL_HANDLE && g_vkDestroyImage)
            {
                g_vkDestroyImage(g_vulkanInstance.device, image, nullptr);
            }
            if (memory != VK_NULL_HANDLE && g_vkFreeMemory)
            {
                g_vkFreeMemory(g_vulkanInstance.device, memory, nullptr);
            }
            AHardwareBuffer_release(hardwareBuffer);
            return false;
        }

        DestroyNativeImage(texture);
        texture.width = static_cast<int>(desc.width);
        texture.height = static_cast<int>(desc.height);
        texture.buffer = hardwareBuffer; // take ownership of caller's reference
        texture.memory = memory;
        texture.imageView = imageView;
        texture.unityImage = temp.unityImage;
        return true;
    }

    void DestroyNativeImage(ExternalTexture& texture)
    {
        LOGI("DestroyNativeImage(%p) begin", &texture);
        if (texture.pendingBuffer)
        {
            AHardwareBuffer_release(texture.pendingBuffer);
            texture.pendingBuffer = nullptr;
        }
        if (texture.pendingFenceFd >= 0)
        {
            CloseFenceFd(texture.pendingFenceFd);
            texture.pendingFenceFd = -1;
        }
        if (texture.framebuffer != VK_NULL_HANDLE && g_vkDestroyFramebuffer)
        {
            g_vkDestroyFramebuffer(g_vulkanInstance.device, texture.framebuffer, nullptr);
            texture.framebuffer = VK_NULL_HANDLE;
        }
        if (texture.descriptorPool != VK_NULL_HANDLE && g_vkDestroyDescriptorPool)
        {
            g_vkDestroyDescriptorPool(g_vulkanInstance.device, texture.descriptorPool, nullptr);
            texture.descriptorPool = VK_NULL_HANDLE;
            texture.descriptorSet = VK_NULL_HANDLE;
        }
        if (texture.imageView != VK_NULL_HANDLE && g_vkDestroyImageView)
        {
            g_vkDestroyImageView(g_vulkanInstance.device, texture.imageView, nullptr);
            texture.imageView = VK_NULL_HANDLE;
        }

        if (texture.unityImage.image != VK_NULL_HANDLE && g_vkDestroyImage)
        {
            g_vkDestroyImage(g_vulkanInstance.device, texture.unityImage.image, nullptr);
            texture.unityImage.image = VK_NULL_HANDLE;
        }

        if (texture.memory != VK_NULL_HANDLE && g_vkFreeMemory)
        {
            g_vkFreeMemory(g_vulkanInstance.device, texture.memory, nullptr);
            texture.memory = VK_NULL_HANDLE;
        }

        if (texture.buffer)
        {
            AHardwareBuffer_release(texture.buffer);
            texture.buffer = nullptr;
        }
        LOGI("DestroyNativeImage(%p) finished", &texture);
        texture.unityImage = {};
    }

    void ClearDeferredYcbcrReleases()
    {
        if (!g_vulkanReady)
        {
            std::lock_guard<std::mutex> lock(g_deferredReleaseMutex);
            g_deferredYcbcrReleases.clear();
            return;
        }

        std::lock_guard<std::mutex> lock(g_deferredReleaseMutex);
        for (const auto& item : g_deferredYcbcrReleases)
        {
            if (item.view != VK_NULL_HANDLE && g_vkDestroyImageView)
            {
                g_vkDestroyImageView(g_vulkanInstance.device, item.view, nullptr);
            }
            if (item.image != VK_NULL_HANDLE && g_vkDestroyImage)
            {
                g_vkDestroyImage(g_vulkanInstance.device, item.image, nullptr);
            }
            if (item.memory != VK_NULL_HANDLE && g_vkFreeMemory)
            {
                g_vkFreeMemory(g_vulkanInstance.device, item.memory, nullptr);
            }
            if (item.buffer)
            {
                AHardwareBuffer_release(item.buffer);
            }
        }
        g_deferredYcbcrReleases.clear();
    }

    void RestoreUnityTexture(ExternalTexture& texture)
    {
        if (!texture.unityTextureBound ||
            !texture.hasOriginalUnityImage ||
            texture.unityTexturePtr == nullptr ||
            !g_graphicsVulkan)
        {
            texture.unityTextureBound = false;
            texture.hasOriginalUnityImage = false;
            texture.unityTexturePtr = nullptr;
            return;
        }

        g_graphicsVulkan->EnsureOutsideRenderPass();

        UnityVulkanImage restoreImage = texture.originalUnityImage;
        if (!g_graphicsVulkan->AccessTexture(
                texture.unityTexturePtr,
                UnityVulkanWholeImage,
                restoreImage.layout,
                VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
                VK_ACCESS_SHADER_READ_BIT,
                kUnityVulkanResourceAccess_Recreate,
                &restoreImage))
        {
            LOGE("Failed to restore Unity texture binding.");
        }
        texture.unityTextureBound = false;
        texture.hasOriginalUnityImage = false;
        texture.unityTexturePtr = nullptr;
    }

    bool TransitionImageToShaderRead(VkCommandBuffer commandBuffer, ExternalTexture& texture)
    {
        if (commandBuffer == VK_NULL_HANDLE || !g_vkCmdPipelineBarrier)
        {
            LOGE("TransitionImageToShaderRead requires a valid command buffer.");
            return false;
        }

        if (texture.unityImage.image == VK_NULL_HANDLE)
        {
            LOGE("Cannot transition image layout – VkImage handle invalid.");
            return false;
        }

        VkImageMemoryBarrier barrier{VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER};
        barrier.srcAccessMask = 0;
        barrier.dstAccessMask = VK_ACCESS_SHADER_READ_BIT;
        barrier.oldLayout = VK_IMAGE_LAYOUT_UNDEFINED;
        barrier.newLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
        barrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        barrier.image = texture.unityImage.image;
        barrier.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        barrier.subresourceRange.baseMipLevel = 0;
        barrier.subresourceRange.levelCount = 1;
        barrier.subresourceRange.baseArrayLayer = 0;
        barrier.subresourceRange.layerCount = 1;

        g_vkCmdPipelineBarrier(
            commandBuffer,
            VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
            VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
            0,
            0,
            nullptr,
            0,
            nullptr,
            1,
            &barrier);

        texture.unityImage.layout = barrier.newLayout;
        LOGI("TransitionImageToShaderRead(%p) newLayout=%d", &texture, barrier.newLayout);
        return true;
    }
    
    bool BindUnityTexture(ExternalTexture& texture)
    {
        if (!g_graphicsVulkan || texture.unityTexturePtr == nullptr)
        {
            LOGE("BindUnityTexture requires a valid Unity texture pointer.");
            return false;
        }

        if (texture.unityImage.image == VK_NULL_HANDLE)
        {
            LOGE("Cannot bind Unity texture before native image exists.");
            return false;
        }

        g_graphicsVulkan->EnsureOutsideRenderPass();

        if (!texture.hasOriginalUnityImage)
        {
            UnityVulkanImage original{};
            if (!g_graphicsVulkan->AccessTexture(
                    texture.unityTexturePtr,
                    UnityVulkanWholeImage,
                    texture.unityImage.layout,
                    VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
                    VK_ACCESS_SHADER_READ_BIT,
                    kUnityVulkanResourceAccess_ObserveOnly,
                    &original))
            {
                LOGE("Failed to capture Unity texture before binding.");
                return false;
            }
            texture.originalUnityImage = original;
            texture.hasOriginalUnityImage = true;
        }

        UnityVulkanImage replacement = texture.unityImage;
        if (!g_graphicsVulkan->AccessTexture(
                texture.unityTexturePtr,
                UnityVulkanWholeImage,
                texture.unityImage.layout,
                VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
                VK_ACCESS_SHADER_READ_BIT,
                kUnityVulkanResourceAccess_Recreate,
                &replacement))
        {
            LOGE("AccessTexture (recreate) failed while binding external image.");
            return false;
        }

        texture.unityTextureBound = true;
        LOGI("Unity texture %p now bound to native image %p", texture.unityTexturePtr, texture.unityImage.image);
        return true;
    }

    void UNITY_INTERFACE_API OnRenderEvent(int eventId, void* data)
    {
        auto* texture = reinterpret_cast<ExternalTexture*>(data);
        if (!texture)
        {
            return;
        }

        const auto renderEvent = static_cast<RenderEventId>(eventId);
        if (!g_vulkanReady || !g_graphicsVulkan)
        {
            CompleteGpuOperation(*texture, false, false, false);
            return;
        }

        TexturePendingOp requestedOp = TexturePendingOp::None;
        bool readySnapshot = false;
        bool aliveSnapshot = false;
        {
            std::lock_guard<std::mutex> lock(texture->stateMutex);
            requestedOp = texture->pendingOp;
            readySnapshot = texture->gpuReady;
            aliveSnapshot = texture->alive;
        }

        auto expectedOpForEvent = [&](RenderEventId evt) -> TexturePendingOp {
            switch (evt)
            {
                case RenderEventId::CreateTexture:
                    return TexturePendingOp::Create;
                case RenderEventId::BindTexture:
                    return TexturePendingOp::BindUnityTexture;
                case RenderEventId::ImportHardwareBuffer:
                    return TexturePendingOp::ImportHardwareBuffer;
                case RenderEventId::DestroyTexture:
                    return TexturePendingOp::Destroy;
                default:
                    return TexturePendingOp::None;
            }
        };

        if (requestedOp != expectedOpForEvent(renderEvent))
        {
            LOGE("Render event %d received but pending op is %d - ignoring.", eventId, static_cast<int>(requestedOp));
            CompleteGpuOperation(*texture, readySnapshot, aliveSnapshot, false);
            return;
        }

        if (ShouldLogThrottled(g_lastRenderEventLogMs, 5000))
        {
            LOGI("Render event %d for texture=%p (op=%d)", eventId, texture, static_cast<int>(requestedOp));
        }

        switch (renderEvent)
        {
            case RenderEventId::CreateTexture:
            {
                UnityVulkanRecordingState recordingState{};
                VkCommandBuffer commandBuffer = VK_NULL_HANDLE;

                if (g_graphicsVulkan)
                {
                    g_graphicsVulkan->EnsureOutsideRenderPass();
                    if (!g_graphicsVulkan->CommandRecordingState(
                            &recordingState, kUnityVulkanGraphicsQueueAccess_DontCare))
                    {
                        LOGE("Failed to query Unity Vulkan recording state for texture creation.");
                    }
                    else
                    {
                        commandBuffer = recordingState.commandBuffer;
                    }
                }

                bool success = false;
                if (commandBuffer == VK_NULL_HANDLE)
                {
                    LOGE("Unity did not provide a valid command buffer for texture creation.");
                }
                else
                {
                    success = CreateNativeImage(*texture, commandBuffer, VK_FORMAT_R8G8B8A8_UNORM);
                }

                CompleteGpuOperation(*texture, success, success, success);
                break;
            }
            case RenderEventId::BindTexture:
            {
                g_graphicsVulkan->EnsureOutsideRenderPass();
                bool success = BindUnityTexture(*texture);
                CompleteGpuOperation(*texture, texture->gpuReady && success, texture->alive, success);
                break;
            }
            case RenderEventId::ImportHardwareBuffer:
            {
                UnityVulkanRecordingState recordingState{};
                VkCommandBuffer commandBuffer = VK_NULL_HANDLE;

                g_graphicsVulkan->EnsureOutsideRenderPass();
                if (g_graphicsVulkan->CommandRecordingState(
                        &recordingState, kUnityVulkanGraphicsQueueAccess_DontCare))
                {
                    commandBuffer = recordingState.commandBuffer;
                }

                bool success = false;
                AHardwareBuffer* buffer = nullptr;
                int fenceFd = -1;
                {
                    std::lock_guard<std::mutex> lock(texture->stateMutex);
                    buffer = texture->pendingBuffer;
                    texture->pendingBuffer = nullptr;
                    fenceFd = texture->pendingFenceFd;
                    texture->pendingFenceFd = -1;
                }

                // Release any previously used source resources that are now safe to destroy.
                PurgeDeferredYcbcrReleases(recordingState.safeFrameNumber);

                if (commandBuffer == VK_NULL_HANDLE)
                {
                    LOGE("Unity did not provide a valid command buffer for HardwareBuffer import.");
                    if (buffer)
                    {
                        AHardwareBuffer_release(buffer);
                        buffer = nullptr;
                    }
                    CloseFenceFd(fenceFd);
                }
                else if (buffer == nullptr)
                {
                    LOGE("No pending HardwareBuffer set for import.");
                    CloseFenceFd(fenceFd);
                }
                else
                {
                    success = ImportHardwareBufferImage(*texture, commandBuffer, buffer, fenceFd, recordingState.currentFrameNumber);
                    if (success)
                    {
                        {
                            std::lock_guard<std::mutex> lock(texture->stateMutex);
                            texture->gpuReady = true;
                            texture->alive = true;
                        }
                    }
                }

                CompleteGpuOperation(*texture, success, true, success);
                break;
            }
            case RenderEventId::DestroyTexture:
            {
                RestoreUnityTexture(*texture);
                DestroyNativeImage(*texture);
                CompleteGpuOperation(*texture, false, false, true);
                break;
            }
            default:
                LOGE("Unknown render event id %d", eventId);
                CompleteGpuOperation(*texture, false, false, false);
                break;
        }
    }
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces)
{
    g_unityInterfaces = interfaces;
    g_graphics = interfaces->Get<IUnityGraphics>();
    g_graphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
    OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
    if (g_graphics)
    {
        g_graphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
    }
}

extern "C" UnityRenderingEventAndData UNITY_INTERFACE_EXPORT QuestVulkan_GetRenderEventFunc()
{
    return OnRenderEvent;
}

extern "C" intptr_t UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API QuestVulkan_CreateTestTexture(int width, int height)
{
    if (!g_vulkanReady || !IsVulkanRenderer())
    {
        LOGE("QuestVulkan_CreateTestTexture called before Vulkan is ready");
        return 0;
    }

    if (width <= 0 || height <= 0)
    {
        LOGE("QuestVulkan_CreateTestTexture invalid size %d x %d", width, height);
        return 0;
    }

    auto texture = std::make_unique<ExternalTexture>();
    texture->width = width;
    texture->height = height;

    {
        std::lock_guard<std::mutex> lock(texture->stateMutex);
        texture->pendingOp = TexturePendingOp::Create;
        texture->gpuReady = false;
        texture->alive = false;
        texture->lastResult = false;
    }

    auto handle = reinterpret_cast<intptr_t>(texture.release());
    LOGI("QuestVulkan_CreateTestTexture -> handle=%p size=%dx%d", reinterpret_cast<void*>(handle), width, height);
    return handle;
}

extern "C" intptr_t UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API QuestVulkan_CreateStreamTexture(int width, int height)
{
    if (!g_vulkanReady || !IsVulkanRenderer())
    {
        LOGE("QuestVulkan_CreateStreamTexture called before Vulkan is ready");
        return 0;
    }

    auto texture = std::make_unique<ExternalTexture>();
    texture->width = width;
    texture->height = height;
    texture->pendingOp = TexturePendingOp::None;
    texture->gpuReady = false;
    texture->alive = true;
    texture->lastResult = true;

    auto handle = reinterpret_cast<intptr_t>(texture.release());
    LOGI("QuestVulkan_CreateStreamTexture -> handle=%p size=%dx%d", reinterpret_cast<void*>(handle), width, height);
    return handle;
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API QuestVulkan_AssignUnityTexture(intptr_t handle, intptr_t unityTexturePtr)
{
    auto texture = reinterpret_cast<ExternalTexture*>(handle);
    if (!texture)
    {
        return;
    }

    std::lock_guard<std::mutex> lock(texture->stateMutex);
    void* newPtr = reinterpret_cast<void*>(unityTexturePtr);
    if (texture->unityTexturePtr != newPtr)
    {
        texture->unityTexturePtr = newPtr;
        texture->unityTextureBound = false;
        texture->hasOriginalUnityImage = false;
        texture->originalUnityImage = {};
        texture->unityTextureFormat = VK_FORMAT_UNDEFINED;
        texture->unityTextureFormatLogged = false;
    }
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API QuestVulkan_SetYcbcrOverride(int model, int range, int swizzleMode)
{
    g_ycbcrOverride.modelOverride.store(model, std::memory_order_relaxed);
    g_ycbcrOverride.rangeOverride.store(range, std::memory_order_relaxed);
    g_ycbcrOverride.swizzleMode.store(swizzleMode, std::memory_order_relaxed);
    g_ycbcrOverride.xChromaOffsetOverride.store(-1, std::memory_order_relaxed);
    g_ycbcrOverride.yChromaOffsetOverride.store(-1, std::memory_order_relaxed);
    g_ycbcrOverride.generation.fetch_add(1, std::memory_order_relaxed);

    LOGI("QuestVulkan_SetYcbcrOverride model=%d range=%d swizzleMode=%d", model, range, swizzleMode);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API QuestVulkan_SetYcbcrOverride2(
    int model,
    int range,
    int swizzleMode,
    int xChromaOffset,
    int yChromaOffset)
{
    g_ycbcrOverride.modelOverride.store(model, std::memory_order_relaxed);
    g_ycbcrOverride.rangeOverride.store(range, std::memory_order_relaxed);
    g_ycbcrOverride.swizzleMode.store(swizzleMode, std::memory_order_relaxed);
    g_ycbcrOverride.xChromaOffsetOverride.store(xChromaOffset, std::memory_order_relaxed);
    g_ycbcrOverride.yChromaOffsetOverride.store(yChromaOffset, std::memory_order_relaxed);
    g_ycbcrOverride.generation.fetch_add(1, std::memory_order_relaxed);

    LOGI("QuestVulkan_SetYcbcrOverride2 model=%d range=%d swizzleMode=%d xOff=%d yOff=%d",
         model,
         range,
         swizzleMode,
         xChromaOffset,
         yChromaOffset);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API QuestVulkan_SetManualYuvParams(
    int enabled,
    int swapUv,
    int debugMode)
{
g_ycbcrOverride.manualYuv.store(enabled != 0 ? 1 : 0, std::memory_order_relaxed);
    g_ycbcrOverride.swapUv.store(swapUv != 0 ? 1 : 0, std::memory_order_relaxed);
    g_ycbcrOverride.debugMode.store(debugMode, std::memory_order_relaxed);
    g_ycbcrOverride.inputMode.store(0, std::memory_order_relaxed);
    g_ycbcrOverride.generation.fetch_add(1, std::memory_order_relaxed);

    LOGI("QuestVulkan_SetManualYuvParams enabled=%d swapUv=%d debugMode=%d",
         enabled != 0 ? 1 : 0,
         swapUv != 0 ? 1 : 0,
         debugMode);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API QuestVulkan_SetManualYuvParams2(
    int enabled,
    int swapUv,
    int debugMode,
    int inputMode)
{
g_ycbcrOverride.manualYuv.store(enabled != 0 ? 1 : 0, std::memory_order_relaxed);
    g_ycbcrOverride.swapUv.store(swapUv != 0 ? 1 : 0, std::memory_order_relaxed);
    g_ycbcrOverride.debugMode.store(debugMode, std::memory_order_relaxed);
    g_ycbcrOverride.inputMode.store(inputMode, std::memory_order_relaxed);
    g_ycbcrOverride.invertU.store(0, std::memory_order_relaxed);
    g_ycbcrOverride.invertV.store(0, std::memory_order_relaxed);
    g_ycbcrOverride.channelOrder.store(0, std::memory_order_relaxed);
    g_ycbcrOverride.generation.fetch_add(1, std::memory_order_relaxed);

    LOGI("QuestVulkan_SetManualYuvParams2 enabled=%d swapUv=%d debugMode=%d inputMode=%d",
         enabled != 0 ? 1 : 0,
         swapUv != 0 ? 1 : 0,
         debugMode,
         inputMode);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API QuestVulkan_SetManualYuvParams3(
    int enabled,
    int swapUv,
    int invertU,
    int invertV,
    int channelOrder,
    int debugMode,
    int inputMode)
{
g_ycbcrOverride.manualYuv.store(enabled != 0 ? 1 : 0, std::memory_order_relaxed);
    g_ycbcrOverride.swapUv.store(swapUv != 0 ? 1 : 0, std::memory_order_relaxed);
    g_ycbcrOverride.invertU.store(invertU != 0 ? 1 : 0, std::memory_order_relaxed);
    g_ycbcrOverride.invertV.store(invertV != 0 ? 1 : 0, std::memory_order_relaxed);
    g_ycbcrOverride.channelOrder.store(channelOrder, std::memory_order_relaxed);
    g_ycbcrOverride.debugMode.store(debugMode, std::memory_order_relaxed);
    g_ycbcrOverride.inputMode.store(inputMode, std::memory_order_relaxed);
    g_ycbcrOverride.generation.fetch_add(1, std::memory_order_relaxed);

    LOGI("QuestVulkan_SetManualYuvParams3 enabled=%d swapUv=%d invertU=%d invertV=%d channelOrder=%d debugMode=%d inputMode=%d",
         enabled != 0 ? 1 : 0,
         swapUv != 0 ? 1 : 0,
         invertU != 0 ? 1 : 0,
         invertV != 0 ? 1 : 0,
         channelOrder,
         debugMode,
         inputMode);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API QuestVulkan_SetColorTransform(
    float mulR,
    float mulG,
    float mulB,
    float mulA,
    float addR,
    float addG,
    float addB,
    float addA)
{
    g_colorTransform.mulR.store(mulR, std::memory_order_relaxed);
    g_colorTransform.mulG.store(mulG, std::memory_order_relaxed);
    g_colorTransform.mulB.store(mulB, std::memory_order_relaxed);
    g_colorTransform.mulA.store(mulA, std::memory_order_relaxed);
    g_colorTransform.addR.store(addR, std::memory_order_relaxed);
    g_colorTransform.addG.store(addG, std::memory_order_relaxed);
    g_colorTransform.addB.store(addB, std::memory_order_relaxed);
    g_colorTransform.addA.store(addA, std::memory_order_relaxed);

    LOGI("QuestVulkan_SetColorTransform mul=(%.3f,%.3f,%.3f,%.3f) add=(%.3f,%.3f,%.3f,%.3f)",
         mulR,
         mulG,
         mulB,
         mulA,
         addR,
         addG,
         addB,
         addA);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API QuestVulkan_SetHardwareBuffer(intptr_t handle, intptr_t hardwareBufferPtr, int width, int height)
{
    auto texture = reinterpret_cast<ExternalTexture*>(handle);
    if (!texture || hardwareBufferPtr == 0)
    {
        return;
    }

    std::lock_guard<std::mutex> lock(texture->stateMutex);
    AHardwareBuffer* incoming = reinterpret_cast<AHardwareBuffer*>(hardwareBufferPtr);
    AHardwareBuffer_acquire(incoming);
    if (texture->pendingBuffer)
    {
        AHardwareBuffer_release(texture->pendingBuffer);
    }
    if (texture->pendingFenceFd >= 0)
    {
        CloseFenceFd(texture->pendingFenceFd);
        texture->pendingFenceFd = -1;
    }
    texture->pendingBuffer = incoming;
    texture->width = width;
    texture->height = height;
    texture->pendingOp = TexturePendingOp::ImportHardwareBuffer;
    texture->lastResult = false;
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API QuestVulkan_SetHardwareBufferWithFence(
    intptr_t handle,
    intptr_t hardwareBufferPtr,
    int width,
    int height,
    int fenceFd)
{
    auto texture = reinterpret_cast<ExternalTexture*>(handle);
    if (!texture || hardwareBufferPtr == 0)
    {
        CloseFenceFd(fenceFd);
        return;
    }

    std::lock_guard<std::mutex> lock(texture->stateMutex);
    AHardwareBuffer* incoming = reinterpret_cast<AHardwareBuffer*>(hardwareBufferPtr);
    AHardwareBuffer_acquire(incoming);
    if (texture->pendingBuffer)
    {
        AHardwareBuffer_release(texture->pendingBuffer);
    }
    if (texture->pendingFenceFd >= 0)
    {
        CloseFenceFd(texture->pendingFenceFd);
    }
    texture->pendingBuffer = incoming;
    texture->pendingFenceFd = fenceFd;
    texture->width = width;
    texture->height = height;
    texture->pendingOp = TexturePendingOp::ImportHardwareBuffer;
    texture->lastResult = false;
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API QuestVulkan_ReleaseAHardwareBuffer(intptr_t hardwareBufferPtr)
{
    auto buffer = reinterpret_cast<AHardwareBuffer*>(hardwareBufferPtr);
    if (buffer)
    {
        AHardwareBuffer_release(buffer);
    }
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API QuestVulkan_CloseFenceFd(int fenceFd)
{
    CloseFenceFd(fenceFd);
}

extern "C" JNIEXPORT jlong JNICALL Java_com_example_questdecoder_HardwareBufferNativeBridge_nativeAcquireAHardwareBuffer(
    JNIEnv* env, jclass, jobject hardwareBufferObj)
{
    if (!env || !hardwareBufferObj)
    {
        return 0;
    }

    AHardwareBuffer* buffer = AHardwareBuffer_fromHardwareBuffer(env, hardwareBufferObj);
    if (buffer == nullptr)
    {
        return 0;
    }

    return reinterpret_cast<jlong>(buffer);
}

extern "C" JNIEXPORT void JNICALL Java_com_example_questdecoder_HardwareBufferNativeBridge_nativeSetDecoderColorInfo(
    JNIEnv*, jclass, jint standard, jint range, jint transfer, jint format)
{
    ApplyDecoderColorInfo(static_cast<int>(standard),
                          static_cast<int>(range),
                          static_cast<int>(transfer),
                          static_cast<int>(format));
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API QuestVulkan_SetUnityTexture(intptr_t handle, intptr_t unityTexturePtr)
{
    auto texture = reinterpret_cast<ExternalTexture*>(handle);
    if (!texture)
    {
        return;
    }

    std::lock_guard<std::mutex> lock(texture->stateMutex);
    texture->unityTexturePtr = reinterpret_cast<void*>(unityTexturePtr);
    texture->pendingOp = TexturePendingOp::BindUnityTexture;
    texture->lastResult = false;
    LOGI("QuestVulkan_SetUnityTexture handle=%p unityTexturePtr=%p", texture, texture->unityTexturePtr);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API QuestVulkan_RequestDestroyTexture(intptr_t handle)
{
    auto texture = reinterpret_cast<ExternalTexture*>(handle);
    if (!texture)
    {
        return;
    }

    std::lock_guard<std::mutex> lock(texture->stateMutex);
    texture->pendingOp = TexturePendingOp::Destroy;
    texture->lastResult = false;
    LOGI("QuestVulkan_RequestDestroyTexture handle=%p", texture);
}

extern "C" bool UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API QuestVulkan_WaitForTexture(intptr_t handle, uint32_t timeoutMs)
{
    auto texture = reinterpret_cast<ExternalTexture*>(handle);
    if (!texture)
    {
        return false;
    }

    std::unique_lock<std::mutex> lock(texture->stateMutex);
    auto complete = [&]() { return texture->pendingOp == TexturePendingOp::None; };
    if (texture->pendingOp == TexturePendingOp::None)
    {
        return texture->lastResult;
    }

    if (timeoutMs == 0)
    {
        texture->stateCondition.wait(lock, complete);
    }
    else
    {
        if (!texture->stateCondition.wait_for(lock, std::chrono::milliseconds(timeoutMs), complete))
        {
            return false;
        }
    }

    LOGI("QuestVulkan_WaitForTexture handle=%p result=%d ready=%d alive=%d",
         texture,
         texture->lastResult ? 1 : 0,
         texture->gpuReady ? 1 : 0,
         texture->alive ? 1 : 0);
    return texture->lastResult;
}

extern "C" int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API QuestVulkan_GetTextureOperationStatus(intptr_t handle)
{
    auto texture = reinterpret_cast<ExternalTexture*>(handle);
    if (!texture)
    {
        return -1;
    }

    std::lock_guard<std::mutex> lock(texture->stateMutex);
    if (texture->pendingOp != TexturePendingOp::None)
    {
        return 0;
    }

    return texture->lastResult ? 1 : 2;
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API QuestVulkan_DestroyTexture(intptr_t handle)
{
    auto texture = reinterpret_cast<ExternalTexture*>(handle);
    if (!texture)
    {
        return;
    }

    QuestVulkan_WaitForTexture(handle, 0);
    LOGI("QuestVulkan_DestroyTexture deleting handle=%p", texture);
    delete texture;
}

extern "C" intptr_t UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API QuestVulkan_GetUnityImage(intptr_t handle)
{
    auto texture = reinterpret_cast<ExternalTexture*>(handle);
    if (!texture || !g_vulkanReady)
    {
        return 0;
    }

    std::lock_guard<std::mutex> lock(texture->stateMutex);
    if (!texture->gpuReady ||
        !texture->alive ||
        texture->unityImage.image == VK_NULL_HANDLE)
    {
        return 0;
    }

    return reinterpret_cast<intptr_t>(&texture->unityImage);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API QuestVulkan_UpdateTestTexture(
    intptr_t handle, float r, float g, float b, float a)
{
    auto texture = reinterpret_cast<ExternalTexture*>(handle);
    if (!texture || !g_vulkanReady)
    {
        return;
    }

    AHardwareBuffer* buffer = nullptr;
    {
        std::lock_guard<std::mutex> lock(texture->stateMutex);
        if (!texture->gpuReady ||
            !texture->alive ||
            texture->pendingOp != TexturePendingOp::None ||
            texture->buffer == nullptr)
        {
            return;
        }
        buffer = texture->buffer;
    }

    uint8_t* data = nullptr;
    if (AHardwareBuffer_lock(buffer,
                             AHARDWAREBUFFER_USAGE_CPU_WRITE_OFTEN,
                             -1, nullptr, reinterpret_cast<void**>(&data)) != 0)
    {
        return;
    }

    AHardwareBuffer_Desc desc{};
    AHardwareBuffer_describe(buffer, &desc);
    const int rowStride = static_cast<int>(desc.stride) * 4;

    uint8_t color[4] = {
        static_cast<uint8_t>(r * 255.0f),
        static_cast<uint8_t>(g * 255.0f),
        static_cast<uint8_t>(b * 255.0f),
        static_cast<uint8_t>(a * 255.0f),
    };

    for (uint32_t y = 0; y < desc.height; ++y)
    {
        uint8_t* row = data + y * rowStride;
        for (uint32_t x = 0; x < desc.width; ++x)
        {
            std::memcpy(row + x * 4, color, 4);
        }
    }

    AHardwareBuffer_unlock(buffer, nullptr);
}
