using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;

using UnityEngine;
using UnityEngine.SceneManagement;

namespace ImprovedCubemapRendering
{
    public class RealtimeCubemapRenderingV1 : MonoBehaviour
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

        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||

        [Header("Setup")]
        public ComputeShader cubemapRenderingCompute;

        [Header("Properties")]
        public RealtimeCubemapTextureFormatType formatType = RealtimeCubemapTextureFormatType.RGBAHalf;
        public int updateFPS = 30;

        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||

        private float nextUpdateInterval;

        private ReflectionProbe reflectionProbe;

        private RenderTexture probeCameraRender;
        private RenderTexture cubemapRender;
        private RenderTexture finalCubemap;

        private GameObject probeCameraGameObject;
        private Camera probeCamera;

        private RenderTextureConverter renderTextureConverter;

        private static int renderTargetDepthBits = 32; //0 16 24 32

        private int computeShaderKernelCubemapCombine;
        private uint computeShaderThreadGroupSizeX = 0;
        private uint computeShaderThreadGroupSizeY = 0;
        private uint computeShaderThreadGroupSizeZ = 0;

        private bool isSetup;

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
            RenderRealtimeCubemap();
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
            //setup our render texture converter class so we can convert render textures to texture2D objects efficently/easily
            if (renderTextureConverter == null)
                renderTextureConverter = new RenderTextureConverter();

