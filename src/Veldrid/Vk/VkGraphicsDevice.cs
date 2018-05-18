﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Vulkan;
using static Veldrid.Vk.VulkanUtil;
using static Vulkan.VulkanNative;

namespace Veldrid.Vk
{
    internal unsafe class VkGraphicsDevice : GraphicsDevice
    {
        private static readonly FixedUtf8String s_name = "Veldrid-VkGraphicsDevice";

        private VkInstance _instance;
        private VkPhysicalDevice _physicalDevice;
        private VkDeviceMemoryManager _memoryManager;
        private VkPhysicalDeviceProperties _physicalDeviceProperties;
        private VkPhysicalDeviceFeatures _physicalDeviceFeatures;
        private VkPhysicalDeviceMemoryProperties _physicalDeviceMemProperties;
        private VkDevice _device;
        private uint _graphicsQueueIndex;
        private uint _presentQueueIndex;
        private VkCommandPool _graphicsCommandPool;
        private readonly ConditionalLock _graphicsCommandPoolLock = new ConditionalLock();
        private VkQueue _graphicsQueue;
        private readonly ConditionalLock _graphicsQueueLock = new ConditionalLock();
        private VkDebugReportCallbackEXT _debugCallbackHandle;
        private PFN_vkDebugReportCallbackEXT _debugCallbackFunc;
        private readonly ConditionalLock _commandListsToDisposeLock = new ConditionalLock();
        private readonly List<VkCommandList> _commandListsToDispose = new List<VkCommandList>();
        private bool _debugMarkerEnabled;
        private vkDebugMarkerSetObjectNameEXT_d _setObjectNameDelegate;
        private bool _multiThreaded;
        private CommandList _immediateCL;

        private const int SharedCommandPoolCount = 4;
        private ConcurrentStack<SharedCommandPool> _sharedGraphicsCommandPools = new ConcurrentStack<SharedCommandPool>();
        private VkDescriptorPoolManager _descriptorPoolManager;
        private FixedUtf8String _platformSurfaceExtension;
        private bool _standardValidationSupported;

        // Staging Resources
        private const uint MinStagingBufferSize = 64;
        private const uint MaxStagingBufferSize = 512;

        private readonly ConditionalLock _stagingResourcesLock = new ConditionalLock();
        private readonly List<VkTexture> _availableStagingTextures = new List<VkTexture>();
        private readonly List<VkBuffer> _availableStagingBuffers = new List<VkBuffer>();

        private readonly Dictionary<VkCommandBuffer, VkTexture> _submittedStagingTextures
            = new Dictionary<VkCommandBuffer, VkTexture>();
        private readonly Dictionary<VkCommandBuffer, VkBuffer> _submittedStagingBuffers
            = new Dictionary<VkCommandBuffer, VkBuffer>();
        private readonly Dictionary<VkCommandBuffer, SharedCommandPool> _submittedSharedCommandPools
            = new Dictionary<VkCommandBuffer, SharedCommandPool>();

        public override GraphicsBackend BackendType => GraphicsBackend.Vulkan;

        public override bool IsUvOriginTopLeft => true;

        public override Swapchain MainSwapchain => _mainSwapchain;

        public override GraphicsDeviceFeatures Features { get; }

        public VkInstance Instance => _instance;
        public VkDevice Device => _device;
        public VkPhysicalDevice PhysicalDevice => _physicalDevice;
        public VkPhysicalDeviceMemoryProperties PhysicalDeviceMemProperties => _physicalDeviceMemProperties;
        public VkQueue GraphicsQueue => _graphicsQueue;
        public uint GraphicsQueueIndex => _graphicsQueueIndex;
        public uint PresentQueueIndex => _presentQueueIndex;
        public VkDeviceMemoryManager MemoryManager => _memoryManager;
        public VkDescriptorPoolManager DescriptorPoolManager => _descriptorPoolManager;

        private readonly ConditionalLock _submittedFencesLock = new ConditionalLock();
        private readonly Queue<Vulkan.VkFence> _availableSubmissionFences = new Queue<Vulkan.VkFence>();
        private readonly Dictionary<Vulkan.VkFence, (VkCommandList, VkCommandBuffer)> _submittedFences
            = new Dictionary<Vulkan.VkFence, (VkCommandList, VkCommandBuffer)>();
        private readonly List<KeyValuePair<Vulkan.VkFence, (VkCommandList, VkCommandBuffer)>> _completedFences
            = new List<KeyValuePair<Vulkan.VkFence, (VkCommandList, VkCommandBuffer)>>();
        private readonly VkSwapchain _mainSwapchain;

        public VkGraphicsDevice(GraphicsDeviceOptions options, SwapchainDescription? scDesc)
            : base(options)
        {
            _multiThreaded = !options.SingleThreaded;
            CreateInstance(options.Debug);

            VkSurfaceKHR surface = VkSurfaceKHR.Null;
            if (scDesc != null)
            {
                surface = VkSurfaceUtil.CreateSurface(_instance, scDesc.Value.Source);
            }

            CreatePhysicalDevice();
            CreateLogicalDevice(surface);

            _memoryManager = new VkDeviceMemoryManager(_device, _physicalDevice, _multiThreaded);

            Features = new GraphicsDeviceFeatures(
                computeShader: true,
                geometryShader: _physicalDeviceFeatures.geometryShader,
                tessellationShaders: _physicalDeviceFeatures.tessellationShader,
                multipleViewports: _physicalDeviceFeatures.multiViewport,
                samplerLodBias: true,
                drawBaseVertex: true,
                drawBaseInstance: true,
                drawIndirect: true,
                drawIndirectBaseInstance: _physicalDeviceFeatures.drawIndirectFirstInstance,
                fillModeWireframe: _physicalDeviceFeatures.fillModeNonSolid,
                samplerAnisotropy: _physicalDeviceFeatures.samplerAnisotropy,
                depthClipDisable: _physicalDeviceFeatures.depthClamp,
                texture1D: true,
                independentBlend: _physicalDeviceFeatures.independentBlend);

            ResourceFactory = new VkResourceFactory(this);

            if (scDesc != null)
            {
                SwapchainDescription desc = scDesc.Value;
                _mainSwapchain = new VkSwapchain(this, ref desc, surface);
            }

            CreateDescriptorPool();
            CreateGraphicsCommandPool();
            for (int i = 0; i < SharedCommandPoolCount; i++)
            {
                _sharedGraphicsCommandPools.Push(new SharedCommandPool(this, true));
            }

            if (options.SingleThreaded)
            {
                _immediateCL = ResourceFactory.CreateCommandList();
            }

            PostDeviceCreated();
        }

