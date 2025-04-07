using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
#endif

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

//https://discussions.unity.com/t/specular-convolution-when-calculating-mip-maps-for-cubemap-render-texture/729652/15

namespace ImprovedCubemapRendering
{
    public class RealtimeCubemapRenderingV4 : MonoBehaviour
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

        public struct MipLevel
        {
            public int mipLevelSquareResolution;
            public float roughnessLevel;
            public int computeShaderKernelThreadGroupSizeX;
            public int computeShaderKernelThreadGroupSizeY;
            public int computeShaderKernelThreadGroupSizeZ;
        }

        public class SceneMesh
        {
            public Mesh mesh;
            public Matrix4x4 localToWorldMatrix;
        }

        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||

        [Header("Setup")]
        public ComputeShader cubemapRenderingCompute;

        [Header("Precomputation")]
        public Texture2D skyboxVisibilityXPOS;
        public Texture2D skyboxVisibilityXNEG;
        public Texture2D skyboxVisibilityYPOS;
        public Texture2D skyboxVisibilityYNEG;
        public Texture2D skyboxVisibilityZPOS;
        public Texture2D skyboxVisibilityZNEG;

        [Header("Properties")]
        public RealtimeCubemapTextureFormatType formatType = RealtimeCubemapTextureFormatType.RGBAHalf;
        public UpdateType updateType = UpdateType.UpdateFPS;
        public int updateFPS = 30;
        public int GGXSpecularConvolutionSamples = 256;

        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||

        private ReflectionProbe reflectionProbe;

        //NOTE: I would like to explore potentially using less render textures here to save on memory
        //whittling it down to atleast 2 would be ideal (camera render target, cubemap)
        private RenderTexture cubemapFaceRender;
        private RenderTexture intermediateCubemap;
        private RenderTexture finalCubemap;
        private Rect cubemapFaceRenderViewport;

        private static int renderTargetDepthBits = 32; //0 16 24 32

        private RenderTextureFormat skyboxVisibilityFormat = RenderTextureFormat.R8;

        private int computeShaderKernelCubemapCombine;
        private int computeShaderKernelConvolveSpecularGGX;
        private int computeShaderThreadGroupSizeX = 0;
        private int computeShaderThreadGroupSizeY = 0;
        private int computeShaderThreadGroupSizeZ = 0;

        private Mesh skyboxMesh;

        private CommandBuffer realtimeSkyboxCommandBuffer;

        private Matrix4x4 realtimeSkyboxMeshTransformMatrix;
        private Matrix4x4 realtimeSkyboxScaleMatrix;
        private Matrix4x4 realtimeSkyboxCameraProjection;
        private Matrix4x4 realtimeSkyboxViewPosition_XPOS;
        private Matrix4x4 realtimeSkyboxViewPosition_XNEG;
        private Matrix4x4 realtimeSkyboxViewPosition_YPOS;
        private Matrix4x4 realtimeSkyboxViewPosition_YNEG;
        private Matrix4x4 realtimeSkyboxViewPosition_ZPOS;
        private Matrix4x4 realtimeSkyboxViewPosition_ZNEG;

        private MipLevel[] specularConvolutionMipLevels;

        private Shader blackObjectShader => Shader.Find("RealtimeCubemapRenderingV4/BlackObject");

        private bool isSetup;

        private float nextUpdateInterval;
        private float updateTime;

        //|||||||||||||||||||||||||||||||||||||| UNITY ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| UNITY ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| UNITY ||||||||||||||||||||||||||||||||||||||
        //unity native callbacks to get this running as expected in playmode

        private void OnEnable()
        {
            Setup();
        }

        private void Update()
        {
            if(updateType == UpdateType.UpdateEveryFrame || updateType == UpdateType.UpdateFPS)
                RenderRealtimeCubemap();
        }

