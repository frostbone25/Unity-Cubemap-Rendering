using UnityEditor;

using UnityEngine;
using UnityEngine.SceneManagement;

//https://discussions.unity.com/t/specular-convolution-when-calculating-mip-maps-for-cubemap-render-texture/729652/15

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

        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||

        [Header("Setup")]
        public ComputeShader cubemapRenderingCompute;

        [Header("Properties")]
        public RealtimeCubemapTextureFormatType formatType = RealtimeCubemapTextureFormatType.RGBAHalf;
        public int updateFPS = 30;
        public int GGXSpecularConvolutionSamples = 256;

        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||

        private float nextUpdateInterval;

        private ReflectionProbe reflectionProbe;

        private RenderTexture probeCameraRender;
        private RenderTexture rawCubemap;
        private RenderTexture convolvedCubemap;
        private RenderTexture finalCubemap;

        private GameObject probeCameraGameObject;
        private Camera probeCamera;

        private RenderTextureConverter renderTextureConverter;

        private static int renderTargetDepthBits = 32; //0 16 24 32

        private int computeShaderKernelCubemapCombine;
        private int computeShaderKernelConvolveSpecularGGX;
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

            //setup our render texture converter class so we can convert render textures to texture2D objects efficently/easily
            if (renderTextureConverter == null)
                renderTextureConverter = new RenderTextureConverter();

            //get our compute shader manually if for whatever reason it wasn't assigned
            if (cubemapRenderingCompute == null)
                cubemapRenderingCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/ImprovedCubemapRendering/RealtimeCubemapRenderingV3/RealtimeCubemapRenderingV3.compute");

            //get some data from the compute shader once (they don't change, no reason to get them every frame anyway)
            computeShaderKernelCubemapCombine = cubemapRenderingCompute.FindKernel("CubemapCombine");
            computeShaderKernelConvolveSpecularGGX = cubemapRenderingCompute.FindKernel("ConvolveSpecularGGX");
            cubemapRenderingCompute.GetKernelThreadGroupSizes(computeShaderKernelCubemapCombine, out computeShaderThreadGroupSizeX, out computeShaderThreadGroupSizeY, out computeShaderThreadGroupSizeZ);
            cubemapRenderingCompute.SetInt("CubemapFaceResolution", reflectionProbe.resolution);
            cubemapRenderingCompute.SetInt("Samples", GGXSpecularConvolutionSamples);

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

            //create a regular 2D render target for the camera
            probeCameraRender = new RenderTexture(reflectionProbe.resolution, reflectionProbe.resolution, renderTargetDepthBits, GetRenderTextureFormatType(formatType));
            probeCameraRender.filterMode = FilterMode.Trilinear;
            probeCameraRender.wrapMode = TextureWrapMode.Clamp;
            probeCameraRender.enableRandomWrite = true;
            probeCameraRender.isPowerOfTwo = true;
            probeCameraRender.Create();

            //NOTE: Since there is no native "RWTextureCube" we use a Tex2DArray with 6 slices which is similar to a cubemap setup.
            rawCubemap = new RenderTexture(reflectionProbe.resolution, reflectionProbe.resolution, renderTargetDepthBits, GetRenderTextureFormatType(formatType));
            rawCubemap.filterMode = FilterMode.Trilinear;
            rawCubemap.wrapMode = TextureWrapMode.Clamp;
            rawCubemap.volumeDepth = 6; //6 faces in cubemap
            rawCubemap.dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray;
            rawCubemap.enableRandomWrite = true;
            rawCubemap.isPowerOfTwo = true;
            rawCubemap.useMipMap = true;
            rawCubemap.autoGenerateMips = false;
            rawCubemap.Create();

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

            if (rawCubemap != null && rawCubemap.IsCreated())
                rawCubemap.Release();

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
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "SceneRender", probeCameraRender);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "CubemapResult", rawCubemap);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, Mathf.CeilToInt(rawCubemap.width / computeShaderThreadGroupSizeX), Mathf.CeilToInt(rawCubemap.height / computeShaderThreadGroupSizeY), 1);

            //X Negative (X-)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.left, Vector3.up);
            probeCamera.Render();
            cubemapRenderingCompute.SetInt("CubemapFaceIndex", 1);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "SceneRender", probeCameraRender);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "CubemapResult", rawCubemap);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, Mathf.CeilToInt(rawCubemap.width / computeShaderThreadGroupSizeX), Mathf.CeilToInt(rawCubemap.height / computeShaderThreadGroupSizeY), 1);

            //Y Positive (Y+)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.down);
            probeCamera.Render();
            cubemapRenderingCompute.SetInt("CubemapFaceIndex", 2);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "SceneRender", probeCameraRender);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "CubemapResult", rawCubemap);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, Mathf.CeilToInt(rawCubemap.width / computeShaderThreadGroupSizeX), Mathf.CeilToInt(rawCubemap.height / computeShaderThreadGroupSizeY), 1);

            //Y Negative (Y-)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.down);
            probeCamera.Render();
            cubemapRenderingCompute.SetInt("CubemapFaceIndex", 3);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "SceneRender", probeCameraRender);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "CubemapResult", rawCubemap);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, Mathf.CeilToInt(rawCubemap.width / computeShaderThreadGroupSizeX), Mathf.CeilToInt(rawCubemap.height / computeShaderThreadGroupSizeY), 1);

            //Z Positive (Z+)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
            probeCamera.Render();
            cubemapRenderingCompute.SetInt("CubemapFaceIndex", 4);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "SceneRender", probeCameraRender);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "CubemapResult", rawCubemap);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, Mathf.CeilToInt(rawCubemap.width / computeShaderThreadGroupSizeX), Mathf.CeilToInt(rawCubemap.height / computeShaderThreadGroupSizeY), 1);

            //Z Negative (Z-)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.back, Vector3.up);
            probeCamera.Render();
            cubemapRenderingCompute.SetInt("CubemapFaceIndex", 5);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "SceneRender", probeCameraRender);
            cubemapRenderingCompute.SetTexture(computeShaderKernelCubemapCombine, "CubemapResult", rawCubemap);
            cubemapRenderingCompute.Dispatch(computeShaderKernelCubemapCombine, Mathf.CeilToInt(rawCubemap.width / computeShaderThreadGroupSizeX), Mathf.CeilToInt(rawCubemap.height / computeShaderThreadGroupSizeY), 1);

            //generate mips so PBR shaders can sample a slightly blurrier version of the reflection cubemap
            //IMPORTANT NOTE: this is not PBR compliant, PBR shaders in unity (and most engines if configured as such) actually need a special mip map setup for reflection cubemaps (specular convolution)
            //so what actually comes from this is not correct nor should it be used (if you really really really have no other choice I suppose you can)
            //with that said in a later version of this we do use a proper specular convolution setup, but this is here just for illustrative/simplicity purposes
            rawCubemap.GenerateMips();

            //|||||||||||||||||||||||||||||||||||||| SPECULAR CONVOLVE CUBEMAP (TEX2DARRAY) ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SPECULAR CONVOLVE CUBEMAP (TEX2DARRAY) ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SPECULAR CONVOLVE CUBEMAP (TEX2DARRAY) ||||||||||||||||||||||||||||||||||||||

            // Transfer mip 0 (this is done separately from the loop below as we do not want to blur it)
            // this saves us a little extra work since the first mip level should be the original reflection
            for (int face = 0; face < 6; face++)
            {
                Graphics.CopyTexture(rawCubemap, face, 0, convolvedCubemap, face, 0);
            }

            //for each cubemap face
            for (int face = 0; face < 6; face++)
            {
                int mipLevelResolution = reflectionProbe.resolution / 2;

                //iterate for each mip level
                for (int mip = 1; mip < convolvedCubemap.mipmapCount; mip++)
                {
                    float roughnessLevel = (1.0f / convolvedCubemap.mipmapCount) * mip;
                    roughnessLevel *= roughnessLevel;

                    cubemapRenderingCompute.SetInt("CubemapFaceIndex", face);
                    cubemapRenderingCompute.SetInt("CubemapMipFaceResolution", mipLevelResolution);
                    cubemapRenderingCompute.SetFloat("Roughness", roughnessLevel);
                    cubemapRenderingCompute.SetTexture(computeShaderKernelConvolveSpecularGGX, "InputCubemap", rawCubemap, mip);
                    cubemapRenderingCompute.SetTexture(computeShaderKernelConvolveSpecularGGX, "CubemapResult", convolvedCubemap, mip);
                    cubemapRenderingCompute.Dispatch(computeShaderKernelConvolveSpecularGGX, Mathf.Max(Mathf.CeilToInt(mipLevelResolution / computeShaderThreadGroupSizeX), 4), Mathf.Max(Mathf.CeilToInt(mipLevelResolution / computeShaderThreadGroupSizeY), 4), 1);

                    mipLevelResolution /= 2;
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
            nextUpdateInterval = Time.time + (1.0f / updateFPS);
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
    }
}