        public override ResourceFactory ResourceFactory { get; }

        protected override CommandList GetImmediateCommandListCore() => _immediateCL;

        protected override void SubmitCommandsCore(
            CommandList cl,
            Fence fence)
        {
            SubmitCommandList(cl, 0, null, 0, null, fence);
        }

        private void SubmitCommandList(
            CommandList cl,
            uint waitSemaphoreCount,
            VkSemaphore* waitSemaphoresPtr,
            uint signalSemaphoreCount,
            VkSemaphore* signalSemaphoresPtr,
            Fence fence)
        {
            VkCommandList vkCL = Util.AssertSubtype<CommandList, VkCommandList>(cl);
            VkCommandBuffer vkCB = vkCL.CommandBuffer;

            SubmitCommandBuffer(vkCL, vkCB, waitSemaphoreCount, waitSemaphoresPtr, signalSemaphoreCount, signalSemaphoresPtr, fence);
            vkCL.CommandBufferSubmitted();
        }

        private void SubmitCommandBuffer(
            VkCommandList vkCL,
            VkCommandBuffer vkCB,
            uint waitSemaphoreCount,
            VkSemaphore* waitSemaphoresPtr,
            uint signalSemaphoreCount,
            VkSemaphore* signalSemaphoresPtr,
            Fence fence)
        {
            CheckSubmittedFences();

            bool useExtraFence = fence != null;
            VkSubmitInfo si = VkSubmitInfo.New();
            si.commandBufferCount = 1;
            si.pCommandBuffers = &vkCB;
            VkPipelineStageFlags waitDstStageMask = VkPipelineStageFlags.ColorAttachmentOutput;
            si.pWaitDstStageMask = &waitDstStageMask;

            si.pWaitSemaphores = waitSemaphoresPtr;
            si.waitSemaphoreCount = waitSemaphoreCount;
            si.pSignalSemaphores = signalSemaphoresPtr;
            si.signalSemaphoreCount = signalSemaphoreCount;

            Vulkan.VkFence vkFence = Vulkan.VkFence.Null;
            Vulkan.VkFence submissionFence = Vulkan.VkFence.Null;
            if (useExtraFence)
            {
                vkFence = Util.AssertSubtype<Fence, VkFence>(fence).DeviceFence;
                submissionFence = GetFreeSubmissionFence();
            }
            else
            {
                vkFence = GetFreeSubmissionFence();
                submissionFence = vkFence;
            }

            using (_graphicsQueueLock.Lock(_multiThreaded))
            {
                vkQueueSubmit(_graphicsQueue, 1, ref si, vkFence);
                _submittedFences.Add(submissionFence, (vkCL, vkCB));

                if (useExtraFence)
                {
                    vkQueueSubmit(_graphicsQueue, 0, null, submissionFence);
                }
            }
        }

        private void CheckSubmittedFences()
        {
            using (_submittedFencesLock.Lock(_multiThreaded))
            {
                Debug.Assert(_completedFences.Count == 0);
                foreach (KeyValuePair<Vulkan.VkFence, (VkCommandList, VkCommandBuffer)> kvp in _submittedFences)
                {
                    if (vkGetFenceStatus(_device, kvp.Key) == VkResult.Success)
                    {
                        _completedFences.Add(kvp);
                    }
                }

                foreach (KeyValuePair<Vulkan.VkFence, (VkCommandList, VkCommandBuffer)> kvp in _completedFences)
                {
                    Vulkan.VkFence fence = kvp.Key;
                    VkCommandBuffer completedCB = kvp.Value.Item2;
                    kvp.Value.Item1?.CommandBufferCompleted(completedCB);
                    bool result = _submittedFences.Remove(fence);
                    Debug.Assert(result);
                    VkResult resetResult = vkResetFences(_device, 1, ref fence);
                    CheckResult(resetResult);
                    _availableSubmissionFences.Enqueue(fence);
                    using (_stagingResourcesLock.Lock(_multiThreaded))
                    {
                        if (_submittedStagingTextures.TryGetValue(completedCB, out VkTexture stagingTex))
                        {
                            _submittedStagingTextures.Remove(completedCB);
                            _availableStagingTextures.Add(stagingTex);
                        }
                        if (_submittedStagingBuffers.TryGetValue(completedCB, out VkBuffer stagingBuffer))
                        {
                            _submittedStagingBuffers.Remove(completedCB);
                            if (stagingBuffer.SizeInBytes <= MaxStagingBufferSize)
                            {
                                _availableStagingBuffers.Add(stagingBuffer);
                            }
                            else
                            {
                                stagingBuffer.Dispose();
                            }
                        }
                        if (_submittedSharedCommandPools.TryGetValue(completedCB, out SharedCommandPool sharedPool))
                        {
                            _submittedSharedCommandPools.Remove(completedCB);
                            using (_graphicsCommandPoolLock.Lock(_multiThreaded))
                            {
                                if (sharedPool.IsCached)
                                {
                                    _sharedGraphicsCommandPools.Push(sharedPool);
                                }
                                else
                                {
                                    sharedPool.Destroy();
                                }
                            }
                        }
                    }


                    VkCommandList cl = kvp.Value.Item1;
                    if (cl != null)
                    {
                        using (_commandListsToDisposeLock.Lock(_multiThreaded))
                        {
                            if (cl.SubmittedCommandBufferCount == 0)
                            {
                                if (_commandListsToDispose.Remove(cl))
                                {
                                    cl.DestroyCommandPool();
                                }
                            }
                        }
                    }
                }

                _completedFences.Clear();
            }
        }

        private Vulkan.VkFence GetFreeSubmissionFence()
        {
            if (_availableSubmissionFences.Count > 0)
            {
                return _availableSubmissionFences.Dequeue();
            }
            else
            {
                VkFenceCreateInfo fenceCI = VkFenceCreateInfo.New();
                VkResult result = vkCreateFence(_device, ref fenceCI, null, out Vulkan.VkFence ret);
                CheckResult(result);
                return ret;
            }
        }

        public void EnqueueDisposedCommandBuffer(VkCommandList vkCL)
        {
            using (_commandListsToDisposeLock.Lock(_multiThreaded))
            {
                _commandListsToDispose.Add(vkCL);
            }
        }

