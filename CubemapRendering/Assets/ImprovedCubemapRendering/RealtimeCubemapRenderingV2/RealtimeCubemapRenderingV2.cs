using UnityEditor;

using UnityEngine;
using UnityEngine.SceneManagement;

namespace ImprovedCubemapRendering
{
    public class RealtimeCubemapRenderingV2 : MonoBehaviour
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

        [Header("Properties")]
        public RealtimeCubemapTextureFormatType formatType = RealtimeCubemapTextureFormatType.RGBAHalf;
        public int updateFPS = 30;

        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||

        private float nextUpdateInterval;

        private ReflectionProbe reflectionProbe;

        private RenderTexture probeCameraRender;
        private RenderTexture finalCubemap;

        private GameObject probeCameraGameObject;
        private Camera probeCamera;

        private RenderTextureConverter renderTextureConverter;

        private static int renderTargetDepthBits = 32; //0 16 24 32

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

            //NOTE: To get around the alignment/orentation issues, the camera actually needs to have a flipped setup for reflections
            probeCamera.projectionMatrix = probeCamera.projectionMatrix * Matrix4x4.Scale(new Vector3(1, -1, 1));
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

            //SELF NOTE: I don't like this at all, because this changes it for the entire scene too (all of the cameras in it) if you don't switch it back.
            //While we are setting it here and then quickly setting it off at the end of the function, which does work... it just irks me that it could potentially cause issues.
            //The proper and better way to handle all of this, is by using a custom command buffer instead where we can control the rendering without potentially fucking everything else up.
            //But for the time being and in the current test environment, it does work :D
            GL.invertCulling = true;

            //|||||||||||||||||||||||||||||||||||||| RENDER CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //here we actually render the scene in 6 different axis
            //render the camera on a given orientation, then combine the result back into our final cubemap which is handled with the compute shader
            //in addition we also use Graphics.CopyTexture to transfer each rendered slice into the final cubemap

            //X Positive (X+)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.right, Vector3.up);
            probeCamera.Render();
            Graphics.CopyTexture(probeCameraRender, 0, 0, finalCubemap, 0, 0);

            //X Negative (X-)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.left, Vector3.up);
            probeCamera.Render();
            Graphics.CopyTexture(probeCameraRender, 0, 0, finalCubemap, 1, 0);

            //Y Positive (Y+)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.up);
            probeCamera.Render();
            Graphics.CopyTexture(probeCameraRender, 0, 0, finalCubemap, 2, 0);

            //Y Negative (Y-)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.up);
            probeCamera.Render();
            Graphics.CopyTexture(probeCameraRender, 0, 0, finalCubemap, 3, 0);

            //Z Positive (Z+)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
            probeCamera.Render();
            Graphics.CopyTexture(probeCameraRender, 0, 0, finalCubemap, 4, 0);

            //Z Negative (Z-)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.back, Vector3.up);
            probeCamera.Render();
            Graphics.CopyTexture(probeCameraRender, 0, 0, finalCubemap, 5, 0);

            //|||||||||||||||||||||||||||||||||||||| CONVERT COMBINED CUBEMAP INTO ACTUAL USABLE CUBEMAP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| CONVERT COMBINED CUBEMAP INTO ACTUAL USABLE CUBEMAP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| CONVERT COMBINED CUBEMAP INTO ACTUAL USABLE CUBEMAP ||||||||||||||||||||||||||||||||||||||

            //generate mips so PBR shaders can sample a slightly blurrier version of the reflection cubemap
            //IMPORTANT NOTE: this is not PBR compliant, PBR shaders in unity (and most engines if configured as such) actually need a special mip map setup for reflection cubemaps (specular convolution)
            //so what actually comes from this is not correct nor should it be used (if you really really really have no other choice I suppose you can)
            //with that said in a later version of this we do use a proper specular convolution setup, but this is here just for illustrative/simplicity purposes
            finalCubemap.GenerateMips();

            //quick fix for flipped camera projection, read comment at top of function for more about this...
            GL.invertCulling = false;

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