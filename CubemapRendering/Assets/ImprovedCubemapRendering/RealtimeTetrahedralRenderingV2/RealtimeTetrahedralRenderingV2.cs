#if UNITY_EDITOR
using UnityEditor;
#endif

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Unity.Collections;
using static ImprovedCubemapRendering.RealtimeTetrahedralRenderingV2;

namespace ImprovedCubemapRendering
{
    public class RealtimeTetrahedralRenderingV2 : MonoBehaviour
    {
        public enum RealtimeCubemapTextureFormatType
        {
            /// <summary>
            /// [HDR] 128-bit's total (32 bits per channel).
            /// </summary>
            RGBAFloat,

            /// <summary>
            /// [HDR] 64-bit's total (16 bits per channel).
            /// </summary>
            RGBAHalf,

            /// <summary>
            /// [HDR] 32-bit's total (11 bits for Red/Green, 10 bits for blue)
            /// </summary>
            RGB111110,

            /// <summary>
            /// [NON-HDR] 32-bit's total (8 bits per channel)
            /// </summary>
            RGBA8
        }

        public enum UpdateType
        {
            /// <summary>
            /// Update Reflection Probe every frame.
            /// </summary>
            UpdateEveryFrame,

            /// <summary>
            /// Update Reflection Probe for a specified time interval.
            /// </summary>
            UpdateFPS,

            /// <summary>
            /// No updates (MANUAL)
            /// </summary>
            None
        }

        public enum SpecularConvolutionFilter
        {
            GGX,
            Gaussian
        }

        public struct MipLevel
        {
            public int mipLevelSquareResolution;
            public float roughnessLevel;
            public int computeShaderKernelThreadGroupSizeX;
            public int computeShaderKernelThreadGroupSizeY;
            public int computeShaderKernelThreadGroupSizeZ;
        }

        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||

        [Header("Setup")]
        public Texture2DArray cubemapToTetrahedralLUT;
        public ComputeShader tetrahedralRenderingComputeShader;

        [Header("(EDITOR ONLY / OFFLINE) LUT Generation")]
        [Range(1, 4)] public int lutSupersampling = 2;
        public ComputeShader tetrahedralLutComputeShader;

        [Header("Properties")]
        public RealtimeCubemapTextureFormatType formatType = RealtimeCubemapTextureFormatType.RGBAHalf;
        public UpdateType updateType = UpdateType.UpdateFPS;
        public int updateFPS = 30;

        [Header("Specular Convolution")]
        public SpecularConvolutionFilter specularConvolutionFilter = SpecularConvolutionFilter.GGX;
        public int GGXSamples = 256;
        public int GaussianSamples = 8;
        public float GaussianSampleOffsetMultiplier = 4.0f;

        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||

        private ReflectionProbe reflectionProbe;

        private GameObject probeCameraGameObject;
        private Camera probeCamera;

        //NOTE: it would be ideal to have to only need to deal with 3 render targets at minimum (camera render target, tetrahedron map, and cubemap)
        //however we can't write directly into a "cubemap" dimension render target, because there is no such thing as RWTextureCUBE.
        //not even a RWTexture2D array works with the "cubemap" dimension even though a cubemap is a Tex2DArray with 6 slices
        //unity complains that there is a mismatch of output texture dimension (expects 5, gets 4) so this is how we have to deal with it unfortunately
        private RenderTexture tetrahedronFaceRender;
        private RenderTexture tetrahedronMap;
        private RenderTexture intermediateCubemap;
        private RenderTexture finalCubemap;

        private static int renderTargetDepthBits = 32; //0 16 24 32

        private static readonly float TetrahedronFaceFovX = 143.98570868f;
        private static readonly float TetrahedronFaceFovY = 125.27438968f;
        private static readonly float TetrahedronFaceAspect = 1.1493626833688518353833739467634f; //TetrahedronFaceFovX / TetrahedronFaceFovY

        private Quaternion probeCameraRotationTetrahedronFace0;
        private Quaternion probeCameraRotationTetrahedronFace1;
        private Quaternion probeCameraRotationTetrahedronFace2;
        private Quaternion probeCameraRotationTetrahedronFace3;

        private int computeShaderTetrahedralFaceCombine;
        private int computeShaderTetrahedralFaceCombineX = 0;
        private int computeShaderTetrahedralFaceCombineY = 0;
        private int computeShaderTetrahedralFaceCombineZ = 0;