        protected override void SwapBuffersCore(Swapchain swapchain)
        {
            VkSwapchain vkSC = Util.AssertSubtype<Swapchain, VkSwapchain>(swapchain);

            VkPresentInfoKHR presentInfo = VkPresentInfoKHR.New();
            VkSwapchainKHR deviceSwapchain = vkSC.DeviceSwapchain;
            presentInfo.swapchainCount = 1;
            presentInfo.pSwapchains = &deviceSwapchain;
            uint imageIndex = vkSC.ImageIndex;
            presentInfo.pImageIndices = &imageIndex;

            ConditionalLock presentLock = vkSC.PresentQueueIndex == _graphicsQueueIndex ? _graphicsQueueLock : vkSC.Lock;
            using (presentLock.Lock(_multiThreaded))
            {
                vkQueuePresentKHR(vkSC.PresentQueue, ref presentInfo);
                if (vkSC.AcquireNextImage(_device, VkSemaphore.Null, vkSC.ImageAvailableFence))
                {
                    Vulkan.VkFence fence = vkSC.ImageAvailableFence;
                    vkWaitForFences(_device, 1, ref fence, true, ulong.MaxValue);
                    vkResetFences(_device, 1, ref fence);
                }
            }
        }

        internal void SetResourceName(DeviceResource resource, string name)
        {
            if (_debugMarkerEnabled)
            {
                switch (resource)
                {
                    case VkBuffer buffer:
                        SetDebugMarkerName(VkDebugReportObjectTypeEXT.BufferEXT, buffer.DeviceBuffer.Handle, name);
                        break;
                    case VkCommandList commandList:
                        SetDebugMarkerName(
                            VkDebugReportObjectTypeEXT.CommandBufferEXT,
                            (ulong)commandList.CommandBuffer.Handle,
                            string.Format("{0}_CommandBuffer", name));
                        SetDebugMarkerName(
                            VkDebugReportObjectTypeEXT.CommandPoolEXT,
                            commandList.CommandPool.Handle,
                            string.Format("{0}_CommandPool", name));
                        break;
                    case VkFramebuffer framebuffer:
                        SetDebugMarkerName(
                            VkDebugReportObjectTypeEXT.FramebufferEXT,
                            framebuffer.CurrentFramebuffer.Handle,
                            name);
                        break;
                    case VkPipeline pipeline:
                        SetDebugMarkerName(VkDebugReportObjectTypeEXT.PipelineEXT, pipeline.DevicePipeline.Handle, name);
                        SetDebugMarkerName(VkDebugReportObjectTypeEXT.PipelineLayoutEXT, pipeline.PipelineLayout.Handle, name);
                        break;
                    case VkResourceLayout resourceLayout:
                        SetDebugMarkerName(
                            VkDebugReportObjectTypeEXT.DescriptorSetLayoutEXT,
                            resourceLayout.DescriptorSetLayout.Handle,
                            name);
                        break;
                    case VkResourceSet resourceSet:
                        SetDebugMarkerName(VkDebugReportObjectTypeEXT.DescriptorSetEXT, resourceSet.DescriptorSet.Handle, name);
                        break;
                    case VkSampler sampler:
                        SetDebugMarkerName(VkDebugReportObjectTypeEXT.SamplerEXT, sampler.DeviceSampler.Handle, name);
                        break;
                    case VkShader shader:
                        SetDebugMarkerName(VkDebugReportObjectTypeEXT.ShaderModuleEXT, shader.ShaderModule.Handle, name);
                        break;
                    case VkTexture tex:
                        SetDebugMarkerName(VkDebugReportObjectTypeEXT.ImageEXT, tex.OptimalDeviceImage.Handle, name);
                        break;
                    case VkTextureView texView:
                        SetDebugMarkerName(VkDebugReportObjectTypeEXT.ImageViewEXT, texView.ImageView.Handle, name);
                        break;
                    case VkFence fence:
                        SetDebugMarkerName(VkDebugReportObjectTypeEXT.FenceEXT, fence.DeviceFence.Handle, name);
                        break;
                    case VkSwapchain sc:
                        SetDebugMarkerName(VkDebugReportObjectTypeEXT.SwapchainKHREXT, sc.DeviceSwapchain.Handle, name);
                        break;
                    default:
                        break;
                }
            }
        }

        private void SetDebugMarkerName(VkDebugReportObjectTypeEXT type, ulong target, string name)
        {
            Debug.Assert(_setObjectNameDelegate != null);

            VkDebugMarkerObjectNameInfoEXT nameInfo = VkDebugMarkerObjectNameInfoEXT.New();
            nameInfo.objectType = type;
            nameInfo.@object = target;

            int byteCount = Encoding.UTF8.GetByteCount(name);
            byte* utf8Ptr = stackalloc byte[byteCount];
            fixed (char* namePtr = name)
            {
                Encoding.UTF8.GetBytes(namePtr, name.Length, utf8Ptr, byteCount);
                nameInfo.pObjectName = utf8Ptr;
                VkResult result = _setObjectNameDelegate(_device, &nameInfo);
                CheckResult(result);
            }
        }

        private void FlushQueuedDisposables()
        {
            using (_commandListsToDisposeLock.Lock(_multiThreaded))
            {
                foreach (VkCommandList vkCB in _commandListsToDispose)
                {
                    vkCB.DestroyCommandPool();
                }

                _commandListsToDispose.Clear();
            }
        }