            //get our compute shader manually if for whatever reason it wasn't assigned
            if (cubemapRenderingCompute == null)
                cubemapRenderingCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/ImprovedCubemapRendering/RealtimeCubemapRenderingV1/RealtimeCubemapRenderingV1.compute");

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
            probeCamera.fieldOfView = 90.0f; //90 degree FOV is important and required to render each of the 6 faces
            probeCamera.nearClipPlane = reflectionProbe.nearClipPlane;
            probeCamera.farClipPlane = reflectionProbe.farClipPlane;
            probeCamera.backgroundColor = reflectionProbe.backgroundColor;
        }

        /// <summary>
        /// Setup to handle realtime rendering
        /// </summary>
        private void SetupRealtimeRendering()
        {
            //if we have these render targets still around, make sure we clean it up before we start
            CleanupRealtimeRendering();

            //start with no reflection data in the scene (at least on meshes within bounds of this reflection probe)
            //NOTE: Not implemented here, but if you want multi-bounce static reflections, we could just feed the previous render target here and reflections will naturally get recursively captured.
            reflectionProbe.customBakedTexture = null;

            //NOTE: Since there is no native "RWTextureCube" we use a Tex2DArray with 6 slices which is similar to a cubemap setup.
            cubemapRender = new RenderTexture(reflectionProbe.resolution, reflectionProbe.resolution, renderTargetDepthBits, GetRenderTextureFormatType(formatType));
            cubemapRender.filterMode = FilterMode.Trilinear;
            cubemapRender.wrapMode = TextureWrapMode.Clamp;
            cubemapRender.volumeDepth = 6; //6 faces in cubemap
            cubemapRender.dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray;
            cubemapRender.enableRandomWrite = true;
            cubemapRender.isPowerOfTwo = true;
            cubemapRender.Create();

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

            //create a regular 2D render target for the camera
            probeCameraRender = new RenderTexture(reflectionProbe.resolution, reflectionProbe.resolution, renderTargetDepthBits, GetRenderTextureFormatType(formatType));
            probeCameraRender.filterMode = FilterMode.Trilinear;
            probeCameraRender.wrapMode = TextureWrapMode.Clamp;
            probeCameraRender.enableRandomWrite = true;
            probeCameraRender.isPowerOfTwo = true;
            probeCameraRender.Create();

            //feed the camera our render target so whatever it renders goes into our own render target
            probeCamera.targetTexture = probeCameraRender;

            //feed the reflection probe our final cubemap also (which will be updated)
            //the nature of this also being realtime means that we will recursively get reflection bounces anyway for free!
            reflectionProbe.customBakedTexture = finalCubemap;

            //get some data from the compute shader once (they don't change, no reason to get them every frame anyway)
            computeShaderKernelCubemapCombine = cubemapRenderingCompute.FindKernel("CubemapCombine");
            cubemapRenderingCompute.GetKernelThreadGroupSizes(computeShaderKernelCubemapCombine, out computeShaderThreadGroupSizeX, out computeShaderThreadGroupSizeY, out computeShaderThreadGroupSizeZ);

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
        }

        /// <summary>
        /// Cleanup of realtime rendering resources
        /// </summary>
        private void CleanupRealtimeRendering()
        {
            if(probeCameraRender != null && probeCameraRender.IsCreated())
                probeCameraRender.Release();

            if (cubemapRender != null && cubemapRender.IsCreated())
                cubemapRender.Release();

            if (finalCubemap != null && finalCubemap.IsCreated())
                finalCubemap.Release();

            isSetup = false;
        }

        //|||||||||||||||||||||||||||||||||||||| RENDER REALTIME CUBEMAP ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| RENDER REALTIME CUBEMAP ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| RENDER REALTIME CUBEMAP ||||||||||||||||||||||||||||||||||||||

        public void RenderRealtimeCubemap()
        {
            if (!isSetup)
                return;

            if (Time.time < nextUpdateInterval)
                return;

            //|||||||||||||||||||||||||||||||||||||| RENDER CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //here we actually render the scene in 6 different axis
            //render the camera on a given orientation, then combine the result back into our final cubemap which is handled with the compute shader

            //X Positive (X+)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.right, Vector3.up);
            probeCamera.Render();
            cubemapRenderingCompute.SetInt("CubemapFaceIndex", 0);
            cubemapRenderingCompute.SetInt("CubemapFaceResolution", reflectionProbe.resolution);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "SceneRender", probeCameraRender);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "CubemapResult", cubemapRender);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, Mathf.CeilToInt(cubemapRender.width / computeShaderThreadGroupSizeX), Mathf.CeilToInt(cubemapRender.width / computeShaderThreadGroupSizeY), 1);

            //X Negative (X-)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.left, Vector3.up);
            probeCamera.Render();
            cubemapRenderingCompute.SetInt("CubemapFaceIndex", 1);
            cubemapRenderingCompute.SetInt("CubemapFaceResolution", reflectionProbe.resolution);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "SceneRender", probeCameraRender);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "CubemapResult", cubemapRender);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, Mathf.CeilToInt(cubemapRender.width / computeShaderThreadGroupSizeX), Mathf.CeilToInt(cubemapRender.width / computeShaderThreadGroupSizeY), 1);

            //Y Positive (Y+)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.down);
            probeCamera.Render();
            cubemapRenderingCompute.SetInt("CubemapFaceIndex", 2);
            cubemapRenderingCompute.SetInt("CubemapFaceResolution", reflectionProbe.resolution);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "SceneRender", probeCameraRender);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "CubemapResult", cubemapRender);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, Mathf.CeilToInt(cubemapRender.width / computeShaderThreadGroupSizeX), Mathf.CeilToInt(cubemapRender.width / computeShaderThreadGroupSizeY), 1);

            //Y Negative (Y-)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.down);
            probeCamera.Render();
            cubemapRenderingCompute.SetInt("CubemapFaceIndex", 3);
            cubemapRenderingCompute.SetInt("CubemapFaceResolution", reflectionProbe.resolution);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "SceneRender", probeCameraRender);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "CubemapResult", cubemapRender);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, Mathf.CeilToInt(cubemapRender.width / computeShaderThreadGroupSizeX), Mathf.CeilToInt(cubemapRender.width / computeShaderThreadGroupSizeY), 1);

            //Z Positive (Z+)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
            probeCamera.Render();
            cubemapRenderingCompute.SetInt("CubemapFaceIndex", 4);
            cubemapRenderingCompute.SetInt("CubemapFaceResolution", reflectionProbe.resolution);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "SceneRender", probeCameraRender);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "CubemapResult", cubemapRender);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, Mathf.CeilToInt(cubemapRender.width / computeShaderThreadGroupSizeX), Mathf.CeilToInt(cubemapRender.width / computeShaderThreadGroupSizeY), 1);

            //Z Negative (Z-)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.back, Vector3.up);
            probeCamera.Render();
            cubemapRenderingCompute.SetInt("CubemapFaceIndex", 5);
            cubemapRenderingCompute.SetInt("CubemapFaceResolution", reflectionProbe.resolution);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "SceneRender", probeCameraRender);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "CubemapResult", cubemapRender);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, Mathf.CeilToInt(cubemapRender.width / computeShaderThreadGroupSizeX), Mathf.CeilToInt(cubemapRender.width / computeShaderThreadGroupSizeY), 1);

            //|||||||||||||||||||||||||||||||||||||| CONVERT COMBINED CUBEMAP INTO ACTUAL USABLE CUBEMAP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| CONVERT COMBINED CUBEMAP INTO ACTUAL USABLE CUBEMAP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| CONVERT COMBINED CUBEMAP INTO ACTUAL USABLE CUBEMAP ||||||||||||||||||||||||||||||||||||||

            //then to transfer the data as efficently as possible, we use Graphics.CopyTexture to copy each slice into the cubemap!
            //after this then we have a render texture cubemap that we just wrote into!
            Graphics.CopyTexture(cubemapRender, 0, 0, finalCubemap, 0, 0);
            Graphics.CopyTexture(cubemapRender, 1, 0, finalCubemap, 1, 0);
            Graphics.CopyTexture(cubemapRender, 2, 0, finalCubemap, 2, 0);
            Graphics.CopyTexture(cubemapRender, 3, 0, finalCubemap, 3, 0);
            Graphics.CopyTexture(cubemapRender, 4, 0, finalCubemap, 4, 0);
            Graphics.CopyTexture(cubemapRender, 5, 0, finalCubemap, 5, 0);

            //generate mips so PBR shaders can sample a slightly blurrier version of the reflection cubemap
            //IMPORTANT NOTE: this is not PBR compliant, PBR shaders in unity (and most engines if configured as such) actually need a special mip map setup for reflection cubemaps (specular convolution)
            //so what actually comes from this is not correct nor should it be used (if you really really really have no other choice I suppose you can)
            //with that said in a later version of this we do use a proper specular convolution setup, but this is here just for illustrative/simplicity purposes
            finalCubemap.GenerateMips();

            //update next time interval
            nextUpdateInterval = Time.time + (1.0f / updateFPS);
        }

        //|||||||||||||||||||||||||||||||||||||| EDITOR ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| EDITOR ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| EDITOR ||||||||||||||||||||||||||||||||||||||

        [ContextMenu("RenderRealtimeCubemapOnce")]
        public void RenderRealtimeCubemapOnce()
        {
            CleanupRendering();

            Setup();
            SetupRealtimeRendering();
            RenderRealtimeCubemap();

            string unityAssetPath = string.Format("Assets/ImprovedCubemapRendering/RealtimeCubemapRenderingV1/Data/{0}_{1}.asset", SceneManager.GetActiveScene().name, gameObject.name);

            AssetDatabase.DeleteAsset(unityAssetPath);
            AssetDatabase.CreateAsset(finalCubemap, unityAssetPath);
        }

        [ContextMenu("CleanupRendering")]
        public void CleanupRendering()
        {
            CleanupRealtimeRendering();
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
    }
}