        private int computeShaderTetrahedralMapToCubemap;
        private int computeShaderTetrahedralMapToCubemapX = 0;
        private int computeShaderTetrahedralMapToCubemapY = 0;
        private int computeShaderTetrahedralMapToCubemapZ = 0;

        private int computeShaderKernelConvolveSpecularGGX;
        private int computeShaderKernelConvolveSpecularGaussian;

        private bool isSetup;
        private bool isRealtimeRenderingSetup;

        private MipLevel[] mipLevels;

        private float nextUpdateInterval;
        private float updateTime;

        //|||||||||||||||||||||||||||||||||||||| UNITY ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| UNITY ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| UNITY ||||||||||||||||||||||||||||||||||||||
        //unity native callbacks to get this running as expected in playmode

        private void OnEnable()
        {
            Setup();
            SetupRealtimeRendering();
        }

        private void Update()
        {
            if(updateType == UpdateType.UpdateEveryFrame || updateType == UpdateType.UpdateFPS)
                RenderRealtimeTetrahedronMap();
        }

        private void OnDisable()
        {
            CleanupRealtimeRendering();
            Cleanup();
        }

        //|||||||||||||||||||||||||||||||||||||| SETUP ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| SETUP ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| SETUP ||||||||||||||||||||||||||||||||||||||

        /// <summary>
        /// Setup to start rendering a cubemap of the scene.
        /// </summary>
        private void Setup()
        {
#if UNITY_EDITOR
            //get our compute shader manually if for whatever reason it wasn't assigned
            if (tetrahedralRenderingComputeShader == null)
                tetrahedralRenderingComputeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/ImprovedCubemapRendering/RealtimeTetrahedralRenderingV2/TetrahedralRendering.compute");

            if (tetrahedralLutComputeShader == null)
                tetrahedralLutComputeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/ImprovedCubemapRendering/RealtimeTetrahedralRenderingV2/TetrahedralLUT.compute");
#endif

            //if there is no compute shader period, we are in trouble and we can't continue!
            //the compute shader is needed so we can flip the render target so that the faces show up correctly on the final cubemap!
            if (tetrahedralRenderingComputeShader == null || cubemapToTetrahedralLUT == null)
            {
                isSetup = false;
                return;
            }

            //get the main reflection probe
            reflectionProbe = GetComponent<ReflectionProbe>();
            reflectionProbe.mode = UnityEngine.Rendering.ReflectionProbeMode.Custom;

            //setup main gameobject that will hold our camera that will render the scene
            //and make sure it's placed right where the reflection probe capture point is supposed to be
            probeCameraGameObject = new GameObject("probeCameraGameObject");
            probeCameraGameObject.transform.position = reflectionProbe.transform.position;
            probeCameraGameObject.transform.position += reflectionProbe.center;

            //add the camera and match all of the coresponding settings from the reflection probe to our camera
            probeCamera = probeCameraGameObject.AddComponent<Camera>();
            probeCamera.forceIntoRenderTexture = true;
            probeCamera.fieldOfView = TetrahedronFaceFovY;
            probeCamera.aspect = TetrahedronFaceAspect;
            probeCamera.nearClipPlane = reflectionProbe.nearClipPlane;
            probeCamera.farClipPlane = reflectionProbe.farClipPlane;
            probeCamera.backgroundColor = reflectionProbe.backgroundColor;
            probeCamera.cullingMask = reflectionProbe.cullingMask;

            //precompute orientations (no reason to recompute these every frame, they won't change!)
            probeCameraRotationTetrahedronFace0 = Quaternion.Euler(27.36780516f, 0.0f, 0.0f);
            probeCameraRotationTetrahedronFace1 = Quaternion.Euler(27.36780516f, 180.0f, 0.0f);
            probeCameraRotationTetrahedronFace2 = Quaternion.Euler(-27.36780516f, -90.0f, 0.0f);
            probeCameraRotationTetrahedronFace3 = Quaternion.Euler(-27.36780516f, 90.0f, 0.0f);

            //even though the impact is likely negligible we will compute this once instead of having to do it every frame
            updateTime = 1.0f / updateFPS;

            isSetup = true;
        }