        private void CreateInstance(bool debug)
        {
            HashSet<string> availableInstanceLayers = new HashSet<string>(EnumerateInstanceLayers());
            HashSet<string> availableInstanceExtensions = new HashSet<string>(GetInstanceExtensions());

            VkInstanceCreateInfo instanceCI = VkInstanceCreateInfo.New();
            VkApplicationInfo applicationInfo = new VkApplicationInfo();
            applicationInfo.apiVersion = new VkVersion(1, 0, 0);
            applicationInfo.applicationVersion = new VkVersion(1, 0, 0);
            applicationInfo.engineVersion = new VkVersion(1, 0, 0);
            applicationInfo.pApplicationName = s_name;
            applicationInfo.pEngineName = s_name;

            instanceCI.pApplicationInfo = &applicationInfo;

            StackList<IntPtr, Size64Bytes> instanceExtensions = new StackList<IntPtr, Size64Bytes>();
            StackList<IntPtr, Size64Bytes> instanceLayers = new StackList<IntPtr, Size64Bytes>();

            if (!availableInstanceExtensions.Contains(CommonStrings.VK_KHR_SURFACE_EXTENSION_NAME))
            {
                throw new VeldridException($"The required instance extension was not available: {CommonStrings.VK_KHR_SURFACE_EXTENSION_NAME}");
            }
            instanceExtensions.Add(CommonStrings.VK_KHR_SURFACE_EXTENSION_NAME);

            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            if (isWindows)
            {
                if (!availableInstanceExtensions.Contains(CommonStrings.VK_KHR_WIN32_SURFACE_EXTENSION_NAME))
                {
                    throw new VeldridException($"The required instance extension was not available: {CommonStrings.VK_KHR_WIN32_SURFACE_EXTENSION_NAME}");
                }

                _platformSurfaceExtension = CommonStrings.VK_KHR_WIN32_SURFACE_EXTENSION_NAME;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (RuntimeInformation.OSDescription.Contains("Unix")) // Android
                {
                    if (!availableInstanceExtensions.Contains(CommonStrings.VK_KHR_ANDROID_SURFACE_EXTENSION_NAME))
                    {
                        throw new VeldridException($"The required instance extension was not available: {CommonStrings.VK_KHR_ANDROID_SURFACE_EXTENSION_NAME}");
                    }

                    _platformSurfaceExtension = CommonStrings.VK_KHR_ANDROID_SURFACE_EXTENSION_NAME;
                }
                else // Desktop Linux
                {
                    if (!availableInstanceExtensions.Contains(CommonStrings.VK_KHR_XLIB_SURFACE_EXTENSION_NAME))
                    {
                        throw new VeldridException($"The required instance extension was not available: {CommonStrings.VK_KHR_XLIB_SURFACE_EXTENSION_NAME}");
                    }

                    _platformSurfaceExtension = CommonStrings.VK_KHR_XLIB_SURFACE_EXTENSION_NAME;
                }
            }
            else
            {
                throw new NotSupportedException("This platform does not support Vulkan.");
            }

            instanceExtensions.Add(_platformSurfaceExtension);

            bool debugReportExtensionAvailable = false;
            if (debug)
            {
                if (availableInstanceExtensions.Contains(CommonStrings.VK_EXT_DEBUG_REPORT_EXTENSION_NAME))
                {
                    debugReportExtensionAvailable = true;
                    instanceExtensions.Add(CommonStrings.VK_EXT_DEBUG_REPORT_EXTENSION_NAME);
                }
                if (availableInstanceLayers.Contains(CommonStrings.StandardValidationLayerName))
                {
                    _standardValidationSupported = true;
                    instanceLayers.Add(CommonStrings.StandardValidationLayerName);
                }
            }

            instanceCI.enabledExtensionCount = instanceExtensions.Count;
            instanceCI.ppEnabledExtensionNames = (byte**)instanceExtensions.Data;

            instanceCI.enabledLayerCount = instanceLayers.Count;
            if (instanceLayers.Count > 0)
            {
                instanceCI.ppEnabledLayerNames = (byte**)instanceLayers.Data;
            }

            VkResult result = vkCreateInstance(ref instanceCI, null, out _instance);
            CheckResult(result);

            if (debug && debugReportExtensionAvailable)
            {
                EnableDebugCallback();
            }
        }

        public void EnableDebugCallback(VkDebugReportFlagsEXT flags = VkDebugReportFlagsEXT.WarningEXT | VkDebugReportFlagsEXT.ErrorEXT)
        {
            Debug.WriteLine("Enabling Vulkan Debug callbacks.");
            _debugCallbackFunc = DebugCallback;
            IntPtr debugFunctionPtr = Marshal.GetFunctionPointerForDelegate(_debugCallbackFunc);
            VkDebugReportCallbackCreateInfoEXT debugCallbackCI = VkDebugReportCallbackCreateInfoEXT.New();
            debugCallbackCI.flags = flags;
            debugCallbackCI.pfnCallback = debugFunctionPtr;
            IntPtr createFnPtr;
            using (FixedUtf8String debugExtFnName = "vkCreateDebugReportCallbackEXT")
            {
                createFnPtr = vkGetInstanceProcAddr(_instance, debugExtFnName);
            }
            if (createFnPtr == IntPtr.Zero)
            {
                return;
            }

            vkCreateDebugReportCallbackEXT_d createDelegate = Marshal.GetDelegateForFunctionPointer<vkCreateDebugReportCallbackEXT_d>(createFnPtr);
            VkResult result = createDelegate(_instance, &debugCallbackCI, IntPtr.Zero, out _debugCallbackHandle);
            CheckResult(result);
        }

        private uint DebugCallback(
            uint flags,
            VkDebugReportObjectTypeEXT objectType,
            ulong @object,
            UIntPtr location,
            int messageCode,
            byte* pLayerPrefix,
            byte* pMessage,
            void* pUserData)
        {
            string message = Util.GetString(pMessage);
            VkDebugReportFlagsEXT debugReportFlags = (VkDebugReportFlagsEXT)flags;

#if DEBUG
            if (Debugger.IsAttached)
            {
                Debugger.Break();
            }
#endif

            string fullMessage = $"[{debugReportFlags}] ({objectType}) {message}";

            if (debugReportFlags == VkDebugReportFlagsEXT.ErrorEXT)
            {
                throw new VeldridException("A Vulkan validation error was encountered: " + fullMessage);
            }

            Console.WriteLine(fullMessage);
            return 0;
        }

        private void CreatePhysicalDevice()
        {
            uint deviceCount = 0;
            vkEnumeratePhysicalDevices(_instance, ref deviceCount, null);
            if (deviceCount == 0)
            {
                throw new InvalidOperationException("No physical devices exist.");
            }

            VkPhysicalDevice[] physicalDevices = new VkPhysicalDevice[deviceCount];
            vkEnumeratePhysicalDevices(_instance, ref deviceCount, ref physicalDevices[0]);
            // Just use the first one.
            _physicalDevice = physicalDevices[0];

            vkGetPhysicalDeviceProperties(_physicalDevice, out _physicalDeviceProperties);
            string deviceName;
            fixed (byte* utf8NamePtr = _physicalDeviceProperties.deviceName)
            {
                deviceName = Encoding.UTF8.GetString(utf8NamePtr, (int)MaxPhysicalDeviceNameSize);
            }

            vkGetPhysicalDeviceFeatures(_physicalDevice, out _physicalDeviceFeatures);

            vkGetPhysicalDeviceMemoryProperties(_physicalDevice, out _physicalDeviceMemProperties);
        }