        private void OnDisable()
        {
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
            Cleanup();

            //get the main reflection probe
            reflectionProbe = GetComponent<ReflectionProbe>();
            reflectionProbe.mode = UnityEngine.Rendering.ReflectionProbeMode.Custom;

#if UNITY_EDITOR
            //get our compute shader manually if for whatever reason it wasn't assigned
            if (cubemapRenderingCompute == null)
                cubemapRenderingCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/ImprovedCubemapRendering/RealtimeCubemapRenderingV4/RealtimeCubemapRenderingV4.compute");
#endif

            //if there is no compute shader period, we are in trouble and we can't continue!
            //the compute shader is needed so we can flip the render target so that the faces show up correctly on the final cubemap!
            if (cubemapRenderingCompute == null)
            {
                isSetup = false;
                return;
            }

            //|||||||||||||||||||||||||||||||||||||| SETUP - RENDER TARGETS ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SETUP - RENDER TARGETS ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SETUP - RENDER TARGETS ||||||||||||||||||||||||||||||||||||||

            realtimeSkyboxCommandBuffer = new CommandBuffer();

            //create a regular 2D render target for the camera
            cubemapFaceRender = new RenderTexture(reflectionProbe.resolution, reflectionProbe.resolution, renderTargetDepthBits, GetRenderTextureFormatType(formatType));
            cubemapFaceRender.filterMode = FilterMode.Trilinear;
            cubemapFaceRender.wrapMode = TextureWrapMode.Clamp;
            cubemapFaceRender.enableRandomWrite = true;
            cubemapFaceRender.isPowerOfTwo = true;
            cubemapFaceRender.Create();

            //NOTE: Since there is no native "RWTextureCube" we use a Tex2DArray with 6 slices which is similar to a cubemap setup.
            intermediateCubemap = new RenderTexture(reflectionProbe.resolution, reflectionProbe.resolution, renderTargetDepthBits, GetRenderTextureFormatType(formatType));
            intermediateCubemap.filterMode = FilterMode.Trilinear;
            intermediateCubemap.wrapMode = TextureWrapMode.Clamp;
            intermediateCubemap.volumeDepth = 6; //6 faces in cubemap
            intermediateCubemap.dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray;
            intermediateCubemap.enableRandomWrite = true;
            intermediateCubemap.isPowerOfTwo = true;
            intermediateCubemap.useMipMap = true;
            intermediateCubemap.autoGenerateMips = false;
            intermediateCubemap.Create();

            //NOTE: This is a workaround since "RWTextureCube" objects don't exist in compute shaders, and we are working instead with a RWTexture2DArray with 6 elements.
            //Most shaders in the scene will expect a cubemap sampler, so we will create another render texture, with the cube dimension.
            finalCubemap = new RenderTexture(reflectionProbe.resolution, reflectionProbe.resolution, renderTargetDepthBits, GetRenderTextureFormatType(formatType));
            finalCubemap.dimension = UnityEngine.Rendering.TextureDimension.Cube;
            finalCubemap.filterMode = FilterMode.Trilinear;
            finalCubemap.wrapMode = TextureWrapMode.Clamp;
            finalCubemap.enableRandomWrite = true;
            finalCubemap.isPowerOfTwo = true;
            finalCubemap.useMipMap = true;
            finalCubemap.autoGenerateMips = false;
            finalCubemap.Create();

            //setup rect for the viewport later
            cubemapFaceRenderViewport = new Rect(0, 0, cubemapFaceRender.width, cubemapFaceRender.height);

            //feed the reflection probe our final cubemap also (which will be updated)
            //the nature of this also being realtime means that we will recursively get reflection bounces anyway for free!
            reflectionProbe.customBakedTexture = finalCubemap;

            //|||||||||||||||||||||||||||||||||||||| SETUP - MATRICES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SETUP - MATRICES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SETUP - MATRICES ||||||||||||||||||||||||||||||||||||||
            //setup a number of matrices ahead of time (they don't change, no reason to update for every frame)

            realtimeSkyboxScaleMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1, -1, -1));
            realtimeSkyboxCameraProjection = Matrix4x4.Perspective(90.0f, 1.0f, reflectionProbe.nearClipPlane, reflectionProbe.farClipPlane);
            realtimeSkyboxMeshTransformMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * (reflectionProbe.farClipPlane * 0.5f));
            realtimeSkyboxViewPosition_XPOS = realtimeSkyboxScaleMatrix * Matrix4x4.LookAt(Vector3.zero, Vector3.left, Vector3.up);
            realtimeSkyboxViewPosition_XNEG = realtimeSkyboxScaleMatrix * Matrix4x4.LookAt(Vector3.zero, Vector3.right, Vector3.up);
            realtimeSkyboxViewPosition_YPOS = realtimeSkyboxScaleMatrix * Matrix4x4.LookAt(Vector3.zero, Vector3.down, Vector3.forward);
            realtimeSkyboxViewPosition_YNEG = realtimeSkyboxScaleMatrix * Matrix4x4.LookAt(Vector3.zero, Vector3.up, Vector3.back);
            realtimeSkyboxViewPosition_ZPOS = realtimeSkyboxScaleMatrix * Matrix4x4.LookAt(Vector3.zero, Vector3.forward, Vector3.up);
            realtimeSkyboxViewPosition_ZNEG = realtimeSkyboxScaleMatrix * Matrix4x4.LookAt(Vector3.zero, Vector3.back, Vector3.up);

            //|||||||||||||||||||||||||||||||||||||| SETUP - COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SETUP - COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SETUP - COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||
            //get some data from the compute shader once (they don't change, no reason to get them every frame anyway)

            computeShaderKernelCubemapCombine = cubemapRenderingCompute.FindKernel("CubemapCombine");
            computeShaderKernelConvolveSpecularGGX = cubemapRenderingCompute.FindKernel("ConvolveSpecularGGX");
            cubemapRenderingCompute.GetKernelThreadGroupSizes(computeShaderKernelCubemapCombine, out uint threadGroupSizeX, out uint threadGroupSizeY, out uint threadGroupSizeZ);
            computeShaderThreadGroupSizeX = Mathf.CeilToInt(intermediateCubemap.width / threadGroupSizeX);
            computeShaderThreadGroupSizeY = Mathf.CeilToInt(intermediateCubemap.height / threadGroupSizeY);
            computeShaderThreadGroupSizeZ = (int)threadGroupSizeZ;
            cubemapRenderingCompute.SetInt(RealtimeCubemapRenderingShaderIDsV4.CubemapFaceResolution, reflectionProbe.resolution);
            cubemapRenderingCompute.SetInt(RealtimeCubemapRenderingShaderIDsV4.SpecularConvolutionSamples, GGXSpecularConvolutionSamples);

            //|||||||||||||||||||||||||||||||||||||| SETUP - SPECULAR CONVOLUTION TERMS ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SETUP - SPECULAR CONVOLUTION TERMS ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SETUP - SPECULAR CONVOLUTION TERMS ||||||||||||||||||||||||||||||||||||||
            //NOTE: here we precompute a number of variables ahead of time that don't need to be updated every frame
            //This pertains to the mip levels that we sample/modify later when doing specular convolution

            //calculate amount of mips a texture with the reflection probe resolution ought to have
            int mipCount = (int)Mathf.Log(reflectionProbe.resolution, 2);
            int mipLevelResolution = reflectionProbe.resolution;

            specularConvolutionMipLevels = new MipLevel[mipCount];

            for (int i = 0; i < specularConvolutionMipLevels.Length; i++)
            {
                cubemapRenderingCompute.GetKernelThreadGroupSizes(computeShaderKernelConvolveSpecularGGX, out uint mipThreadGroupSizeX, out uint mipThreadGroupSizeY, out uint mipThreadGroupSizeZ);

                specularConvolutionMipLevels[i] = new MipLevel()
                {
                    mipLevelSquareResolution = mipLevelResolution,
                    roughnessLevel = Mathf.Pow((1.0f / specularConvolutionMipLevels.Length) * i, 2),
                    computeShaderKernelThreadGroupSizeX = Mathf.Max(Mathf.CeilToInt(mipLevelResolution / mipThreadGroupSizeX), 4),
                    computeShaderKernelThreadGroupSizeY = Mathf.Max(Mathf.CeilToInt(mipLevelResolution / mipThreadGroupSizeY), 4),
                    computeShaderKernelThreadGroupSizeZ = (int)mipThreadGroupSizeZ,
                };

                mipLevelResolution /= 2;
            }

            //|||||||||||||||||||||||||||||||||||||| SETUP - COMPUTE SHADER TEXTURES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SETUP - COMPUTE SHADER TEXTURES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SETUP - COMPUTE SHADER TEXTURES ||||||||||||||||||||||||||||||||||||||
            //set the textures for the kernels ahead of time, no need to set it every frame
            //the textures have random read/write enabled also so they will natrually get updated
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, RealtimeCubemapRenderingShaderIDsV4.CubemapFace, cubemapFaceRender);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, RealtimeCubemapRenderingShaderIDsV4.CubemapOutput, intermediateCubemap);

            //|||||||||||||||||||||||||||||||||||||| SETUP - SKYBOX MESH ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SETUP - SKYBOX MESH ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SETUP - SKYBOX MESH ||||||||||||||||||||||||||||||||||||||

            //GameObject skyboxMeshGameObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject skyboxMeshGameObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            skyboxMesh = skyboxMeshGameObject.GetComponent<MeshFilter>().sharedMesh;
            DestroyImmediate(skyboxMeshGameObject);

            //|||||||||||||||||||||||||||||||||||||| SETUP - MISC ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SETUP - MISC ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SETUP - MISC ||||||||||||||||||||||||||||||||||||||

            //even though the impact is likely negligible we will compute this once instead of having to do it every frame
            updateTime = 1.0f / updateFPS;

            //we are setup now to start rendering!
            isSetup = true;
        }

        //|||||||||||||||||||||||||||||||||||||| CLEANUP ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| CLEANUP ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| CLEANUP ||||||||||||||||||||||||||||||||||||||

        /// <summary>
        /// Cleanup after rendering a cubemap of the scene.
        /// </summary>
        private void Cleanup()
        {
            if (cubemapFaceRender != null && cubemapFaceRender.IsCreated())
                cubemapFaceRender.Release();

            if (intermediateCubemap != null && intermediateCubemap.IsCreated())
                intermediateCubemap.Release();

            if (finalCubemap != null && finalCubemap.IsCreated())
                finalCubemap.Release();

            if (realtimeSkyboxCommandBuffer != null)
                realtimeSkyboxCommandBuffer.Clear();

            isSetup = false;
        }


        //|||||||||||||||||||||||||||||||||||||| RENDER REALTIME CUBEMAP ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| RENDER REALTIME CUBEMAP ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| RENDER REALTIME CUBEMAP ||||||||||||||||||||||||||||||||||||||

        public void RenderRealtimeCubemap()
        {
            //if we are not setup, we can't render!
            if (!isSetup)
                return;

            //if it's not our time to update, then don't render!
            if (Time.time < nextUpdateInterval && updateType == UpdateType.UpdateFPS)
                return;

            //|||||||||||||||||||||||||||||||||||||| RENDER CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //here we actually render the scene in 6 different axis
            //render the camera on a given orientation, then combine the result back into our final cubemap which is handled with the compute shader

            //X Positive (X+)
            realtimeSkyboxCommandBuffer.Clear();
            realtimeSkyboxCommandBuffer.SetRenderTarget(cubemapFaceRender);
            realtimeSkyboxCommandBuffer.SetViewProjectionMatrices(realtimeSkyboxViewPosition_XPOS, realtimeSkyboxCameraProjection);
            realtimeSkyboxCommandBuffer.SetViewport(cubemapFaceRenderViewport);
            realtimeSkyboxCommandBuffer.ClearRenderTarget(true, true, Color.clear); //IMPORTANT: clear contents before we render a new frame
            realtimeSkyboxCommandBuffer.DrawMesh(skyboxMesh, realtimeSkyboxMeshTransformMatrix, RenderSettings.skybox);

            Graphics.ExecuteCommandBuffer(realtimeSkyboxCommandBuffer);

            cubemapRenderingCompute.SetInt(RealtimeCubemapRenderingShaderIDsV4.CubemapFaceIndex, 0);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, RealtimeCubemapRenderingShaderIDsV4.SkyboxVisibilityFace, skyboxVisibilityXPOS);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, computeShaderThreadGroupSizeX, computeShaderThreadGroupSizeY, computeShaderThreadGroupSizeZ);

            //X Negative (X-)
            realtimeSkyboxCommandBuffer.Clear();
            realtimeSkyboxCommandBuffer.SetRenderTarget(cubemapFaceRender);
            realtimeSkyboxCommandBuffer.SetViewProjectionMatrices(realtimeSkyboxViewPosition_XNEG, realtimeSkyboxCameraProjection);
            realtimeSkyboxCommandBuffer.SetViewport(cubemapFaceRenderViewport);
            realtimeSkyboxCommandBuffer.ClearRenderTarget(true, true, Color.clear); //IMPORTANT: clear contents before we render a new frame
            realtimeSkyboxCommandBuffer.DrawMesh(skyboxMesh, realtimeSkyboxMeshTransformMatrix, RenderSettings.skybox);

            Graphics.ExecuteCommandBuffer(realtimeSkyboxCommandBuffer);

            cubemapRenderingCompute.SetInt(RealtimeCubemapRenderingShaderIDsV4.CubemapFaceIndex, 1);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, RealtimeCubemapRenderingShaderIDsV4.SkyboxVisibilityFace, skyboxVisibilityXNEG);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, computeShaderThreadGroupSizeX, computeShaderThreadGroupSizeY, computeShaderThreadGroupSizeZ);

            //Y Positive (Y+)
            realtimeSkyboxCommandBuffer.Clear();
            realtimeSkyboxCommandBuffer.SetRenderTarget(cubemapFaceRender);
            realtimeSkyboxCommandBuffer.SetViewProjectionMatrices(realtimeSkyboxViewPosition_YPOS, realtimeSkyboxCameraProjection);
            realtimeSkyboxCommandBuffer.SetViewport(cubemapFaceRenderViewport);
            realtimeSkyboxCommandBuffer.ClearRenderTarget(true, true, Color.clear); //IMPORTANT: clear contents before we render a new frame
            realtimeSkyboxCommandBuffer.DrawMesh(skyboxMesh, realtimeSkyboxMeshTransformMatrix, RenderSettings.skybox);

            Graphics.ExecuteCommandBuffer(realtimeSkyboxCommandBuffer);

            cubemapRenderingCompute.SetInt(RealtimeCubemapRenderingShaderIDsV4.CubemapFaceIndex, 2);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, RealtimeCubemapRenderingShaderIDsV4.SkyboxVisibilityFace, skyboxVisibilityYPOS);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, computeShaderThreadGroupSizeX, computeShaderThreadGroupSizeY, computeShaderThreadGroupSizeZ);

            //Y Negative (Y-)
            realtimeSkyboxCommandBuffer.Clear();
            realtimeSkyboxCommandBuffer.SetRenderTarget(cubemapFaceRender);
            realtimeSkyboxCommandBuffer.SetViewProjectionMatrices(realtimeSkyboxViewPosition_YNEG, realtimeSkyboxCameraProjection);
            realtimeSkyboxCommandBuffer.SetViewport(cubemapFaceRenderViewport);
            realtimeSkyboxCommandBuffer.ClearRenderTarget(true, true, Color.clear); //IMPORTANT: clear contents before we render a new frame
            realtimeSkyboxCommandBuffer.DrawMesh(skyboxMesh, realtimeSkyboxMeshTransformMatrix, RenderSettings.skybox);

            Graphics.ExecuteCommandBuffer(realtimeSkyboxCommandBuffer);

            cubemapRenderingCompute.SetInt(RealtimeCubemapRenderingShaderIDsV4.CubemapFaceIndex, 3);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, RealtimeCubemapRenderingShaderIDsV4.SkyboxVisibilityFace, skyboxVisibilityYNEG);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, computeShaderThreadGroupSizeX, computeShaderThreadGroupSizeY, computeShaderThreadGroupSizeZ);

            //Z Positive (Z+)
            realtimeSkyboxCommandBuffer.Clear();
            realtimeSkyboxCommandBuffer.SetRenderTarget(cubemapFaceRender);
            realtimeSkyboxCommandBuffer.SetViewProjectionMatrices(realtimeSkyboxViewPosition_ZPOS, realtimeSkyboxCameraProjection);
            realtimeSkyboxCommandBuffer.SetViewport(cubemapFaceRenderViewport);
            realtimeSkyboxCommandBuffer.ClearRenderTarget(true, true, Color.clear); //IMPORTANT: clear contents before we render a new frame
            realtimeSkyboxCommandBuffer.DrawMesh(skyboxMesh, realtimeSkyboxMeshTransformMatrix, RenderSettings.skybox);

            Graphics.ExecuteCommandBuffer(realtimeSkyboxCommandBuffer);

            cubemapRenderingCompute.SetInt(RealtimeCubemapRenderingShaderIDsV4.CubemapFaceIndex, 4);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, RealtimeCubemapRenderingShaderIDsV4.SkyboxVisibilityFace, skyboxVisibilityZPOS);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, computeShaderThreadGroupSizeX, computeShaderThreadGroupSizeY, computeShaderThreadGroupSizeZ);

            //Z Negative (Z-)
            realtimeSkyboxCommandBuffer.Clear();
            realtimeSkyboxCommandBuffer.SetRenderTarget(cubemapFaceRender);
            realtimeSkyboxCommandBuffer.SetViewProjectionMatrices(realtimeSkyboxViewPosition_ZNEG, realtimeSkyboxCameraProjection);
            realtimeSkyboxCommandBuffer.SetViewport(cubemapFaceRenderViewport);
            realtimeSkyboxCommandBuffer.ClearRenderTarget(true, true, Color.clear); //IMPORTANT: clear contents before we render a new frame
            realtimeSkyboxCommandBuffer.DrawMesh(skyboxMesh, realtimeSkyboxMeshTransformMatrix, RenderSettings.skybox);

            Graphics.ExecuteCommandBuffer(realtimeSkyboxCommandBuffer);

            cubemapRenderingCompute.SetInt(RealtimeCubemapRenderingShaderIDsV4.CubemapFaceIndex, 5);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, RealtimeCubemapRenderingShaderIDsV4.SkyboxVisibilityFace, skyboxVisibilityZNEG);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, computeShaderThreadGroupSizeX, computeShaderThreadGroupSizeY, computeShaderThreadGroupSizeZ);

            //generate mips so PBR shaders can sample a slightly blurrier version of the reflection cubemap
            //IMPORTANT NOTE: this is not PBR compliant, PBR shaders in unity (and most engines if configured as such) actually need a special mip map setup for reflection cubemaps (specular convolution)
            //so what actually comes from this is not correct nor should it be used (if you really really really have no other choice I suppose you can)
            //with that said in a later version of this we do use a proper specular convolution setup, but this is here just for illustrative/simplicity purposes
            intermediateCubemap.GenerateMips();

            //|||||||||||||||||||||||||||||||||||||| SPECULAR CONVOLVE CUBEMAP (TEX2DARRAY) ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SPECULAR CONVOLVE CUBEMAP (TEX2DARRAY) ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SPECULAR CONVOLVE CUBEMAP (TEX2DARRAY) ||||||||||||||||||||||||||||||||||||||

            //iterate for each mip level
            for (int mip = 1; mip < specularConvolutionMipLevels.Length; mip++)
            {
                MipLevel mipLevel = specularConvolutionMipLevels[mip];

                //note, unlike the compute kernel for combining rendered faces into a cubemap
                //the properties/textures here change for every mip level so they need to be updated accordingly
                cubemapRenderingCompute.SetInt(RealtimeCubemapRenderingShaderIDsV4.CubemapMipFaceResolution, mipLevel.mipLevelSquareResolution);
                cubemapRenderingCompute.SetFloat(RealtimeCubemapRenderingShaderIDsV4.SpecularRoughness, mipLevel.roughnessLevel);
                cubemapRenderingCompute.SetTexture(computeShaderKernelConvolveSpecularGGX, RealtimeCubemapRenderingShaderIDsV4.InputCubemap, intermediateCubemap, mip - 1);
                cubemapRenderingCompute.SetTexture(computeShaderKernelConvolveSpecularGGX, RealtimeCubemapRenderingShaderIDsV4.CubemapOutput, intermediateCubemap, mip);
                cubemapRenderingCompute.Dispatch(computeShaderKernelConvolveSpecularGGX, mipLevel.computeShaderKernelThreadGroupSizeX, mipLevel.computeShaderKernelThreadGroupSizeY, mipLevel.computeShaderKernelThreadGroupSizeZ);
            }

            //|||||||||||||||||||||||||||||||||||||| TRANSFER FINAL RESULTS TO PROPER CUBEMAP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| TRANSFER FINAL RESULTS TO PROPER CUBEMAP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| TRANSFER FINAL RESULTS TO PROPER CUBEMAP ||||||||||||||||||||||||||||||||||||||

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
            nextUpdateInterval = Time.time + updateTime;
        }

        //|||||||||||||||||||||||||||||||||||||| EDITOR ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| EDITOR ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| EDITOR ||||||||||||||||||||||||||||||||||||||

        [ContextMenu("RenderRealtimeCubemapOnce")]
        public void RenderRealtimeCubemapOnce()
        {
            Setup();
            RenderRealtimeCubemap();

            string unityAssetPath = string.Format("Assets/ImprovedCubemapRendering/RealtimeCubemapRenderingV3/Data/{0}_{1}.asset", SceneManager.GetActiveScene().name, gameObject.name);

            AssetDatabase.DeleteAsset(unityAssetPath);
            AssetDatabase.CreateAsset(finalCubemap, unityAssetPath);
        }

        [ContextMenu("CleanupRendering")]
        public void CleanupRendering()
        {
            Cleanup();
        }

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

        public static bool ContainBounds(Bounds bounds, Bounds target) => bounds.Contains(target.center) || bounds.Contains(target.min) || bounds.Contains(target.max);

        //|||||||||||||||||||||||||||||||||||||| PRECOMPUTE SCENE BUFFER ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PRECOMPUTE SCENE BUFFER ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PRECOMPUTE SCENE BUFFER ||||||||||||||||||||||||||||||||||||||

        [ContextMenu("Precompute Skybox Visibility")]
        public void PrecomputeSkyboxVisibility()
        {
            //|||||||||||||||||||||||||||||||||||||| PRECOMPUTE SCENE BUFFER - SETUP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| PRECOMPUTE SCENE BUFFER - SETUP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| PRECOMPUTE SCENE BUFFER - SETUP ||||||||||||||||||||||||||||||||||||||

            //get the main reflection probe
            reflectionProbe = GetComponent<ReflectionProbe>();

            //setup our render texture converter class so we can convert render textures to texture2D objects efficently/easily
            RenderTextureConverter renderTextureConverter = new RenderTextureConverter();

            //create a regular 2D render target for the camera
            RenderTexture cubemapFace = new RenderTexture(reflectionProbe.resolution, reflectionProbe.resolution, renderTargetDepthBits, skyboxVisibilityFormat);
            cubemapFace.filterMode = FilterMode.Trilinear;
            cubemapFace.wrapMode = TextureWrapMode.Clamp;
            cubemapFace.enableRandomWrite = true;
            cubemapFace.isPowerOfTwo = true;
            cubemapFace.Create();











            CommandBuffer precomputeSkyboxVisibilityCommandBuffer = new CommandBuffer();

            List<SceneMesh> sceneMeshes = GetSceneMeshes();

            string skyboxVisibilityXPOS_assetPath = string.Format("Assets/ImprovedCubemapRendering/RealtimeCubemapRenderingV4/Data/SkyboxVisibility_XPOS_{0}_{1}.asset", SceneManager.GetActiveScene().name, gameObject.name);
            string skyboxVisibilityXNEG_assetPath = string.Format("Assets/ImprovedCubemapRendering/RealtimeCubemapRenderingV4/Data/SkyboxVisibility_XNEG_{0}_{1}.asset", SceneManager.GetActiveScene().name, gameObject.name);
            string skyboxVisibilityYPOS_assetPath = string.Format("Assets/ImprovedCubemapRendering/RealtimeCubemapRenderingV4/Data/SkyboxVisibility_YPOS_{0}_{1}.asset", SceneManager.GetActiveScene().name, gameObject.name);
            string skyboxVisibilityYNEG_assetPath = string.Format("Assets/ImprovedCubemapRendering/RealtimeCubemapRenderingV4/Data/SkyboxVisibility_YNEG_{0}_{1}.asset", SceneManager.GetActiveScene().name, gameObject.name);
            string skyboxVisibilityZPOS_assetPath = string.Format("Assets/ImprovedCubemapRendering/RealtimeCubemapRenderingV4/Data/SkyboxVisibility_ZPOS_{0}_{1}.asset", SceneManager.GetActiveScene().name, gameObject.name);
            string skyboxVisibilityZNEG_assetPath = string.Format("Assets/ImprovedCubemapRendering/RealtimeCubemapRenderingV4/Data/SkyboxVisibility_ZNEG_{0}_{1}.asset", SceneManager.GetActiveScene().name, gameObject.name);

            Matrix4x4 skyboxVisibilityCameraLookMatrix;
            Matrix4x4 skyboxVisibilityCameraScaleMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1, -1, -1));
            Matrix4x4 skyboxVisibilityCameraViewPosition;
            Matrix4x4 skyboxVisibilityCameraProjection = Matrix4x4.Perspective(90.0f, 1.0f, reflectionProbe.nearClipPlane, reflectionProbe.farClipPlane);
            Vector3 skyboxVisibilityCameraPosition = reflectionProbe.transform.position + reflectionProbe.center;

            Material blackObjectMaterial = new Material(blackObjectShader);

            //|||||||||||||||||||||||||||||||||||||| RENDER CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //here we actually render the scene in 6 different axis
            //render the camera on a given orientation, then combine the result back into our final cubemap which is handled with the compute shader

            GL.invertCulling = true;

            //X Positive (X+)
            skyboxVisibilityCameraLookMatrix = Matrix4x4.LookAt(skyboxVisibilityCameraPosition, skyboxVisibilityCameraPosition + Vector3.right, Vector3.up);
            skyboxVisibilityCameraViewPosition = skyboxVisibilityCameraScaleMatrix * skyboxVisibilityCameraLookMatrix.inverse;
            precomputeSkyboxVisibilityCommandBuffer.Clear();
            precomputeSkyboxVisibilityCommandBuffer.SetRenderTarget(cubemapFace);
            precomputeSkyboxVisibilityCommandBuffer.SetViewProjectionMatrices(skyboxVisibilityCameraViewPosition, skyboxVisibilityCameraProjection);
            precomputeSkyboxVisibilityCommandBuffer.SetViewport(new Rect(0, 0, cubemapFace.width, cubemapFace.height));
            precomputeSkyboxVisibilityCommandBuffer.ClearRenderTarget(true, true, Color.white); //IMPORTANT: clear contents before we render a new frame

            for (int i = 0; i < sceneMeshes.Count; i++)
            {
                precomputeSkyboxVisibilityCommandBuffer.DrawMesh(sceneMeshes[i].mesh, sceneMeshes[i].localToWorldMatrix, blackObjectMaterial);
            }

            Graphics.ExecuteCommandBuffer(precomputeSkyboxVisibilityCommandBuffer);

            renderTextureConverter.SaveRenderTexture2DAsTexture2D(cubemapFace, string.Format("Assets/ImprovedCubemapRendering/RealtimeCubemapRenderingV4/Data/SkyboxVisibility_XPOS_{0}_{1}.asset", SceneManager.GetActiveScene().name, gameObject.name));

            //X Negative (X-)
            skyboxVisibilityCameraLookMatrix = Matrix4x4.LookAt(skyboxVisibilityCameraPosition, skyboxVisibilityCameraPosition + Vector3.left, Vector3.up);
            skyboxVisibilityCameraViewPosition = skyboxVisibilityCameraScaleMatrix * skyboxVisibilityCameraLookMatrix.inverse;
            precomputeSkyboxVisibilityCommandBuffer.Clear();
            precomputeSkyboxVisibilityCommandBuffer.SetRenderTarget(cubemapFace);
            precomputeSkyboxVisibilityCommandBuffer.SetViewProjectionMatrices(skyboxVisibilityCameraViewPosition, skyboxVisibilityCameraProjection);
            precomputeSkyboxVisibilityCommandBuffer.SetViewport(new Rect(0, 0, cubemapFace.width, cubemapFace.height));
            precomputeSkyboxVisibilityCommandBuffer.ClearRenderTarget(true, true, Color.white); //IMPORTANT: clear contents before we render a new frame

            for (int i = 0; i < sceneMeshes.Count; i++)
            {
                precomputeSkyboxVisibilityCommandBuffer.DrawMesh(sceneMeshes[i].mesh, sceneMeshes[i].localToWorldMatrix, blackObjectMaterial);
            }

            Graphics.ExecuteCommandBuffer(precomputeSkyboxVisibilityCommandBuffer);

            renderTextureConverter.SaveRenderTexture2DAsTexture2D(cubemapFace, string.Format("Assets/ImprovedCubemapRendering/RealtimeCubemapRenderingV4/Data/SkyboxVisibility_XNEG_{0}_{1}.asset", SceneManager.GetActiveScene().name, gameObject.name));

            //Y Positive (Y+)
            skyboxVisibilityCameraLookMatrix = Matrix4x4.LookAt(skyboxVisibilityCameraPosition, skyboxVisibilityCameraPosition + Vector3.up, Vector3.back);
            skyboxVisibilityCameraViewPosition = skyboxVisibilityCameraScaleMatrix * skyboxVisibilityCameraLookMatrix.inverse;
            precomputeSkyboxVisibilityCommandBuffer.Clear();
            precomputeSkyboxVisibilityCommandBuffer.SetRenderTarget(cubemapFace);
            precomputeSkyboxVisibilityCommandBuffer.SetViewProjectionMatrices(skyboxVisibilityCameraViewPosition, skyboxVisibilityCameraProjection);
            precomputeSkyboxVisibilityCommandBuffer.SetViewport(new Rect(0, 0, cubemapFace.width, cubemapFace.height));
            precomputeSkyboxVisibilityCommandBuffer.ClearRenderTarget(true, true, Color.white); //IMPORTANT: clear contents before we render a new frame

            for (int i = 0; i < sceneMeshes.Count; i++)
            {
                precomputeSkyboxVisibilityCommandBuffer.DrawMesh(sceneMeshes[i].mesh, sceneMeshes[i].localToWorldMatrix, blackObjectMaterial);
            }

            Graphics.ExecuteCommandBuffer(precomputeSkyboxVisibilityCommandBuffer);

            renderTextureConverter.SaveRenderTexture2DAsTexture2D(cubemapFace, string.Format("Assets/ImprovedCubemapRendering/RealtimeCubemapRenderingV4/Data/SkyboxVisibility_YPOS_{0}_{1}.asset", SceneManager.GetActiveScene().name, gameObject.name));

            //Y Negative (Y-)
            skyboxVisibilityCameraLookMatrix = Matrix4x4.LookAt(skyboxVisibilityCameraPosition, skyboxVisibilityCameraPosition + Vector3.down, Vector3.forward);
            skyboxVisibilityCameraViewPosition = skyboxVisibilityCameraScaleMatrix * skyboxVisibilityCameraLookMatrix.inverse;
            precomputeSkyboxVisibilityCommandBuffer.Clear();
            precomputeSkyboxVisibilityCommandBuffer.SetRenderTarget(cubemapFace);
            precomputeSkyboxVisibilityCommandBuffer.SetViewProjectionMatrices(skyboxVisibilityCameraViewPosition, skyboxVisibilityCameraProjection);
            precomputeSkyboxVisibilityCommandBuffer.SetViewport(new Rect(0, 0, cubemapFace.width, cubemapFace.height));
            precomputeSkyboxVisibilityCommandBuffer.ClearRenderTarget(true, true, Color.white); //IMPORTANT: clear contents before we render a new frame

            for (int i = 0; i < sceneMeshes.Count; i++)
            {
                precomputeSkyboxVisibilityCommandBuffer.DrawMesh(sceneMeshes[i].mesh, sceneMeshes[i].localToWorldMatrix, blackObjectMaterial);
            }

            Graphics.ExecuteCommandBuffer(precomputeSkyboxVisibilityCommandBuffer);

            renderTextureConverter.SaveRenderTexture2DAsTexture2D(cubemapFace, string.Format("Assets/ImprovedCubemapRendering/RealtimeCubemapRenderingV4/Data/SkyboxVisibility_YNEG_{0}_{1}.asset", SceneManager.GetActiveScene().name, gameObject.name));

            //Z Positive (Z+)
            skyboxVisibilityCameraLookMatrix = Matrix4x4.LookAt(skyboxVisibilityCameraPosition, skyboxVisibilityCameraPosition + Vector3.forward, Vector3.up);
            skyboxVisibilityCameraViewPosition = skyboxVisibilityCameraScaleMatrix * skyboxVisibilityCameraLookMatrix.inverse;
            precomputeSkyboxVisibilityCommandBuffer.Clear();
            precomputeSkyboxVisibilityCommandBuffer.SetRenderTarget(cubemapFace);
            precomputeSkyboxVisibilityCommandBuffer.SetViewProjectionMatrices(skyboxVisibilityCameraViewPosition, skyboxVisibilityCameraProjection);
            precomputeSkyboxVisibilityCommandBuffer.SetViewport(new Rect(0, 0, cubemapFace.width, cubemapFace.height));
            precomputeSkyboxVisibilityCommandBuffer.ClearRenderTarget(true, true, Color.white); //IMPORTANT: clear contents before we render a new frame

            for (int i = 0; i < sceneMeshes.Count; i++)
            {
                precomputeSkyboxVisibilityCommandBuffer.DrawMesh(sceneMeshes[i].mesh, sceneMeshes[i].localToWorldMatrix, blackObjectMaterial);
            }

            Graphics.ExecuteCommandBuffer(precomputeSkyboxVisibilityCommandBuffer);

            renderTextureConverter.SaveRenderTexture2DAsTexture2D(cubemapFace, string.Format("Assets/ImprovedCubemapRendering/RealtimeCubemapRenderingV4/Data/SkyboxVisibility_ZPOS_{0}_{1}.asset", SceneManager.GetActiveScene().name, gameObject.name));

            //Z Negative (Z-)
            skyboxVisibilityCameraLookMatrix = Matrix4x4.LookAt(skyboxVisibilityCameraPosition, skyboxVisibilityCameraPosition + Vector3.back, Vector3.up);
            skyboxVisibilityCameraViewPosition = skyboxVisibilityCameraScaleMatrix * skyboxVisibilityCameraLookMatrix.inverse;
            precomputeSkyboxVisibilityCommandBuffer.Clear();
            precomputeSkyboxVisibilityCommandBuffer.SetRenderTarget(cubemapFace);
            precomputeSkyboxVisibilityCommandBuffer.SetViewProjectionMatrices(skyboxVisibilityCameraViewPosition, skyboxVisibilityCameraProjection);
            precomputeSkyboxVisibilityCommandBuffer.SetViewport(new Rect(0, 0, cubemapFace.width, cubemapFace.height));
            precomputeSkyboxVisibilityCommandBuffer.ClearRenderTarget(true, true, Color.white); //IMPORTANT: clear contents before we render a new frame
            precomputeSkyboxVisibilityCommandBuffer.SetInvertCulling(true);

            for (int i = 0; i < sceneMeshes.Count; i++)
            {
                precomputeSkyboxVisibilityCommandBuffer.DrawMesh(sceneMeshes[i].mesh, sceneMeshes[i].localToWorldMatrix, blackObjectMaterial);
            }

            Graphics.ExecuteCommandBuffer(precomputeSkyboxVisibilityCommandBuffer);

            renderTextureConverter.SaveRenderTexture2DAsTexture2D(cubemapFace, string.Format("Assets/ImprovedCubemapRendering/RealtimeCubemapRenderingV4/Data/SkyboxVisibility_ZNEG_{0}_{1}.asset", SceneManager.GetActiveScene().name, gameObject.name));

            GL.invertCulling = false;

            skyboxVisibilityXPOS = AssetDatabase.LoadAssetAtPath<Texture2D>(skyboxVisibilityXPOS_assetPath);
            skyboxVisibilityXNEG = AssetDatabase.LoadAssetAtPath<Texture2D>(skyboxVisibilityXNEG_assetPath);
            skyboxVisibilityYPOS = AssetDatabase.LoadAssetAtPath<Texture2D>(skyboxVisibilityYPOS_assetPath);
            skyboxVisibilityYNEG = AssetDatabase.LoadAssetAtPath<Texture2D>(skyboxVisibilityYNEG_assetPath);
            skyboxVisibilityZPOS = AssetDatabase.LoadAssetAtPath<Texture2D>(skyboxVisibilityZPOS_assetPath);
            skyboxVisibilityZNEG = AssetDatabase.LoadAssetAtPath<Texture2D>(skyboxVisibilityZNEG_assetPath);

            cubemapFace.Release();
            precomputeSkyboxVisibilityCommandBuffer.Release();

            DestroyImmediate(blackObjectMaterial);
        }

        private List<SceneMesh> GetSceneMeshes()
        {
            List<SceneMesh> sceneMeshes = new List<SceneMesh>();

            MeshFilter[] meshFiltersInScene = FindObjectsByType<MeshFilter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            for (int i = 0; i < meshFiltersInScene.Length; i++)
            {
                //bool includeMesh = GameObjectUtility.GetStaticEditorFlags(meshFiltersInScene[i].gameObject).HasFlag(StaticEditorFlags.ContributeGI);

                bool includeMesh = true;
                meshFiltersInScene[i].TryGetComponent<MeshRenderer>(out MeshRenderer meshRenderer);

                if (includeMesh)
                {
                    SceneMesh sceneMesh = new SceneMesh();
                    sceneMesh.mesh = meshFiltersInScene[i].sharedMesh;
                    sceneMesh.localToWorldMatrix = meshFiltersInScene[i].transform.localToWorldMatrix;
                    sceneMeshes.Add(sceneMesh);
                }
            }

            return sceneMeshes;
        }
    }
}