        /// <summary>
        /// Setup to handle realtime rendering
        /// </summary>
        private void SetupRealtimeRendering()
        {
            //if we have these render targets still around, make sure we clean it up before we start
            CleanupRealtimeRendering();

            //if we are not setup, then don't bother setting up the realtime rendering resources!
            if (!isSetup)
                return;

            //start with no reflection data in the scene (at least on meshes within bounds of this reflection probe)
            //NOTE: Not implemented here, but if you want multi-bounce static reflections, we could just feed the previous render target here and reflections will naturally get recursively captured.
            reflectionProbe.customBakedTexture = null;

            //NOTE: This is our actual final cubemap, which in technical terms is a Tex2DArray with 6 slices, however we can't work with it or write to it in a compute shader.
            finalCubemap = new RenderTexture(reflectionProbe.resolution, reflectionProbe.resolution, 0, GetRenderTextureFormatType(formatType));
            finalCubemap.dimension = UnityEngine.Rendering.TextureDimension.Cube;
            finalCubemap.filterMode = FilterMode.Trilinear;
            finalCubemap.wrapMode = TextureWrapMode.Clamp;
            finalCubemap.enableRandomWrite = true;
            finalCubemap.isPowerOfTwo = true;
            finalCubemap.useMipMap = true;
            finalCubemap.autoGenerateMips = false;
            finalCubemap.Create();

            //create a regular 2D render target for the camera
            tetrahedronFaceRender = new RenderTexture(reflectionProbe.resolution, reflectionProbe.resolution, renderTargetDepthBits, GetRenderTextureFormatType(formatType));
            tetrahedronFaceRender.filterMode = FilterMode.Trilinear;
            tetrahedronFaceRender.wrapMode = TextureWrapMode.Clamp;
            tetrahedronFaceRender.enableRandomWrite = true;
            tetrahedronFaceRender.isPowerOfTwo = true;
            tetrahedronFaceRender.autoGenerateMips = false;
            tetrahedronFaceRender.useMipMap = false;
            tetrahedronFaceRender.Create();

            //feed the camera our render target so whatever it renders goes into our own render target
            probeCamera.targetTexture = tetrahedronFaceRender;

            //feed the reflection probe our final cubemap also (which will be updated)
            //the nature of this also being realtime means that we will recursively get reflection bounces anyway for free!
            reflectionProbe.customBakedTexture = finalCubemap;

            //create a 2D render target for the tetrahedron map
            tetrahedronMap = new RenderTexture(reflectionProbe.resolution * 2, reflectionProbe.resolution * 2, 0, GetRenderTextureFormatType(formatType));
            tetrahedronMap.filterMode = FilterMode.Trilinear;
            tetrahedronMap.wrapMode = TextureWrapMode.Clamp;
            tetrahedronMap.enableRandomWrite = true;
            tetrahedronMap.isPowerOfTwo = true;
            tetrahedronMap.autoGenerateMips = false;
            tetrahedronMap.useMipMap = false;
            tetrahedronMap.Create();

            intermediateCubemap = new RenderTexture(reflectionProbe.resolution, reflectionProbe.resolution, 0, GetRenderTextureFormatType(formatType));
            intermediateCubemap.dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray;
            intermediateCubemap.volumeDepth = 6;
            intermediateCubemap.filterMode = FilterMode.Trilinear;
            intermediateCubemap.wrapMode = TextureWrapMode.Clamp;
            intermediateCubemap.enableRandomWrite = true;
            intermediateCubemap.isPowerOfTwo = true;
            intermediateCubemap.useMipMap = true;
            intermediateCubemap.autoGenerateMips = false;
            intermediateCubemap.Create();

            //get some data from the compute shader once (they don't change, no reason to get them every frame anyway)
            computeShaderTetrahedralFaceCombine = tetrahedralRenderingComputeShader.FindKernel("TetrahedralFaceCombineNaive");
            computeShaderTetrahedralMapToCubemap = tetrahedralRenderingComputeShader.FindKernel("TetrahedralMapToCubemap");
            computeShaderKernelConvolveSpecularGGX = tetrahedralRenderingComputeShader.FindKernel("ConvolveSpecularGGX");
            computeShaderKernelConvolveSpecularGaussian = tetrahedralRenderingComputeShader.FindKernel("ConvolveSpecularGaussian");

            //to save constantly needing to compute thread group sizes, we only need to do it once here because it doesn't change
            //the only time we need to change this is if the render target changes resolution, and in that case we just need to set things up again
            tetrahedralRenderingComputeShader.GetKernelThreadGroupSizes(computeShaderTetrahedralFaceCombine, out uint threadGroupSizeX, out uint threadGroupSizeY, out uint threadGroupSizeZ);
            computeShaderTetrahedralFaceCombineX = Mathf.CeilToInt(tetrahedronMap.width / threadGroupSizeX);
            computeShaderTetrahedralFaceCombineY = Mathf.CeilToInt(tetrahedronMap.width / threadGroupSizeY);
            computeShaderTetrahedralFaceCombineZ = (int)threadGroupSizeZ;

            tetrahedralRenderingComputeShader.GetKernelThreadGroupSizes(computeShaderTetrahedralMapToCubemap, out threadGroupSizeX, out threadGroupSizeY, out threadGroupSizeZ);
            computeShaderTetrahedralMapToCubemapX = Mathf.CeilToInt(intermediateCubemap.width / threadGroupSizeX);
            computeShaderTetrahedralMapToCubemapY = Mathf.CeilToInt(intermediateCubemap.width / threadGroupSizeY);
            computeShaderTetrahedralMapToCubemapZ = (int)threadGroupSizeZ;

            //set the render resolution once, don't need to do it every frame
            //again only time this needs to be updated is if resolutions change, and in that case we just need to set stuff up again
            tetrahedralRenderingComputeShader.SetVector(RealtimeTetrahedralRenderingV2ShaderIDs.TetrahedronFaceResolution, new Vector4(tetrahedronFaceRender.width, tetrahedronFaceRender.height, 0.0f, 0.0f));
            tetrahedralRenderingComputeShader.SetVector(RealtimeTetrahedralRenderingV2ShaderIDs.TetrahedronMapResolution, new Vector4(tetrahedronMap.width, tetrahedronMap.height, 0.0f, 0.0f));

            tetrahedralRenderingComputeShader.SetTexture(computeShaderTetrahedralFaceCombine, RealtimeTetrahedralRenderingV2ShaderIDs.TetrahedronFaceRender, tetrahedronFaceRender);
            tetrahedralRenderingComputeShader.SetTexture(computeShaderTetrahedralFaceCombine, RealtimeTetrahedralRenderingV2ShaderIDs.TetrahedronFaceMapOutput, tetrahedronMap);

            tetrahedralRenderingComputeShader.SetTexture(computeShaderTetrahedralMapToCubemap, RealtimeTetrahedralRenderingV2ShaderIDs.TetrahedralColorMap, tetrahedronMap);
            tetrahedralRenderingComputeShader.SetTexture(computeShaderTetrahedralMapToCubemap, RealtimeTetrahedralRenderingV2ShaderIDs.TetrahedralCubemapLUT, cubemapToTetrahedralLUT);
            tetrahedralRenderingComputeShader.SetTexture(computeShaderTetrahedralMapToCubemap, RealtimeTetrahedralRenderingV2ShaderIDs.CubemapOutput, intermediateCubemap);

            //|||||||||||||||||||||||||||||||||||||| SETUP - SPECULAR CONVOLUTION TERMS ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SETUP - SPECULAR CONVOLUTION TERMS ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SETUP - SPECULAR CONVOLUTION TERMS ||||||||||||||||||||||||||||||||||||||
            //NOTE: here we precompute a number of variables ahead of time that don't need to be updated every frame
            //This pertains to the mip levels that we sample/modify later when doing specular convolution

            //calculate amount of mips a texture with the reflection probe resolution ought to have
            int mipCount = (int)Mathf.Log(reflectionProbe.resolution, 2);
            int mipLevelResolution = reflectionProbe.resolution;

            mipLevels = new MipLevel[mipCount];

            for (int i = 0; i < mipLevels.Length; i++)
            {
                tetrahedralRenderingComputeShader.GetKernelThreadGroupSizes(computeShaderKernelConvolveSpecularGGX, out uint mipThreadGroupSizeX, out uint mipThreadGroupSizeY, out uint mipThreadGroupSizeZ);

                mipLevels[i] = new MipLevel()
                {
                    mipLevelSquareResolution = mipLevelResolution,
                    roughnessLevel = Mathf.Pow((1.0f / mipLevels.Length) * i, 2),
                    computeShaderKernelThreadGroupSizeX = Mathf.Max(Mathf.CeilToInt(mipLevelResolution / mipThreadGroupSizeX), 4),
                    computeShaderKernelThreadGroupSizeY = Mathf.Max(Mathf.CeilToInt(mipLevelResolution / mipThreadGroupSizeY), 4),
                    computeShaderKernelThreadGroupSizeZ = (int)mipThreadGroupSizeZ,
                };

                mipLevelResolution /= 2;
            }

            tetrahedralRenderingComputeShader.SetInt(RealtimeCubemapRenderingShaderIDsV3.CubemapFaceResolution, reflectionProbe.resolution);
            tetrahedralRenderingComputeShader.SetInt(RealtimeCubemapRenderingShaderIDsV3.SpecularConvolutionSamples, GGXSamples);
            tetrahedralRenderingComputeShader.SetInt(RealtimeTetrahedralRenderingV2ShaderIDs.GaussianSampleRadius, GaussianSamples);
            tetrahedralRenderingComputeShader.SetFloat(RealtimeTetrahedralRenderingV2ShaderIDs.GaussianSampleOffset, GaussianSampleOffsetMultiplier);

            //we are setup now to start rendering!
            isRealtimeRenderingSetup = true;
        }