        private void CreateLogicalDevice(VkSurfaceKHR surface)
        {
            GetQueueFamilyIndices(surface);

            HashSet<uint> familyIndices = new HashSet<uint> { _graphicsQueueIndex, _presentQueueIndex };
            VkDeviceQueueCreateInfo* queueCreateInfos = stackalloc VkDeviceQueueCreateInfo[familyIndices.Count];
            uint queueCreateInfosCount = (uint)familyIndices.Count;

            int i = 0;
            foreach (uint index in familyIndices)
            {
                VkDeviceQueueCreateInfo queueCreateInfo = VkDeviceQueueCreateInfo.New();
                queueCreateInfo.queueFamilyIndex = _graphicsQueueIndex;
                queueCreateInfo.queueCount = 1;
                float priority = 1f;
                queueCreateInfo.pQueuePriorities = &priority;
                queueCreateInfos[i] = queueCreateInfo;
                i += 1;
            }

            VkPhysicalDeviceFeatures deviceFeatures = new VkPhysicalDeviceFeatures();
            deviceFeatures.samplerAnisotropy = _physicalDeviceFeatures.samplerAnisotropy;
            deviceFeatures.fillModeNonSolid = _physicalDeviceFeatures.fillModeNonSolid;
            deviceFeatures.geometryShader = _physicalDeviceFeatures.geometryShader;
            deviceFeatures.depthClamp = _physicalDeviceFeatures.depthClamp;
            deviceFeatures.multiViewport = _physicalDeviceFeatures.multiViewport;
            deviceFeatures.textureCompressionBC = _physicalDeviceFeatures.textureCompressionBC;
            deviceFeatures.textureCompressionETC2 = _physicalDeviceFeatures.textureCompressionETC2;
            deviceFeatures.multiDrawIndirect = _physicalDeviceFeatures.multiDrawIndirect;
            deviceFeatures.drawIndirectFirstInstance = _physicalDeviceFeatures.drawIndirectFirstInstance;

            uint propertyCount = 0;
            VkResult result = vkEnumerateDeviceExtensionProperties(_physicalDevice, (byte*)null, &propertyCount, null);
            CheckResult(result);
            VkExtensionProperties* properties = stackalloc VkExtensionProperties[(int)propertyCount];
            result = vkEnumerateDeviceExtensionProperties(_physicalDevice, (byte*)null, &propertyCount, properties);
            CheckResult(result);

            StackList<IntPtr> extensionNames = new StackList<IntPtr>();
            for (int property = 0; property < propertyCount; property++)
            {
                string extensionName = Util.GetString(properties[property].extensionName);
                if (extensionName == "VK_EXT_debug_marker")
                {
                    extensionNames.Add(CommonStrings.VK_EXT_DEBUG_MARKER_EXTENSION_NAME);
                    _debugMarkerEnabled = true;
                }
                else if (extensionName == "VK_KHR_swapchain")
                {
                    extensionNames.Add((IntPtr)properties[property].extensionName);
                }
            }

            VkDeviceCreateInfo deviceCreateInfo = VkDeviceCreateInfo.New();
            deviceCreateInfo.queueCreateInfoCount = queueCreateInfosCount;
            deviceCreateInfo.pQueueCreateInfos = queueCreateInfos;

            deviceCreateInfo.pEnabledFeatures = &deviceFeatures;

            StackList<IntPtr> layerNames = new StackList<IntPtr>();
            if (_standardValidationSupported)
            {
                layerNames.Add(CommonStrings.StandardValidationLayerName);
            }
            deviceCreateInfo.enabledLayerCount = layerNames.Count;
            deviceCreateInfo.ppEnabledLayerNames = (byte**)layerNames.Data;

            deviceCreateInfo.enabledExtensionCount = extensionNames.Count;
            deviceCreateInfo.ppEnabledExtensionNames = (byte**)extensionNames.Data;

            result = vkCreateDevice(_physicalDevice, ref deviceCreateInfo, null, out _device);
            CheckResult(result);

            vkGetDeviceQueue(_device, _graphicsQueueIndex, 0, out _graphicsQueue);

            if (_debugMarkerEnabled)
            {
                IntPtr setObjectNamePtr;
                using (FixedUtf8String debugExtFnName = "vkDebugMarkerSetObjectNameEXT")
                {
                    setObjectNamePtr = vkGetInstanceProcAddr(_instance, debugExtFnName);
                }

                _setObjectNameDelegate = Marshal.GetDelegateForFunctionPointer<vkDebugMarkerSetObjectNameEXT_d>(setObjectNamePtr);
            }
        }

        private void GetQueueFamilyIndices(VkSurfaceKHR surface)
        {
            uint queueFamilyCount = 0;
            vkGetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref queueFamilyCount, null);
            VkQueueFamilyProperties[] qfp = new VkQueueFamilyProperties[queueFamilyCount];
            vkGetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref queueFamilyCount, out qfp[0]);

            bool foundGraphics = false;
            bool foundPresent = surface == VkSurfaceKHR.Null;

