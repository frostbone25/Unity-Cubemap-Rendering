#if UNITY_EDITOR
using UnityEditor;
#endif

using UnityEngine;
using UnityEngine.SceneManagement;

//REFERENCE - https://discussions.unity.com/t/specular-convolution-when-calculating-mip-maps-for-cubemap-render-texture/729652/15
//The implemntation here is not exact or 1:1 to what unity does natively for specular convolution
//I compared the custom convolution here against the native realtime reflection it looks identical at high sample counts.
//The exact reconstructed unity specular convolution notes from that thread also are incomplete and not fully accurate (some of the provided code snippets reference functions that were not provided)
//In addition as well, the reconstruction of unity's specular convolution process is also overly complicated for what it should be, so I went the more simple route.
//Also while having a peek at the native unity shaders used potentially for realtime specular convolution... it looks like gaussian blurs oddly enough.
//So I opted instead for a more normal/proper approach of convolving with GGX to be compliant with most PBR shaders
//The beauty here also is in the future we can swap out the GGX sampling with a different specular BDRF if your main object shaders in the scene use a different model

namespace ImprovedCubemapRendering
{
    public class RealtimeCubemapRenderingV3 : MonoBehaviour
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
        public ComputeShader cubemapRenderingCompute;

        [Header("Properties")]
        public RealtimeCubemapTextureFormatType formatType = RealtimeCubemapTextureFormatType.RGBAHalf;
        public bool update = true;
        public int updateFPS = 30;
        public int GGXSpecularConvolutionSamples = 256;

        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||

        private ReflectionProbe reflectionProbe;

        private GameObject probeCameraGameObject;
        private Camera probeCamera;

        //NOTE: I would like to explore potentially using less render textures here to save on memory
        //whittling it down to atleast 2 would be ideal (camera render target, cubemap)
        private RenderTexture probeCameraRender;
        private RenderTexture intermediateCubemap;
        private RenderTexture convolvedCubemap;
        private RenderTexture finalCubemap;

        private static int renderTargetDepthBits = 32; //0 16 24 32

        private Quaternion probeCameraRotationXPOS;
        private Quaternion probeCameraRotationXNEG;
        private Quaternion probeCameraRotationYPOS;
        private Quaternion probeCameraRotationYNEG;
        private Quaternion probeCameraRotationZPOS;
        private Quaternion probeCameraRotationZNEG;

        private int computeShaderKernelCubemapCombine;
        private int computeShaderKernelConvolveSpecularGGX;
        private int computeShaderThreadGroupSizeX = 0;
        private int computeShaderThreadGroupSizeY = 0;
        private int computeShaderThreadGroupSizeZ = 0;

        private MipLevel[] mipLevels;

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
                cubemapRenderingCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/ImprovedCubemapRendering/RealtimeCubemapRenderingV3/RealtimeCubemapRenderingV3.compute");
#endif

            //if there is no compute shader period, we are in trouble and we can't continue!
            //the compute shader is needed so we can flip the render target so that the faces show up correctly on the final cubemap!
            if (cubemapRenderingCompute == null)
            {
                isSetup = false;
                return;
            }

            //setup main gameobject that will hold our camera that will render the scene
            //and make sure it's placed right where the reflection probe capture point is supposed to be
            probeCameraGameObject = new GameObject("probeCameraGameObject");
            probeCameraGameObject.transform.position = reflectionProbe.transform.position;
            probeCameraGameObject.transform.position += reflectionProbe.center;

            //add the camera and match all of the coresponding settings from the reflection probe to our camera
            probeCamera = probeCameraGameObject.AddComponent<Camera>();
            probeCamera.forceIntoRenderTexture = true;
            probeCamera.fieldOfView = 90.0f; //90 degree FOV is important and required to render each of the 6 faces
            probeCamera.nearClipPlane = reflectionProbe.nearClipPlane;
            probeCamera.farClipPlane = reflectionProbe.farClipPlane;
            probeCamera.backgroundColor = reflectionProbe.backgroundColor;
            probeCamera.cullingMask = reflectionProbe.cullingMask;