        //|||||||||||||||||||||||||||||||||||||| CLEANUP ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| CLEANUP ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| CLEANUP ||||||||||||||||||||||||||||||||||||||

        /// <summary>
        /// Cleanup after rendering a cubemap of the scene.
        /// </summary>
        private void Cleanup()
        {
            //remove our main camera gameobject (which will get rid of the camera)
            if (probeCameraGameObject != null)
                DestroyImmediate(probeCameraGameObject);

            //make sure these references are gone
            probeCameraGameObject = null;
            probeCamera = null;

            isSetup = false;
        }

        /// <summary>
        /// Cleanup of realtime rendering resources
        /// </summary>
        private void CleanupRealtimeRendering()
        {
            if(tetrahedronFaceRender != null && tetrahedronFaceRender.IsCreated())
                tetrahedronFaceRender.Release();

            if (tetrahedronMap != null && tetrahedronMap.IsCreated())
                tetrahedronMap.Release();

            if (intermediateCubemap != null && intermediateCubemap.IsCreated())
                intermediateCubemap.Release();

            if (finalCubemap != null && finalCubemap.IsCreated())
                finalCubemap.Release();

            isRealtimeRenderingSetup = false;
        }

        //|||||||||||||||||||||||||||||||||||||| RENDER REALTIME TETRAHEDRON MAP ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| RENDER REALTIME TETRAHEDRON MAP ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| RENDER REALTIME TETRAHEDRON MAP ||||||||||||||||||||||||||||||||||||||