            for (uint i = 0; i < qfp.Length; i++)
            {
                if ((qfp[i].queueFlags & VkQueueFlags.Graphics) != 0)
                {
                    _graphicsQueueIndex = i;
                }

                if (!foundPresent)
                {
                    vkGetPhysicalDeviceSurfaceSupportKHR(_physicalDevice, i, surface, out VkBool32 presentSupported);
                    if (presentSupported)
                    {
                        _presentQueueIndex = i;
                        foundPresent = true;
                    }
                }

                if (foundGraphics && foundPresent)
                {
                    return;
                }
            }
        }

        private void CreateDescriptorPool()
        {
            _descriptorPoolManager = new VkDescriptorPoolManager(this);
        }

        private void CreateGraphicsCommandPool()
        {
            VkCommandPoolCreateInfo commandPoolCI = VkCommandPoolCreateInfo.New();
            commandPoolCI.flags = VkCommandPoolCreateFlags.ResetCommandBuffer;
            commandPoolCI.queueFamilyIndex = _graphicsQueueIndex;
            VkResult result = vkCreateCommandPool(_device, ref commandPoolCI, null, out _graphicsCommandPool);
            CheckResult(result);
        }

        protected override MappedResource MapCore(MappableResource resource, MapMode mode, uint subresource)
        {
            VkMemoryBlock memoryBlock = default(VkMemoryBlock);
            IntPtr mappedPtr = IntPtr.Zero;
            uint sizeInBytes;
            uint offset = 0;
            uint rowPitch = 0;
            uint depthPitch = 0;
            if (resource is VkBuffer buffer)
            {
                memoryBlock = buffer.Memory;
                sizeInBytes = buffer.SizeInBytes;
            }
            else
            {
                VkTexture texture = Util.AssertSubtype<MappableResource, VkTexture>(resource);
                VkSubresourceLayout layout = texture.GetSubresourceLayout(subresource);
                memoryBlock = texture.Memory;
                sizeInBytes = (uint)layout.size;
                offset = (uint)layout.offset;
                rowPitch = (uint)layout.rowPitch;
                depthPitch = (uint)layout.depthPitch;
            }

            if (memoryBlock.DeviceMemory.Handle != 0)
            {
                if (memoryBlock.IsPersistentMapped)
                {
                    mappedPtr = (IntPtr)memoryBlock.BlockMappedPointer;
                }
                else
                {
                    mappedPtr = _memoryManager.Map(memoryBlock);
                }
            }

            byte* dataPtr = (byte*)mappedPtr.ToPointer() + offset;
            return new MappedResource(
                resource,
                mode,
                (IntPtr)dataPtr,
                sizeInBytes,
                subresource,
                rowPitch,
                depthPitch);
        }

        protected override void UnmapCore(MappableResource resource, uint subresource)
        {
            VkMemoryBlock memoryBlock = default(VkMemoryBlock);
            if (resource is VkBuffer buffer)
            {
                memoryBlock = buffer.Memory;
            }
            else
            {
                VkTexture tex = Util.AssertSubtype<MappableResource, VkTexture>(resource);
                memoryBlock = tex.Memory;
            }

            if (memoryBlock.DeviceMemory.Handle != 0 && !memoryBlock.IsPersistentMapped)
            {
                vkUnmapMemory(_device, memoryBlock.DeviceMemory);
            }
        }

        protected override void PlatformDispose()
        {
            Debug.Assert(_submittedFences.Count == 0);
            foreach (Vulkan.VkFence fence in _availableSubmissionFences)
            {
                vkDestroyFence(_device, fence, null);
            }

            _mainSwapchain?.Dispose();
            if (_debugCallbackFunc != null)
            {
                _debugCallbackFunc = null;
                FixedUtf8String debugExtFnName = "vkDestroyDebugReportCallbackEXT";
                IntPtr destroyFuncPtr = vkGetInstanceProcAddr(_instance, debugExtFnName);
                vkDestroyDebugReportCallbackEXT_d destroyDel
                    = Marshal.GetDelegateForFunctionPointer<vkDestroyDebugReportCallbackEXT_d>(destroyFuncPtr);
                destroyDel(_instance, _debugCallbackHandle, null);
            }

            _descriptorPoolManager.DestroyAll();
            vkDestroyCommandPool(_device, _graphicsCommandPool, null);

            Debug.Assert(_submittedStagingTextures.Count == 0);
            foreach (VkTexture tex in _availableStagingTextures)
            {
                tex.Dispose();
            }

            Debug.Assert(_submittedStagingBuffers.Count == 0);
            foreach (VkBuffer buffer in _availableStagingBuffers)
            {
                buffer.Dispose();
            }

            while (_sharedGraphicsCommandPools.TryPop(out SharedCommandPool sharedPool))
            {
                sharedPool.Destroy();
            }

            ImmediateCommandList?.Dispose();

            _memoryManager.Dispose();

            VkResult result = vkDeviceWaitIdle(_device);
            CheckResult(result);
            vkDestroyDevice(_device, null);
            vkDestroyInstance(_instance, null);
        }

        protected override void WaitForIdleCore()
        {
            using (_graphicsQueueLock.Lock(_multiThreaded))
            {
                vkQueueWaitIdle(_graphicsQueue);
            }

            CheckSubmittedFences();
            FlushQueuedDisposables();
        }

        public override TextureSampleCount GetSampleCountLimit(PixelFormat format, bool depthFormat)
        {
            VkImageUsageFlags usageFlags = VkImageUsageFlags.Sampled;
            usageFlags |= depthFormat ? VkImageUsageFlags.DepthStencilAttachment : VkImageUsageFlags.ColorAttachment;

            vkGetPhysicalDeviceImageFormatProperties(
                _physicalDevice,
                VkFormats.VdToVkPixelFormat(format),
                VkImageType.Image2D,
                VkImageTiling.Optimal,
                usageFlags,
                VkImageCreateFlags.None,
                out VkImageFormatProperties formatProperties);

            VkSampleCountFlags vkSampleCounts = formatProperties.sampleCounts;
            if ((vkSampleCounts & VkSampleCountFlags.Count32) == VkSampleCountFlags.Count32)
            {
                return TextureSampleCount.Count32;
            }
            else if ((vkSampleCounts & VkSampleCountFlags.Count16) == VkSampleCountFlags.Count16)
            {
                return TextureSampleCount.Count16;
            }
            else if ((vkSampleCounts & VkSampleCountFlags.Count8) == VkSampleCountFlags.Count8)
            {
                return TextureSampleCount.Count8;
            }
            else if ((vkSampleCounts & VkSampleCountFlags.Count4) == VkSampleCountFlags.Count4)
            {
                return TextureSampleCount.Count4;
            }
            else if ((vkSampleCounts & VkSampleCountFlags.Count2) == VkSampleCountFlags.Count2)
            {
                return TextureSampleCount.Count2;
            }

            return TextureSampleCount.Count1;
        }

        protected override bool GetPixelFormatSupportCore(
            PixelFormat format,
            TextureType type,
            TextureUsage usage,
            out PixelFormatProperties properties)
        {
            VkFormat vkFormat = VkFormats.VdToVkPixelFormat(format, (usage & TextureUsage.DepthStencil) != 0);
            VkImageType vkType = VkFormats.VdToVkTextureType(type);
            VkImageTiling tiling = usage == TextureUsage.Staging ? VkImageTiling.Linear : VkImageTiling.Optimal;
            VkImageUsageFlags vkUsage = VkFormats.VdToVkTextureUsage(usage);

            VkResult result = vkGetPhysicalDeviceImageFormatProperties(
                _physicalDevice,
                vkFormat,
                vkType,
                tiling,
                vkUsage,
                VkImageCreateFlags.None,
                out VkImageFormatProperties vkProps);

            if (result == VkResult.ErrorFormatNotSupported)
            {
                properties = default(PixelFormatProperties);
                return false;
            }
            CheckResult(result);

            properties = new PixelFormatProperties(
               vkProps.maxExtent.width,
               vkProps.maxExtent.height,
               vkProps.maxExtent.depth,
               vkProps.maxMipLevels,
               vkProps.maxArrayLayers,
               (uint)vkProps.sampleCounts);
            return true;
        }

        protected override void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
        {
            VkBuffer vkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(buffer);
            VkBuffer copySrcVkBuffer = null;
            IntPtr mappedPtr;
            byte* destPtr;
            bool isPersistentMapped = vkBuffer.Memory.IsPersistentMapped;
            if (isPersistentMapped)
            {
                mappedPtr = (IntPtr)vkBuffer.Memory.BlockMappedPointer;
                destPtr = (byte*)mappedPtr + bufferOffsetInBytes;
            }
            else
            {
                copySrcVkBuffer = GetFreeStagingBuffer(sizeInBytes);
                mappedPtr = (IntPtr)copySrcVkBuffer.Memory.BlockMappedPointer;
                destPtr = (byte*)mappedPtr;
            }

            Unsafe.CopyBlock(destPtr, source.ToPointer(), sizeInBytes);

            if (!isPersistentMapped)
            {
                SharedCommandPool pool = GetFreeCommandPool();
                VkCommandBuffer cb = pool.BeginNewCommandBuffer();

                VkBufferCopy copyRegion = new VkBufferCopy
                {
                    dstOffset = bufferOffsetInBytes,
                    size = sizeInBytes
                };
                vkCmdCopyBuffer(cb, copySrcVkBuffer.DeviceBuffer, vkBuffer.DeviceBuffer, 1, ref copyRegion);

                pool.EndAndSubmit(cb);
                using (_stagingResourcesLock.Lock(_multiThreaded))
                {
                    _submittedStagingBuffers.Add(cb, copySrcVkBuffer);
                }
            }
        }

        private SharedCommandPool GetFreeCommandPool()
        {
            if (!_sharedGraphicsCommandPools.TryPop(out SharedCommandPool sharedPool))
            {
                sharedPool = new SharedCommandPool(this, false);
            }

            return sharedPool;
        }

        private IntPtr MapBuffer(VkBuffer buffer, uint numBytes)
        {
            if (buffer.Memory.IsPersistentMapped)
            {
                return (IntPtr)buffer.Memory.BlockMappedPointer;
            }
            else
            {
                void* mappedPtr;
                VkResult result = vkMapMemory(Device, buffer.Memory.DeviceMemory, buffer.Memory.Offset, numBytes, 0, &mappedPtr);
                CheckResult(result);
                return (IntPtr)mappedPtr;
            }
        }

        private void UnmapBuffer(VkBuffer buffer)
        {
            if (!buffer.Memory.IsPersistentMapped)
            {
                vkUnmapMemory(Device, buffer.Memory.DeviceMemory);
            }
        }

        protected override void UpdateTextureCore(
            Texture texture,
            IntPtr source,
            uint sizeInBytes,
            uint x,
            uint y,
            uint z,
            uint width,
            uint height,
            uint depth,
            uint mipLevel,
            uint arrayLayer)
        {
            VkTexture vkTex = Util.AssertSubtype<Texture, VkTexture>(texture);
            bool isStaging = (vkTex.Usage & TextureUsage.Staging) != 0;
            if (isStaging)
            {
                VkMemoryBlock memBlock = vkTex.Memory;
                uint subresource = texture.CalculateSubresource(mipLevel, arrayLayer);
                VkSubresourceLayout layout = vkTex.GetSubresourceLayout(subresource);
                byte* imageBasePtr = (byte*)memBlock.BlockMappedPointer + layout.offset;

                uint srcRowPitch = FormatHelpers.GetRowPitch(width, texture.Format);
                uint srcDepthPitch = FormatHelpers.GetDepthPitch(srcRowPitch, height, texture.Format);
                Util.CopyTextureRegion(
                    source.ToPointer(),
                    0, 0, 0,
                    srcRowPitch, srcDepthPitch,
                    imageBasePtr,
                    x, y, z,
                    (uint)layout.rowPitch, (uint)layout.depthPitch,
                    width, height, depth,
                    texture.Format);
            }
            else
            {
                VkTexture stagingTex = GetFreeStagingTexture(width, height, depth, texture.Format);
                UpdateTexture(stagingTex, source, sizeInBytes, 0, 0, 0, width, height, depth, 0, 0);
                SharedCommandPool pool = GetFreeCommandPool();
                VkCommandBuffer cb = pool.BeginNewCommandBuffer();
                VkCommandList.CopyTextureCore_VkCommandBuffer(
                    cb,
                    stagingTex, 0, 0, 0, 0, 0,
                    texture, x, y, z, mipLevel, arrayLayer,
                    width, height, depth, 1);
                pool.EndAndSubmit(cb);
                using (_stagingResourcesLock.Lock(_multiThreaded))
                {
                    _submittedStagingTextures.Add(cb, stagingTex);
                }
            }
        }

        private VkTexture GetFreeStagingTexture(uint width, uint height, uint depth, PixelFormat format)
        {
            uint pixelSize = FormatHelpers.GetSizeInBytes(format);
            uint totalSize = width * height * depth * pixelSize;
            using (_stagingResourcesLock.Lock(_multiThreaded))
            {
                for (int i = 0; i < _availableStagingTextures.Count; i++)
                {
                    VkTexture tex = _availableStagingTextures[i];
                    if (tex.Memory.Size >= totalSize)
                    {
                        _availableStagingTextures.RemoveAt(i);
                        tex.SetStagingDimensions(width, height, depth, format);
                        return tex;
                    }
                }
            }

            uint texWidth = Math.Max(256, width);
            uint texHeight = Math.Max(256, height);
            VkTexture newTex = (VkTexture)ResourceFactory.CreateTexture(TextureDescription.Texture3D(
                texWidth, texHeight, depth, 1, format, TextureUsage.Staging));
            newTex.SetStagingDimensions(width, height, depth, format);

            return newTex;
        }

        private VkBuffer GetFreeStagingBuffer(uint size)
        {
            using (_stagingResourcesLock.Lock(_multiThreaded))
            {
                for (int i = 0; i < _availableStagingBuffers.Count; i++)
                {
                    VkBuffer buffer = _availableStagingBuffers[i];
                    if (buffer.SizeInBytes >= size)
                    {
                        _availableStagingBuffers.RemoveAt(i);
                        return buffer;
                    }
                }
            }

            uint newBufferSize = Math.Max(MinStagingBufferSize, size);
            VkBuffer newBuffer = (VkBuffer)ResourceFactory.CreateBuffer(
                new BufferDescription(newBufferSize, BufferUsage.Staging));
            return newBuffer;
        }

        public override void ResetFence(Fence fence)
        {
            Vulkan.VkFence vkFence = Util.AssertSubtype<Fence, VkFence>(fence).DeviceFence;
            vkResetFences(_device, 1, ref vkFence);
        }

        public override bool WaitForFence(Fence fence, ulong nanosecondTimeout)
        {
            Vulkan.VkFence vkFence = Util.AssertSubtype<Fence, VkFence>(fence).DeviceFence;
            VkResult result = vkWaitForFences(_device, 1, ref vkFence, true, nanosecondTimeout);
            return result == VkResult.Success;
        }

        public override bool WaitForFences(Fence[] fences, bool waitAll, ulong nanosecondTimeout)
        {
            int fenceCount = fences.Length;
            Vulkan.VkFence* fencesPtr = stackalloc Vulkan.VkFence[fenceCount];
            for (int i = 0; i < fenceCount; i++)
            {
                fencesPtr[i] = Util.AssertSubtype<Fence, VkFence>(fences[i]).DeviceFence;
            }

            VkResult result = vkWaitForFences(_device, (uint)fenceCount, fencesPtr, waitAll, nanosecondTimeout);
            return result == VkResult.Success;
        }

        internal static bool IsSupported()
        {
            if (!IsVulkanLoaded())
            {
                return false;
            }

            HashSet<string> instanceExtensions = new HashSet<string>(GetInstanceExtensions());
            if (!instanceExtensions.Contains(CommonStrings.VK_KHR_SURFACE_EXTENSION_NAME))
            {
                return false;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return instanceExtensions.Contains(CommonStrings.VK_KHR_WIN32_SURFACE_EXTENSION_NAME);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (RuntimeInformation.OSDescription.Contains("Unix")) // Android
                {
                    return instanceExtensions.Contains(CommonStrings.VK_KHR_ANDROID_SURFACE_EXTENSION_NAME);
                }
                else
                {
                    return instanceExtensions.Contains(CommonStrings.VK_KHR_XLIB_SURFACE_EXTENSION_NAME);
                }
            }

            return false;
        }

        internal void ClearColorTexture(VkTexture texture, VkClearColorValue color)
        {
            VkImageSubresourceRange range = new VkImageSubresourceRange(
                 VkImageAspectFlags.Color,
                 0,
                 texture.MipLevels,
                 0,
                 texture.ArrayLayers);
            SharedCommandPool pool = GetFreeCommandPool();
            VkCommandBuffer cb = pool.BeginNewCommandBuffer();
            texture.TransitionImageLayout(cb, 0, texture.MipLevels, 0, texture.ArrayLayers, VkImageLayout.TransferDstOptimal);
            vkCmdClearColorImage(cb, texture.OptimalDeviceImage, VkImageLayout.TransferDstOptimal, &color, 1, &range);
            texture.TransitionImageLayout(cb, 0, texture.MipLevels, 0, texture.ArrayLayers, VkImageLayout.ColorAttachmentOptimal);
            pool.EndAndSubmit(cb);
        }

        internal void ClearDepthTexture(VkTexture texture, VkClearDepthStencilValue clearValue)
        {
            VkImageSubresourceRange range = new VkImageSubresourceRange(
                 VkImageAspectFlags.Depth | VkImageAspectFlags.Stencil,
                 0,
                 texture.MipLevels,
                 0,
                 texture.ArrayLayers);
            SharedCommandPool pool = GetFreeCommandPool();
            VkCommandBuffer cb = pool.BeginNewCommandBuffer();
            texture.TransitionImageLayout(cb, 0, texture.MipLevels, 0, texture.ArrayLayers, VkImageLayout.TransferDstOptimal);
            vkCmdClearDepthStencilImage(
                cb,
                texture.OptimalDeviceImage,
                VkImageLayout.TransferDstOptimal,
                &clearValue,
                1,
                &range);
            texture.TransitionImageLayout(cb, 0, texture.MipLevels, 0, texture.ArrayLayers, VkImageLayout.DepthStencilAttachmentOptimal);
            pool.EndAndSubmit(cb);
        }

        private class SharedCommandPool
        {
            private readonly VkGraphicsDevice _gd;
            private readonly VkCommandPool _pool;
            private readonly VkCommandBuffer _cb;

            public bool IsCached { get; }

            public SharedCommandPool(VkGraphicsDevice gd, bool isCached)
            {
                _gd = gd;
                IsCached = isCached;

                VkCommandPoolCreateInfo commandPoolCI = VkCommandPoolCreateInfo.New();
                commandPoolCI.flags = VkCommandPoolCreateFlags.Transient | VkCommandPoolCreateFlags.ResetCommandBuffer;
                commandPoolCI.queueFamilyIndex = _gd.GraphicsQueueIndex;
                VkResult result = vkCreateCommandPool(_gd.Device, ref commandPoolCI, null, out _pool);
                CheckResult(result);

                VkCommandBufferAllocateInfo allocateInfo = VkCommandBufferAllocateInfo.New();
                allocateInfo.commandBufferCount = 1;
                allocateInfo.level = VkCommandBufferLevel.Primary;
                allocateInfo.commandPool = _pool;
                result = vkAllocateCommandBuffers(_gd.Device, ref allocateInfo, out _cb);
                CheckResult(result);
            }

            public VkCommandBuffer BeginNewCommandBuffer()
            {
                VkCommandBufferBeginInfo beginInfo = VkCommandBufferBeginInfo.New();
                beginInfo.flags = VkCommandBufferUsageFlags.OneTimeSubmit;
                VkResult result = vkBeginCommandBuffer(_cb, ref beginInfo);
                CheckResult(result);

                return _cb;
            }

            public void EndAndSubmit(VkCommandBuffer cb)
            {
                VkResult result = vkEndCommandBuffer(cb);
                CheckResult(result);
                _gd.SubmitCommandBuffer(null, cb, 0, null, 0, null, null);
                using (_gd._stagingResourcesLock.Lock(_gd._multiThreaded))
                {
                    _gd._submittedSharedCommandPools.Add(cb, this);
                }
            }

            internal void Destroy()
            {
                vkDestroyCommandPool(_gd.Device, _pool, null);
            }
        }
    }

    internal unsafe delegate VkResult vkCreateDebugReportCallbackEXT_d(
        VkInstance instance,
        VkDebugReportCallbackCreateInfoEXT* createInfo,
        IntPtr allocatorPtr,
        out VkDebugReportCallbackEXT ret);

    internal unsafe delegate void vkDestroyDebugReportCallbackEXT_d(
        VkInstance instance,
        VkDebugReportCallbackEXT callback,
        VkAllocationCallbacks* pAllocator);

    internal unsafe delegate VkResult vkDebugMarkerSetObjectNameEXT_d(VkDevice device, VkDebugMarkerObjectNameInfoEXT* pNameInfo);
}
