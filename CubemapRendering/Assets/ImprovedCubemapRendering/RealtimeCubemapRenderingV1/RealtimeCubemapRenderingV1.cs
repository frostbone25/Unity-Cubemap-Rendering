#if UNITY_EDITOR
using UnityEditor;
#endif

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

        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||

        [Header("Setup")]
        public ComputeShader cubemapRenderingCompute;

        [Header("Properties")]
        public RealtimeCubemapTextureFormatType formatType = RealtimeCubemapTextureFormatType.RGBAHalf;
        public UpdateType update = UpdateType.UpdateFPS;
        public int updateFPS = 30;

        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||

        private ReflectionProbe reflectionProbe;

        private GameObject probeCameraGameObject;
        private Camera probeCamera;

        //NOTE: it would be ideal to have to only need to deal with 2 render targets at minimum (camera render target, and cubemap)
        //however we can't write directly into a "cubemap" dimension render target, because there is no such thing as RWTextureCUBE.
        //not even a RWTexture2D array works with the "cubemap" dimension even though a cubemap is a Tex2DArray with 6 slices
        //unity complains that there is a mismatch of output texture dimension (expects 5, gets 4) so this is how we have to deal with it unfortunately
        private RenderTexture cubemapFaceRender;
        private RenderTexture cubemapFlippedFaceRender;
        private RenderTexture finalCubemap;

        private static int renderTargetDepthBits = 32; //0 16 24 32

        private Quaternion probeCameraRotationXPOS;
        private Quaternion probeCameraRotationXNEG;
        private Quaternion probeCameraRotationYPOS;
        private Quaternion probeCameraRotationYNEG;
        private Quaternion probeCameraRotationZPOS;
        private Quaternion probeCameraRotationZNEG;

        private int computeShaderKernelFlipRenderTarget;
        private int computeShaderThreadGroupSizeX = 0;
        private int computeShaderThreadGroupSizeY = 0;
        private int computeShaderThreadGroupSizeZ = 0;

        private bool isSetup;
        private bool isRealtimeRenderingSetup;

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
            if(update == UpdateType.UpdateEveryFrame || update == UpdateType.UpdateFPS)
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
#if UNITY_EDITOR
            //get our compute shader manually if for whatever reason it wasn't assigned
            if (cubemapRenderingCompute == null)
                cubemapRenderingCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/ImprovedCubemapRendering/RealtimeCubemapRenderingV1/RealtimeCubemapRenderingV1.compute");