        public void RenderRealtimeTetrahedronMap()
        {
            /*
             * NOTE TO SELF:
             * One thing we could do, is instead of having the process of...
             * - Render And Combine Tetrahedron Faces
             * - Convert Tetrahedron Map -> Intermediate Cubemap
             * - Copy Intermediate Cubemap to Final Cubemap
             * 
             * We could look into simplifying things even more by instead...
             * - Render and Combine Tetrahedron Faces into Intermediate Cubemap
             * - Copy Intermediate Cubemap to Final Cubemap
             * 
             * So instead of having a "tetrahedron map" we could get rid of it and instead use the LUT to render into our intermediate cubemap.
             * This would save additional memory and extra instructions of going into this texture that we won't really be using.
            */

            //if we are not setup, we can't render!
            if (!isSetup || !isRealtimeRenderingSetup)
                return;

            //if it's not our time to update, then don't render!
            if (Time.time < nextUpdateInterval && updateType == UpdateType.UpdateFPS)
                return;

            //|||||||||||||||||||||||||||||||||||||| RENDER AND COMBINE TETRAHEDRON FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER AND COMBINE TETRAHEDRON FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER AND COMBINE TETRAHEDRON FACES ||||||||||||||||||||||||||||||||||||||
            //here we actually render the scene in 4 different orentations
            //render the camera on a given orientation, then combine the result back into our final tetrahedron map which is handled with the compute shader

            //X Positive (X+)
            //rotate and render view
            probeCameraGameObject.transform.rotation = probeCameraRotationTetrahedronFace0;
            probeCamera.Render();
            tetrahedralRenderingComputeShader.SetInt(RealtimeTetrahedralRenderingV2ShaderIDs.TetrahedronFaceIndex, 0);
            tetrahedralRenderingComputeShader.Dispatch(computeShaderTetrahedralFaceCombine, computeShaderTetrahedralFaceCombineX, computeShaderTetrahedralFaceCombineY, computeShaderTetrahedralFaceCombineZ);

            //X Negative (X-)
            //rotate and render view
            probeCameraGameObject.transform.rotation = probeCameraRotationTetrahedronFace1;
            probeCamera.Render();
            tetrahedralRenderingComputeShader.SetInt(RealtimeTetrahedralRenderingV2ShaderIDs.TetrahedronFaceIndex, 1);
            tetrahedralRenderingComputeShader.Dispatch(computeShaderTetrahedralFaceCombine, computeShaderTetrahedralFaceCombineX, computeShaderTetrahedralFaceCombineY, computeShaderTetrahedralFaceCombineZ);

            //Y Positive (Y+)
            //rotate and render view
            probeCameraGameObject.transform.rotation = probeCameraRotationTetrahedronFace2;
            probeCamera.Render();
            tetrahedralRenderingComputeShader.SetInt(RealtimeTetrahedralRenderingV2ShaderIDs.TetrahedronFaceIndex, 2);
            tetrahedralRenderingComputeShader.Dispatch(computeShaderTetrahedralFaceCombine, computeShaderTetrahedralFaceCombineX, computeShaderTetrahedralFaceCombineY, computeShaderTetrahedralFaceCombineZ);

            //Y Negative (Y-)
            //rotate and render view
            probeCameraGameObject.transform.rotation = probeCameraRotationTetrahedronFace3;
            probeCamera.Render();
            tetrahedralRenderingComputeShader.SetInt(RealtimeTetrahedralRenderingV2ShaderIDs.TetrahedronFaceIndex, 3);
            tetrahedralRenderingComputeShader.Dispatch(computeShaderTetrahedralFaceCombine, computeShaderTetrahedralFaceCombineX, computeShaderTetrahedralFaceCombineY, computeShaderTetrahedralFaceCombineZ);

            //|||||||||||||||||||||||||||||||||||||| TETRAHEDRON MAP INTO CUBEMAP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| TETRAHEDRON MAP INTO CUBEMAP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| TETRAHEDRON MAP INTO CUBEMAP ||||||||||||||||||||||||||||||||||||||
            //we rendered our tetrahedron faces into a tetrahedron map, but in order to make this usable for objects in the scene it needs to be a cubemap.
            //so we convert our tetrahedron map into a cubemap using a LUT

            tetrahedralRenderingComputeShader.Dispatch(computeShaderTetrahedralMapToCubemap, computeShaderTetrahedralMapToCubemapX, computeShaderTetrahedralMapToCubemapY, computeShaderTetrahedralMapToCubemapZ);

            //|||||||||||||||||||||||||||||||||||||| SPECULAR CONVOLVE CUBEMAP (TEX2DARRAY) ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SPECULAR CONVOLVE CUBEMAP (TEX2DARRAY) ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SPECULAR CONVOLVE CUBEMAP (TEX2DARRAY) ||||||||||||||||||||||||||||||||||||||

            switch(specularConvolutionFilter)
            {
                case SpecularConvolutionFilter.GGX:
                    //manually filter/generate the mip levels for the cubemap with a filter for specular convolution
                    //NOTE: skip mip 0 because we just wrote to it, and it will be our "sourcE" texture
                    for (int mip = 1; mip < mipLevels.Length; mip++)
                    {
                        MipLevel mipLevel = mipLevels[mip];

                        //note, unlike the compute kernel for combining rendered faces into a cubemap
                        //the properties/textures here change for every mip level so they need to be updated accordingly
                        tetrahedralRenderingComputeShader.SetInt(RealtimeTetrahedralRenderingV2ShaderIDs.CubemapMipFaceResolution, mipLevel.mipLevelSquareResolution);
                        tetrahedralRenderingComputeShader.SetFloat(RealtimeTetrahedralRenderingV2ShaderIDs.SpecularRoughness, mipLevel.roughnessLevel);
                        tetrahedralRenderingComputeShader.SetTexture(computeShaderKernelConvolveSpecularGGX, RealtimeTetrahedralRenderingV2ShaderIDs.CubemapInput, intermediateCubemap, mip - 1);
                        tetrahedralRenderingComputeShader.SetTexture(computeShaderKernelConvolveSpecularGGX, RealtimeTetrahedralRenderingV2ShaderIDs.CubemapOutput, intermediateCubemap, mip);
                        tetrahedralRenderingComputeShader.Dispatch(computeShaderKernelConvolveSpecularGGX, mipLevel.computeShaderKernelThreadGroupSizeX, mipLevel.computeShaderKernelThreadGroupSizeY, mipLevel.computeShaderKernelThreadGroupSizeZ);
                    }

                    break;
                case SpecularConvolutionFilter.Gaussian:
                    //manually filter/generate the mip levels for the cubemap with a filter for specular convolution
                    //NOTE: skip mip 0 because we just wrote to it, and it will be our "sourcE" texture
                    for (int mip = 1; mip < mipLevels.Length; mip++)
                    {
                        MipLevel mipLevel = mipLevels[mip];

                        //note, unlike the compute kernel for combining rendered faces into a cubemap
                        //the properties/textures here change for every mip level so they need to be updated accordingly
                        tetrahedralRenderingComputeShader.SetInt(RealtimeTetrahedralRenderingV2ShaderIDs.CubemapMipFaceResolution, mipLevel.mipLevelSquareResolution);
                        tetrahedralRenderingComputeShader.SetTexture(computeShaderKernelConvolveSpecularGaussian, RealtimeTetrahedralRenderingV2ShaderIDs.CubemapInput, intermediateCubemap, mip - 1);
                        tetrahedralRenderingComputeShader.SetTexture(computeShaderKernelConvolveSpecularGaussian, RealtimeTetrahedralRenderingV2ShaderIDs.CubemapOutput, intermediateCubemap, mip);
                        tetrahedralRenderingComputeShader.Dispatch(computeShaderKernelConvolveSpecularGaussian, mipLevel.computeShaderKernelThreadGroupSizeX, mipLevel.computeShaderKernelThreadGroupSizeY, mipLevel.computeShaderKernelThreadGroupSizeZ);
                    }

                    break;
            }

            //|||||||||||||||||||||||||||||||||||||| CUBEMAP (TEX2DARRAY) TO CUBEMAP (TEXCUBE) ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| CUBEMAP (TEX2DARRAY) TO CUBEMAP (TEXCUBE) ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| CUBEMAP (TEX2DARRAY) TO CUBEMAP (TEXCUBE) ||||||||||||||||||||||||||||||||||||||

            //then to transfer the data as efficently as possible, we use Graphics.CopyTexture to copy each slice into the cubemap!
            //after this then we have a render texture cubemap that we just wrote into!
            for (int i = 0; i < intermediateCubemap.mipmapCount; i++)
            {
                Graphics.CopyTexture(intermediateCubemap, 0, i, finalCubemap, 0, i);
                Graphics.CopyTexture(intermediateCubemap, 1, i, finalCubemap, 1, i);
                Graphics.CopyTexture(intermediateCubemap, 2, i, finalCubemap, 2, i);
                Graphics.CopyTexture(intermediateCubemap, 3, i, finalCubemap, 3, i);
                Graphics.CopyTexture(intermediateCubemap, 4, i, finalCubemap, 4, i);
                Graphics.CopyTexture(intermediateCubemap, 5, i, finalCubemap, 5, i);
            }

            //update next time interval
            //NOTE TO SELF: using Time.time in the long term might have precison issues later, would be prefered to switch this to double instead.
            nextUpdateInterval = Time.time + updateTime;
        }