            //precompute orientations (no reason to recompute these every frame, they won't change!)
            probeCameraRotationXPOS = Quaternion.LookRotation(Vector3.right, Vector3.up);
            probeCameraRotationXNEG = Quaternion.LookRotation(Vector3.left, Vector3.up);
            probeCameraRotationYPOS = Quaternion.LookRotation(Vector3.up, Vector3.down);
            probeCameraRotationYNEG = Quaternion.LookRotation(Vector3.down, Vector3.down);
            probeCameraRotationZPOS = Quaternion.LookRotation(Vector3.forward, Vector3.up);
            probeCameraRotationZNEG = Quaternion.LookRotation(Vector3.back, Vector3.up);

            //|||||||||||||||||||||||||||||||||||||| SETUP - RENDER TARGETS ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SETUP - RENDER TARGETS ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SETUP - RENDER TARGETS ||||||||||||||||||||||||||||||||||||||

            //create a regular 2D render target for the camera
            probeCameraRender = new RenderTexture(reflectionProbe.resolution, reflectionProbe.resolution, renderTargetDepthBits, GetRenderTextureFormatType(formatType));
            probeCameraRender.filterMode = FilterMode.Trilinear;
            probeCameraRender.wrapMode = TextureWrapMode.Clamp;
            probeCameraRender.enableRandomWrite = true;
            probeCameraRender.isPowerOfTwo = true;
            probeCameraRender.Create();

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

            //NOTE: Since there is no native "RWTextureCube" we use a Tex2DArray with 6 slices which is similar to a cubemap setup.
            convolvedCubemap = new RenderTexture(reflectionProbe.resolution, reflectionProbe.resolution, renderTargetDepthBits, GetRenderTextureFormatType(formatType));
            convolvedCubemap.filterMode = FilterMode.Trilinear;
            convolvedCubemap.wrapMode = TextureWrapMode.Clamp;
            convolvedCubemap.volumeDepth = 6; //6 faces in cubemap
            convolvedCubemap.dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray;
            convolvedCubemap.enableRandomWrite = true;
            convolvedCubemap.isPowerOfTwo = true;
            convolvedCubemap.useMipMap = true;
            convolvedCubemap.autoGenerateMips = false;
            convolvedCubemap.Create();

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

            //feed the camera our render target so whatever it renders goes into our own render target
            probeCamera.targetTexture = probeCameraRender;

            //feed the reflection probe our final cubemap also (which will be updated)
            //the nature of this also being realtime means that we will recursively get reflection bounces anyway for free!
            reflectionProbe.customBakedTexture = finalCubemap;

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
                cubemapRenderingCompute.GetKernelThreadGroupSizes(computeShaderKernelCubemapCombine, out uint mipThreadGroupSizeX, out uint mipThreadGroupSizeY, out uint mipThreadGroupSizeZ);

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

            cubemapRenderingCompute.SetInt(RealtimeCubemapRenderingShaderIDsV3.CubemapFaceResolution, reflectionProbe.resolution);
            cubemapRenderingCompute.SetInt(RealtimeCubemapRenderingShaderIDsV3.SpecularConvolutionSamples, GGXSpecularConvolutionSamples);

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
            //remove our main camera gameobject (which will get rid of the camera)
            if (probeCameraGameObject != null)
                DestroyImmediate(probeCameraGameObject);

            //make sure these references are gone
            probeCameraGameObject = null;
            probeCamera = null;

            if (probeCameraRender != null && probeCameraRender.IsCreated())
                probeCameraRender.Release();

            if (intermediateCubemap != null && intermediateCubemap.IsCreated())
                intermediateCubemap.Release();

            if (convolvedCubemap != null && convolvedCubemap.IsCreated())
                convolvedCubemap.Release();

            if (finalCubemap != null && finalCubemap.IsCreated())
                finalCubemap.Release();

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
            if (Time.time < nextUpdateInterval && update)
                return;

            //|||||||||||||||||||||||||||||||||||||| RENDER CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //here we actually render the scene in 6 different axis
            //render the camera on a given orientation, then combine the result back into our final cubemap which is handled with the compute shader