#endif

            //if there is no compute shader period, we are in trouble and we can't continue!
            //the compute shader is needed so we can flip the render target so that the faces show up correctly on the final cubemap!
            if (cubemapRenderingCompute == null)
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
            cubemapFaceRender = new RenderTexture(reflectionProbe.resolution, reflectionProbe.resolution, renderTargetDepthBits, GetRenderTextureFormatType(formatType));
            cubemapFaceRender.filterMode = FilterMode.Trilinear;
            cubemapFaceRender.wrapMode = TextureWrapMode.Clamp;
            cubemapFaceRender.enableRandomWrite = true;
            cubemapFaceRender.isPowerOfTwo = true;
            cubemapFaceRender.Create();

            //create a regular 2D render target for the camera
            cubemapFlippedFaceRender = new RenderTexture(reflectionProbe.resolution, reflectionProbe.resolution, renderTargetDepthBits, GetRenderTextureFormatType(formatType));
            cubemapFlippedFaceRender.filterMode = FilterMode.Trilinear;
            cubemapFlippedFaceRender.wrapMode = TextureWrapMode.Clamp;
            cubemapFlippedFaceRender.enableRandomWrite = true;
            cubemapFlippedFaceRender.isPowerOfTwo = true;
            cubemapFlippedFaceRender.Create();

            //feed the camera our render target so whatever it renders goes into our own render target
            probeCamera.targetTexture = cubemapFaceRender;

            //feed the reflection probe our final cubemap also (which will be updated)
            //the nature of this also being realtime means that we will recursively get reflection bounces anyway for free!
            reflectionProbe.customBakedTexture = finalCubemap;

            //get some data from the compute shader once (they don't change, no reason to get them every frame anyway)
            computeShaderKernelFlipRenderTarget = cubemapRenderingCompute.FindKernel("FlipRenderTarget");

            //to save constantly needing to compute thread group sizes, we only need to do it once here because it doesn't change
            //the only time we need to change this is if the render target changes resolution, and in that case we just need to set things up again
            cubemapRenderingCompute.GetKernelThreadGroupSizes(computeShaderKernelFlipRenderTarget, out uint threadGroupSizeX, out uint threadGroupSizeY, out uint threadGroupSizeZ);
            computeShaderThreadGroupSizeX = Mathf.CeilToInt(cubemapFlippedFaceRender.width / threadGroupSizeX);
            computeShaderThreadGroupSizeY = Mathf.CeilToInt(cubemapFlippedFaceRender.width / threadGroupSizeY);
            computeShaderThreadGroupSizeZ = (int)threadGroupSizeZ;

            //set the render resolution once, don't need to do it every frame
            //again only time this needs to be updated is if resolutions change, and in that case we just need to set stuff up again
            cubemapRenderingCompute.SetInt(RealtimeCubemapRenderingShaderIDsV1.InputResolutionSquare, reflectionProbe.resolution);

            //set the textures for the kernels ahead of time, no need to set it every frame
            //the textures have random read/write enabled also so they will natrually get updated
            cubemapRenderingCompute.SetTexture(computeShaderKernelFlipRenderTarget, RealtimeCubemapRenderingShaderIDsV1.Input, cubemapFaceRender);
            cubemapRenderingCompute.SetTexture(computeShaderKernelFlipRenderTarget, RealtimeCubemapRenderingShaderIDsV1.Output, cubemapFlippedFaceRender);

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
            if(cubemapFaceRender != null && cubemapFaceRender.IsCreated())
                cubemapFaceRender.Release();

            if (cubemapFlippedFaceRender != null && cubemapFlippedFaceRender.IsCreated())
                cubemapFlippedFaceRender.Release();

            if (finalCubemap != null && finalCubemap.IsCreated())
                finalCubemap.Release();

            isRealtimeRenderingSetup = false;
        }

        //|||||||||||||||||||||||||||||||||||||| RENDER REALTIME CUBEMAP ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| RENDER REALTIME CUBEMAP ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| RENDER REALTIME CUBEMAP ||||||||||||||||||||||||||||||||||||||

        public void RenderRealtimeCubemap()
        {
            //if we are not setup, we can't render!
            if (!isSetup || !isRealtimeRenderingSetup)
                return;

            //if it's not our time to update, then don't render!
            if (Time.time < nextUpdateInterval && update == UpdateType.UpdateFPS)
                return;

            //|||||||||||||||||||||||||||||||||||||| RENDER AND COMBINE CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER AND COMBINE CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER AND COMBINE CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //here we actually render the scene in 6 different axis
            //render the camera on a given orientation, then combine the result back into our final cubemap which is handled with the compute shader

            //X Positive (X+)
            //rotate and render view
            probeCameraGameObject.transform.rotation = probeCameraRotationXPOS;
            probeCamera.Render();

            //flip render target
            cubemapRenderingCompute.Dispatch(computeShaderKernelFlipRenderTarget, computeShaderThreadGroupSizeX, computeShaderThreadGroupSizeY, computeShaderThreadGroupSizeZ);
            
            //copy final flipped render target to the main cubemap
            Graphics.CopyTexture(cubemapFlippedFaceRender, 0, 0, finalCubemap, 0, 0);

            //X Negative (X-)
            //rotate and render view
            probeCameraGameObject.transform.rotation = probeCameraRotationXNEG;
            probeCamera.Render();

            //flip render target
            cubemapRenderingCompute.Dispatch(computeShaderKernelFlipRenderTarget, computeShaderThreadGroupSizeX, computeShaderThreadGroupSizeY, computeShaderThreadGroupSizeZ);

            //copy final flipped render target to the main cubemap
            Graphics.CopyTexture(cubemapFlippedFaceRender, 0, 0, finalCubemap, 1, 0);

            //Y Positive (Y+)
            //rotate and render view
            probeCameraGameObject.transform.rotation = probeCameraRotationYPOS;
            probeCamera.Render();

            //flip render target
            cubemapRenderingCompute.Dispatch(computeShaderKernelFlipRenderTarget, computeShaderThreadGroupSizeX, computeShaderThreadGroupSizeY, computeShaderThreadGroupSizeZ);

            //copy final flipped render target to the main cubemap
            Graphics.CopyTexture(cubemapFlippedFaceRender, 0, 0, finalCubemap, 2, 0);

            //Y Negative (Y-)
            //rotate and render view
            probeCameraGameObject.transform.rotation = probeCameraRotationYNEG;
            probeCamera.Render();

            //flip render target
            cubemapRenderingCompute.Dispatch(computeShaderKernelFlipRenderTarget, computeShaderThreadGroupSizeX, computeShaderThreadGroupSizeY, computeShaderThreadGroupSizeZ);

            //copy final flipped render target to the main cubemap
            Graphics.CopyTexture(cubemapFlippedFaceRender, 0, 0, finalCubemap, 3, 0);

            //Z Positive (Z+)
            //rotate and render view
            probeCameraGameObject.transform.rotation = probeCameraRotationZPOS;
            probeCamera.Render();

            //flip render target
            cubemapRenderingCompute.Dispatch(computeShaderKernelFlipRenderTarget, computeShaderThreadGroupSizeX, computeShaderThreadGroupSizeY, computeShaderThreadGroupSizeZ);

            //copy final flipped render target to the main cubemap
            Graphics.CopyTexture(cubemapFlippedFaceRender, 0, 0, finalCubemap, 4, 0);

            //Z Negative (Z-)
            //rotate and render view
            probeCameraGameObject.transform.rotation = probeCameraRotationZNEG;
            probeCamera.Render();

            //flip render target
            cubemapRenderingCompute.Dispatch(computeShaderKernelFlipRenderTarget, computeShaderThreadGroupSizeX, computeShaderThreadGroupSizeY, computeShaderThreadGroupSizeZ);

            //copy final flipped render target to the main cubemap
            Graphics.CopyTexture(cubemapFlippedFaceRender, 0, 0, finalCubemap, 5, 0);

            //generate mips so PBR shaders can sample a slightly blurrier version of the reflection cubemap
            //IMPORTANT NOTE: this is not PBR compliant, PBR shaders in unity (and most engines if configured as such) actually need a special mip map setup for reflection cubemaps (specular convolution)
            //so what actually comes from this is not correct nor should it be used (if you really really really have no other choice I suppose you can)
            //with that said in a later version of this we do use a proper specular convolution setup, but this is here just for illustrative/simplicity purposes
            finalCubemap.GenerateMips();

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