        //|||||||||||||||||||||||||||||||||||||| EDITOR ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| EDITOR ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| EDITOR ||||||||||||||||||||||||||||||||||||||
#if UNITY_EDITOR

        [ContextMenu("RenderRealtimeTetrahedronMapOnce")]
        public void RenderRealtimeTetrahedronMapOnce()
        {
            CleanupRendering();

            Setup();
            SetupRealtimeRendering();
            RenderRealtimeTetrahedronMap();

            string unityAssetPath = string.Format("Assets/ImprovedCubemapRendering/RealtimeTetrahedralRenderingV2/Data/{0}_{1}.asset", SceneManager.GetActiveScene().name, gameObject.name);

            AssetDatabase.DeleteAsset(unityAssetPath);
            AssetDatabase.CreateAsset(finalCubemap, unityAssetPath);
        }

        [ContextMenu("CleanupRendering")]
        public void CleanupRendering()
        {
            CleanupRealtimeRendering();
            Cleanup();
        }

        //|||||||||||||||||||||||||||||||||||||| GENERATE TETRAHEDRON LUT ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| GENERATE TETRAHEDRON LUT ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| GENERATE TETRAHEDRON LUT ||||||||||||||||||||||||||||||||||||||

        [ContextMenu("GenerateTetrahedronLUT")]
        public void GenerateTetrahedronLUT()
        {
            RenderTextureFormat lutRenderTextureFormat = RenderTextureFormat.RGHalf;
            TextureFormat lutTextureFormat = TextureFormat.RGHalf;

            reflectionProbe = GetComponent<ReflectionProbe>();

            int cubemapFaceResolution = reflectionProbe.resolution;

            Texture2DArray outputTexture2DArray = new Texture2DArray(cubemapFaceResolution, cubemapFaceResolution, 6, lutTextureFormat, false, true, true);

            int computeShaderCubemapToTetrahedralUV = tetrahedralLutComputeShader.FindKernel("CubemapToTetrahedralUV");
            tetrahedralLutComputeShader.SetInt("CubemapFaceResolution", cubemapFaceResolution);
            tetrahedralLutComputeShader.SetVector("TetrahedralMapResolution", new Vector4(cubemapFaceResolution * lutSupersampling, cubemapFaceResolution * lutSupersampling, 0, 0));
            tetrahedralLutComputeShader.SetFloat("VerticalFOV", TetrahedronFaceFovY);

            //NOTE HERE: While reconstruting a math function for the LUT (rather than just doing a capture with the RayDirectionTruth.shader)
            //The FOV value that was most accurate to the actual capture was this.
            //This value was also eyeballed and tweaked by hand, so I'm not sure how to get to this value mathematically.
            //But I adjusted the values and played with it until it looked really close to the actual ground truth capture... and it works so to hell with it!
            tetrahedralLutComputeShader.SetFloat("HorizontalFOV", 131.55f); // Original Paper Value: 143.98570868

            RenderTexture inputFace = new RenderTexture(cubemapFaceResolution, cubemapFaceResolution, 0, lutRenderTextureFormat);
            inputFace.filterMode = FilterMode.Bilinear;
            inputFace.wrapMode = TextureWrapMode.Clamp;
            inputFace.dimension = UnityEngine.Rendering.TextureDimension.Tex2D;
            inputFace.enableRandomWrite = true;
            inputFace.isPowerOfTwo = true;
            inputFace.useMipMap = false;
            inputFace.autoGenerateMips = false;
            inputFace.Create();

            //int inputFaceMemorySize = (int)Profiler.GetRuntimeMemorySizeLong(inputFace);
            int inputFaceMemorySize = (int)RenderTextureSize.GetRenderTextureMemorySize(inputFace);

            NativeArray<byte> inputFaceData = new NativeArray<byte>(inputFaceMemorySize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            for (int i = 0; i < 6; i++)
            {
                tetrahedralLutComputeShader.SetInt("CubemapFaceIndex", i);
                tetrahedralLutComputeShader.SetTexture(computeShaderCubemapToTetrahedralUV, "Output", inputFace);
                tetrahedralLutComputeShader.Dispatch(computeShaderCubemapToTetrahedralUV, Mathf.CeilToInt(cubemapFaceResolution / 4), Mathf.CeilToInt(cubemapFaceResolution / 4), 1);

                AsyncGPUReadbackRequest inputFaceDataRequest = AsyncGPUReadback.RequestIntoNativeArray(ref inputFaceData, inputFace, 0, (request) =>
                {
                    outputTexture2DArray.SetPixelData<byte>(inputFaceData, 0, i);
                    outputTexture2DArray.Apply(false, false);
                });

                inputFaceDataRequest.WaitForCompletion();
            }

            inputFaceData.Dispose();
            inputFace.Release();

            AssetDatabase.CreateAsset(outputTexture2DArray, string.Format("Assets/ImprovedCubemapRendering/RealtimeTetrahedralRenderingV2/Data/CubemapToTetrahedronLUT_{0}.asset", reflectionProbe.resolution));
        }

#endif
        //|||||||||||||||||||||||||||||||||||||| RENDER TEXTURE FORMAT ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| RENDER TEXTURE FORMAT ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| RENDER TEXTURE FORMAT ||||||||||||||||||||||||||||||||||||||
        //nothing critical here, but this is just a helper function to convert our custom enum type to the coresponding render texture format.
        //just makes it easier on the user end to configure the prefered render texture format

        private static RenderTextureFormat GetRenderTextureFormatType(RealtimeCubemapTextureFormatType formatType)
        {
            switch (formatType)
            {
                case RealtimeCubemapTextureFormatType.RGBAFloat:
                    return RenderTextureFormat.ARGBFloat;
                case RealtimeCubemapTextureFormatType.RGBAHalf:
                    return RenderTextureFormat.ARGBHalf;
                case RealtimeCubemapTextureFormatType.RGB111110:
                    return RenderTextureFormat.RGB111110Float;
                case RealtimeCubemapTextureFormatType.RGBA8:
                    return RenderTextureFormat.ARGB32;
                default:
                    return RenderTextureFormat.ARGBHalf;
            }
        }
    }
}