            //X Positive (X+)
            //rotate and render view
            probeCameraGameObject.transform.rotation = probeCameraRotationXPOS;
            probeCamera.Render();

            //flip render target and combine into intermediate cubemap
            cubemapRenderingCompute.SetInt(RealtimeCubemapRenderingShaderIDsV3.CubemapFaceIndex, 0);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, RealtimeCubemapRenderingShaderIDsV3.CubemapFace, probeCameraRender);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, RealtimeCubemapRenderingShaderIDsV3.CubemapOutput, intermediateCubemap);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, computeShaderThreadGroupSizeX, computeShaderThreadGroupSizeY, computeShaderThreadGroupSizeZ);

            //X Negative (X-)
            //rotate and render view
            probeCameraGameObject.transform.rotation = probeCameraRotationXNEG;
            probeCamera.Render();

            //flip render target and combine into intermediate cubemap
            cubemapRenderingCompute.SetInt(RealtimeCubemapRenderingShaderIDsV3.CubemapFaceIndex, 1);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, RealtimeCubemapRenderingShaderIDsV3.CubemapFace, probeCameraRender);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, RealtimeCubemapRenderingShaderIDsV3.CubemapOutput, intermediateCubemap);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, computeShaderThreadGroupSizeX, computeShaderThreadGroupSizeY, computeShaderThreadGroupSizeZ);

            //Y Positive (Y+)
            //rotate and render view
            probeCameraGameObject.transform.rotation = probeCameraRotationYPOS;
            probeCamera.Render();

            //flip render target and combine into intermediate cubemap
            cubemapRenderingCompute.SetInt(RealtimeCubemapRenderingShaderIDsV3.CubemapFaceIndex, 2);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, RealtimeCubemapRenderingShaderIDsV3.CubemapFace, probeCameraRender);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, RealtimeCubemapRenderingShaderIDsV3.CubemapOutput, intermediateCubemap);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, computeShaderThreadGroupSizeX, computeShaderThreadGroupSizeY, computeShaderThreadGroupSizeZ);

            //Y Negative (Y-)
            //rotate and render view
            probeCameraGameObject.transform.rotation = probeCameraRotationYNEG;
            probeCamera.Render();

            //flip render target and combine into intermediate cubemap
            cubemapRenderingCompute.SetInt(RealtimeCubemapRenderingShaderIDsV3.CubemapFaceIndex, 3);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, RealtimeCubemapRenderingShaderIDsV3.CubemapFace, probeCameraRender);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, RealtimeCubemapRenderingShaderIDsV3.CubemapOutput, intermediateCubemap);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, computeShaderThreadGroupSizeX, computeShaderThreadGroupSizeY, computeShaderThreadGroupSizeZ);

            //Z Positive (Z+)
            //rotate and render view
            probeCameraGameObject.transform.rotation = probeCameraRotationZPOS;
            probeCamera.Render();

            //flip render target and combine into intermediate cubemap
            cubemapRenderingCompute.SetInt(RealtimeCubemapRenderingShaderIDsV3.CubemapFaceIndex, 4);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, RealtimeCubemapRenderingShaderIDsV3.CubemapFace, probeCameraRender);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, RealtimeCubemapRenderingShaderIDsV3.CubemapOutput, intermediateCubemap);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, computeShaderThreadGroupSizeX, computeShaderThreadGroupSizeY, computeShaderThreadGroupSizeZ);

            //Z Negative (Z-)
            //rotate and render view
            probeCameraGameObject.transform.rotation = probeCameraRotationZNEG;
            probeCamera.Render();

            //flip render target and combine into intermediate cubemap
            cubemapRenderingCompute.SetInt(RealtimeCubemapRenderingShaderIDsV3.CubemapFaceIndex, 5);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, RealtimeCubemapRenderingShaderIDsV3.CubemapFace, probeCameraRender);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, RealtimeCubemapRenderingShaderIDsV3.CubemapOutput, intermediateCubemap);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, computeShaderThreadGroupSizeX, computeShaderThreadGroupSizeY, computeShaderThreadGroupSizeZ);

            //generate mips so PBR shaders can sample a slightly blurrier version of the reflection cubemap
            //IMPORTANT NOTE: this is not PBR compliant, PBR shaders in unity (and most engines if configured as such) actually need a special mip map setup for reflection cubemaps (specular convolution)
            //so what actually comes from this is not correct nor should it be used (if you really really really have no other choice I suppose you can)
            //with that said in a later version of this we do use a proper specular convolution setup, but this is here just for illustrative/simplicity purposes
            intermediateCubemap.GenerateMips();

            //|||||||||||||||||||||||||||||||||||||| SPECULAR CONVOLVE CUBEMAP (TEX2DARRAY) ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SPECULAR CONVOLVE CUBEMAP (TEX2DARRAY) ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SPECULAR CONVOLVE CUBEMAP (TEX2DARRAY) ||||||||||||||||||||||||||||||||||||||

            //transfer mip 0 (this is done separately from the loop below as we do not want to blur it)
            //this saves us a little extra work since the first mip level should be the original reflection
            for (int face = 0; face < 6; face++)
            {
                Graphics.CopyTexture(intermediateCubemap, face, 0, convolvedCubemap, face, 0);
            }

            //for each cubemap face
            for (int face = 0; face < 6; face++)
            {
                //iterate for each mip level
                for (int mip = 1; mip < mipLevels.Length; mip++)
                {
                    MipLevel mipLevel = mipLevels[mip];

                    cubemapRenderingCompute.SetInt(RealtimeCubemapRenderingShaderIDsV3.CubemapFaceIndex, face);
                    cubemapRenderingCompute.SetInt(RealtimeCubemapRenderingShaderIDsV3.CubemapMipFaceResolution, mipLevel.mipLevelSquareResolution);
                    cubemapRenderingCompute.SetFloat(RealtimeCubemapRenderingShaderIDsV3.SpecularRoughness, mipLevel.roughnessLevel);
                    cubemapRenderingCompute.SetTexture(computeShaderKernelConvolveSpecularGGX, RealtimeCubemapRenderingShaderIDsV3.InputCubemap, intermediateCubemap, mip);
                    cubemapRenderingCompute.SetTexture(computeShaderKernelConvolveSpecularGGX, RealtimeCubemapRenderingShaderIDsV3.CubemapOutput, convolvedCubemap, mip);
                    cubemapRenderingCompute.Dispatch(computeShaderKernelConvolveSpecularGGX, mipLevel.computeShaderKernelThreadGroupSizeX, mipLevel.computeShaderKernelThreadGroupSizeY, mipLevel.computeShaderKernelThreadGroupSizeZ);
                }
            }
            //|||||||||||||||||||||||||||||||||||||| TRANSFER FINAL RESULTS TO PROPER CUBEMAP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| TRANSFER FINAL RESULTS TO PROPER CUBEMAP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| TRANSFER FINAL RESULTS TO PROPER CUBEMAP ||||||||||||||||||||||||||||||||||||||

            //then to transfer the data as efficently as possible, we use Graphics.CopyTexture to copy each slice into the cubemap!
            //after this then we have a render texture cubemap that we just wrote into!
            for (int i = 0; i < convolvedCubemap.mipmapCount; i++)
            {
                Graphics.CopyTexture(convolvedCubemap, 0, i, finalCubemap, 0, i);
                Graphics.CopyTexture(convolvedCubemap, 1, i, finalCubemap, 1, i);
                Graphics.CopyTexture(convolvedCubemap, 2, i, finalCubemap, 2, i);
                Graphics.CopyTexture(convolvedCubemap, 3, i, finalCubemap, 3, i);
                Graphics.CopyTexture(convolvedCubemap, 4, i, finalCubemap, 4, i);
                Graphics.CopyTexture(convolvedCubemap, 5, i, finalCubemap, 5, i);
            }

            //update next time interval
            //NOTE TO SELF: using Time.time in the long term might have precison issues later, would be prefered to switch this to double instead.
            nextUpdateInterval = Time.time + updateTime;
        }

        //|||||||||||||||||||||||||||||||||||||| EDITOR ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| EDITOR ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| EDITOR ||||||||||||||||||||||||||||||||||||||
#if UNITY_EDITOR

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