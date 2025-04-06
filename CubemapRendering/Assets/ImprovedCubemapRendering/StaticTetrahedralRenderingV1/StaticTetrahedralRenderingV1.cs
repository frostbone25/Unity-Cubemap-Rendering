using System;
using System.Reflection;
using Unity.Collections;
using UnityEditor;

using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ImprovedCubemapRendering
{
    public class StaticTetrahedralRenderingV1 : MonoBehaviour
    {
        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||

        //should we render our scene in half precison rather than float?
        //NOTE: since we are in a static context (or baked) we don't have to worry about performance.
        //so if you are in a scene that uses physical light units, you may want to retain floating point precison to avoid errors
        //otherwise, you could probably render in half and not notice a difference.
        [Header("Properties")]
        public bool renderInHalfPrecison;
        [Range(1.0f, 2.0f)]public float overdrawFactor = 1.0f;
        public ComputeShader tetrahedralLutComputeShader;
        public ComputeShader tetrahedralRenderingComputeShader;
        public Texture2DArray cubemapToTetrahedralNaiveLUT;
        //public Texture2D naiveLUT;
        //public Texture2D compactLUT;

        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||

        private TextureFormat textureFormat
        {
            get
            {
                return renderInHalfPrecison ? TextureFormat.RGBAHalf : TextureFormat.RGBAFloat;
            }
        }

        private RenderTextureFormat renderTextureFormat
        {
            get
            {
                return renderInHalfPrecison ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGBFloat;
            }
        }

        private ReflectionProbe reflectionProbe;
        private RenderTexture probeCameraRender;
        private GameObject probeCameraGameObject;
        private Camera probeCamera;
        private RenderTextureConverter renderTextureConverter;

        private static int renderTargetDepthBits = 32; //0 16 24 32

        private static readonly float TetrahedronFaceFovX = 143.98570868f;
        private static readonly float TetrahedronFaceFovY = 125.27438968f;
        //private static float TetrahedronFaceAspect = TetrahedronFaceFovX / TetrahedronFaceFovY;
        private static readonly float TetrahedronFaceAspect = 1.1493626833688518353833739467634f; //TetrahedronFaceFovX / TetrahedronFaceFovY
        //private static float TetrahedronFaceAspect = TetrahedronFaceFovY / TetrahedronFaceFovX;
        //private static readonly float TetrahedronFaceAspect = 0.8700473875391005923547050739147f; //TetrahedronFaceFovY / TetrahedronFaceFovX

        private int TetrahedronShadowMapWidth => reflectionProbe.resolution * 2;
        private int TetrahedronShadowMapHeight => reflectionProbe.resolution * 2;

        private float OffsetX => 0.5f / TetrahedronShadowMapWidth;
        private float OffsetY => 0.5f / TetrahedronShadowMapHeight;

        private Matrix4x4 TetrahedronLightFacePerspectiveMatrix => Matrix4x4.Perspective(TetrahedronFaceFovY, TetrahedronFaceAspect, reflectionProbe.nearClipPlane, reflectionProbe.farClipPlane);

        //top left
        private static Vector3 TetrahedronGreenFaceCenter = new Vector3(0.0f, -0.57735026f, 0.81649661f);
        private static Vector3 TetrahedronGreenFaceRotation = new Vector3(0.0f, 27.36780516f, 0.0f); //Roll Pitch Yaw (Directly From Paper)
        private Quaternion TetrahedronGreenFaceUnityRotation = Quaternion.Euler(TetrahedronGreenFaceRotation.y, TetrahedronGreenFaceRotation.z, TetrahedronGreenFaceRotation.x);

        private Matrix4x4 TetrahedronGreenFaceViewMatrix => Matrix4x4.TRS(reflectionProbe.bounds.center, Quaternion.Euler(TetrahedronGreenFaceRotation), Vector3.one);
        private Matrix4x4 TetrahedronGreenFaceViewPerspectiveTexMatrixOffset => new Matrix4x4()
        {
            m00 = 0.25f,
            m01 = 0.0f,
            m02 = 0.0f,
            m03 = 0.0f,

            m10 = 0.0f,
            m11 = -0.25f,
            m12 = 0.0f,
            m13 = 0.0f,

            m20 = 0.0f,
            m21 = 0.0f,
            m22 = 1.0f,
            m23 = 0.0f,

            m30 = 0.25f + OffsetX,
            m31 = 0.25f + OffsetY,
            m32 = 0.0f,
            m33 = 1.0f,
        };
        private Matrix4x4 TetrahedronGreenFaceViewPerspectiveTexMatrix => TetrahedronGreenFaceViewMatrix * TetrahedronLightFacePerspectiveMatrix * TetrahedronGreenFaceViewPerspectiveTexMatrixOffset;

        //top right
        private static Vector3 TetrahedronYellowFaceCenter = new Vector3(0.0f, -0.57735026f, -0.81649661f);
        private static Vector3 TetrahedronYellowFaceRotation = new Vector3(0.0f, 27.36780516f, 180.0f); //Roll Pitch Yaw (Directly From Paper)
        private Quaternion TetrahedronYellowFaceUnityRotation = Quaternion.Euler(TetrahedronYellowFaceRotation.y, TetrahedronYellowFaceRotation.z, TetrahedronYellowFaceRotation.x);

        private Matrix4x4 TetrahedronYellowFaceViewMatrix => Matrix4x4.TRS(reflectionProbe.bounds.center, Quaternion.Euler(TetrahedronYellowFaceRotation), Vector3.one);
        private Matrix4x4 TetrahedronYellowFaceViewPerspectiveTexMatrixOffset => new Matrix4x4()
        {
            m00 = 0.25f,
            m01 = 0.0f,
            m02 = 0.0f,
            m03 = 0.0f,

            m10 = 0.0f,
            m11 = -0.25f,
            m12 = 0.0f,
            m13 = 0.0f,

            m20 = 0.0f,
            m21 = 0.0f,
            m22 = 1.0f,
            m23 = 0.0f,

            m30 = 0.75f + OffsetX,
            m31 = 0.25f + OffsetY,
            m32 = 0.0f,
            m33 = 1.0f,
        };
        private Matrix4x4 TetrahedronYellowFaceViewPerspectiveTexMatrix => TetrahedronYellowFaceViewMatrix * TetrahedronLightFacePerspectiveMatrix * TetrahedronYellowFaceViewPerspectiveTexMatrixOffset;

        //bottom left
        private static Vector3 TetrahedronBlueFaceCenter = new Vector3(-0.81649661f, 0.57735026f, 0.0f);
        private static Vector3 TetrahedronBlueFaceRotation = new Vector3(0.0f, -27.36780516f, -90.0f); //Roll Pitch Yaw (Directly From Paper)
        private Quaternion TetrahedronBlueFaceUnityRotation = Quaternion.Euler(TetrahedronBlueFaceRotation.y, TetrahedronBlueFaceRotation.z, TetrahedronBlueFaceRotation.x);

        private Matrix4x4 TetrahedronBlueFaceViewMatrix => Matrix4x4.TRS(reflectionProbe.bounds.center, Quaternion.Euler(TetrahedronBlueFaceRotation), Vector3.one);
        private Matrix4x4 TetrahedronBlueFaceViewPerspectiveTexMatrixOffset => new Matrix4x4()
        {
            m00 = 0.25f,
            m01 = 0.0f,
            m02 = 0.0f,
            m03 = 0.0f,

            m10 = 0.0f,
            m11 = -0.25f,
            m12 = 0.0f,
            m13 = 0.0f,

            m20 = 0.0f,
            m21 = 0.0f,
            m22 = 1.0f,
            m23 = 0.0f,

            m30 = 0.25f + OffsetX,
            m31 = 0.75f + OffsetY,
            m32 = 0.0f,
            m33 = 1.0f,
        };
        private Matrix4x4 TetrahedronBlueFaceViewPerspectiveTexMatrix => TetrahedronBlueFaceViewMatrix * TetrahedronLightFacePerspectiveMatrix * TetrahedronBlueFaceViewPerspectiveTexMatrixOffset;

        //bottom right
        private static Vector3 TetrahedronRedFaceCenter = new Vector3(0.81649661f, 0.57735026f, 0.0f);
        private static Vector3 TetrahedronRedFaceRotation = new Vector3(0.0f, -27.36780516f, 90.0f); //Roll Pitch Yaw (Directly From Paper)
        private Quaternion TetrahedronRedFaceUnityRotation = Quaternion.Euler(TetrahedronRedFaceRotation.y, TetrahedronRedFaceRotation.z, TetrahedronRedFaceRotation.x);

        private Matrix4x4 TetrahedronRedFaceViewMatrix => Matrix4x4.TRS(reflectionProbe.bounds.center, Quaternion.Euler(TetrahedronRedFaceRotation), Vector3.one);
        private Matrix4x4 TetrahedronRedFaceViewPerspectiveTexMatrixOffset => new Matrix4x4()
        {
            m00 = 0.25f,
            m01 = 0.0f,
            m02 = 0.0f,
            m03 = 0.0f,

            m10 = 0.0f,
            m11 = -0.25f,
            m12 = 0.0f,
            m13 = 0.0f,

            m20 = 0.0f,
            m21 = 0.0f,
            m22 = 1.0f,
            m23 = 0.0f,

            m30 = 0.75f + OffsetX,
            m31 = 0.75f + OffsetY,
            m32 = 0.0f,
            m33 = 1.0f,
        };
        private Matrix4x4 TetrahedronRedFaceViewPerspectiveTexMatrix => TetrahedronRedFaceViewMatrix * TetrahedronLightFacePerspectiveMatrix * TetrahedronRedFaceViewPerspectiveTexMatrixOffset;

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
            probeCamera.fieldOfView = TetrahedronFaceFovY * overdrawFactor;
            probeCamera.aspect = TetrahedronFaceAspect;
            probeCamera.nearClipPlane = reflectionProbe.nearClipPlane;
            probeCamera.farClipPlane = reflectionProbe.farClipPlane;
            probeCamera.backgroundColor = reflectionProbe.backgroundColor;
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
            DestroyImmediate(probeCameraGameObject);

            //make sure these references are gone
            probeCameraGameObject = null;
            probeCamera = null;
        }

        //|||||||||||||||||||||||||||||||||||||| RENDER STATIC TETRAHEDRON NAIVE ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| RENDER STATIC TETRAHEDRON NAIVE ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| RENDER STATIC TETRAHEDRON NAIVE ||||||||||||||||||||||||||||||||||||||

        /// <summary>
        /// Main cubemap rendering function
        /// </summary>
        [ContextMenu("GenerateTetrahedronNaiveLUT")]
        public void GenerateTetrahedronNaiveLUT()
        {
            RenderTextureFormat rtFormat = RenderTextureFormat.RGHalf;
            TextureFormat tFormat = TextureFormat.RGHalf;

            reflectionProbe = GetComponent<ReflectionProbe>();

            int cubemapFaceResolution = reflectionProbe.resolution;

            Texture2DArray outputTexture2DArray = new Texture2DArray(cubemapFaceResolution, cubemapFaceResolution, 6, tFormat, false, true, true);

            int computeShaderCubemapToTetrahedralUV = tetrahedralLutComputeShader.FindKernel("CubemapToTetrahedralUV");
            tetrahedralLutComputeShader.SetInt("CubemapFaceResolution", cubemapFaceResolution);
            tetrahedralLutComputeShader.SetVector("TetrahedralMapResolution", new Vector4(cubemapFaceResolution * 2, cubemapFaceResolution * 2, 0, 0));
            tetrahedralLutComputeShader.SetFloat("HorizontalFOV", 131.55f * overdrawFactor); // Original: 143.98570868
            tetrahedralLutComputeShader.SetFloat("VerticalFOV", 125.27438968f);

            RenderTexture inputFace = new RenderTexture(cubemapFaceResolution, cubemapFaceResolution, 0, rtFormat);
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

            AssetDatabase.CreateAsset(outputTexture2DArray, "Assets/CubemapToTetrahedronNaiveLUT.asset");

            //cleanup our mess because we are done!
            Cleanup();
        }

        /// <summary>
        /// Main cubemap rendering function
        /// </summary>
        [ContextMenu("RenderStaticTetrahedronNaive")]
        public void RenderStaticTetrahedronNaive()
        {
            //setup before we start
            Setup();

            //if we have this render target still around, make sure we clean it up before we start
            if (probeCameraRender != null && probeCameraRender.IsCreated())
                probeCameraRender.Release();

            //start with no reflection data in the scene (at least on meshes within bounds of this reflection probe)
            //NOTE: Not implemented here, but if you want multi-bounce static reflections, we would just loop this entire function, and assign the previous cubemap texture here.
            reflectionProbe.customBakedTexture = null;

            //create our render target that our reflection probe camera will render into
            probeCameraRender = new RenderTexture(reflectionProbe.resolution, reflectionProbe.resolution, 32, renderTextureFormat);
            probeCameraRender.filterMode = FilterMode.Bilinear;
            probeCameraRender.wrapMode = TextureWrapMode.Clamp;
            probeCameraRender.enableRandomWrite = true;
            probeCameraRender.isPowerOfTwo = true;
            probeCameraRender.useMipMap = false;
            probeCameraRender.autoGenerateMips = false;
            probeCameraRender.Create();

            //assign the render target to the camera so when rendering on the camera, it gets fed into our texture
            probeCamera.targetTexture = probeCameraRender;

            RenderTexture tetrahedronMap = new RenderTexture(reflectionProbe.resolution * 2, reflectionProbe.resolution * 2, 0, renderTextureFormat);
            tetrahedronMap.filterMode = FilterMode.Bilinear;
            tetrahedronMap.wrapMode = TextureWrapMode.Clamp;
            tetrahedronMap.enableRandomWrite = true;
            tetrahedronMap.isPowerOfTwo = true;
            tetrahedronMap.useMipMap = false;
            tetrahedronMap.autoGenerateMips = false;
            tetrahedronMap.Create();

            int computeShaderTetrahedralFaceCombine = tetrahedralRenderingComputeShader.FindKernel("TetrahedralFaceCombine");
            tetrahedralRenderingComputeShader.SetVector("TetrahedronFaceResolution", new Vector4(probeCameraRender.width, probeCameraRender.height, 0.0f, 0.0f));
            tetrahedralRenderingComputeShader.SetVector("TetrahedronMapResolution", new Vector4(tetrahedronMap.width, tetrahedronMap.height, 0.0f, 0.0f));

            //|||||||||||||||||||||||||||||||||||||| RENDER AND COMBINE TETRAHEDRON FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER AND COMBINE TETRAHEDRON FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER AND COMBINE TETRAHEDRON FACES ||||||||||||||||||||||||||||||||||||||
            //here we actually render the scene in 4 different axis

            probeCameraGameObject.transform.rotation = TetrahedronGreenFaceUnityRotation;
            probeCamera.Render();
            tetrahedralRenderingComputeShader.SetInt("TetrahedronFaceIndex", 0);
            tetrahedralRenderingComputeShader.SetTexture(computeShaderTetrahedralFaceCombine, "TetrahedronFaceRender", probeCameraRender);
            tetrahedralRenderingComputeShader.SetTexture(computeShaderTetrahedralFaceCombine, "TetrahedronFaceMapOutput", tetrahedronMap);
            tetrahedralRenderingComputeShader.Dispatch(computeShaderTetrahedralFaceCombine, Mathf.CeilToInt(tetrahedronMap.width / 4), Mathf.CeilToInt(tetrahedronMap.height / 4), 1);

            probeCameraGameObject.transform.rotation = TetrahedronYellowFaceUnityRotation;
            probeCamera.Render();
            tetrahedralRenderingComputeShader.SetInt("TetrahedronFaceIndex", 1);
            tetrahedralRenderingComputeShader.SetTexture(computeShaderTetrahedralFaceCombine, "TetrahedronFaceRender", probeCameraRender);
            tetrahedralRenderingComputeShader.SetTexture(computeShaderTetrahedralFaceCombine, "TetrahedronFaceMapOutput", tetrahedronMap);
            tetrahedralRenderingComputeShader.Dispatch(computeShaderTetrahedralFaceCombine, Mathf.CeilToInt(tetrahedronMap.width / 4), Mathf.CeilToInt(tetrahedronMap.height / 4), 1);

            probeCameraGameObject.transform.rotation = TetrahedronBlueFaceUnityRotation;
            probeCamera.Render();
            tetrahedralRenderingComputeShader.SetInt("TetrahedronFaceIndex", 2);
            tetrahedralRenderingComputeShader.SetTexture(computeShaderTetrahedralFaceCombine, "TetrahedronFaceRender", probeCameraRender);
            tetrahedralRenderingComputeShader.SetTexture(computeShaderTetrahedralFaceCombine, "TetrahedronFaceMapOutput", tetrahedronMap);
            tetrahedralRenderingComputeShader.Dispatch(computeShaderTetrahedralFaceCombine, Mathf.CeilToInt(tetrahedronMap.width / 4), Mathf.CeilToInt(tetrahedronMap.height / 4), 1);

            probeCameraGameObject.transform.rotation = TetrahedronRedFaceUnityRotation;
            probeCamera.Render();
            tetrahedralRenderingComputeShader.SetInt("TetrahedronFaceIndex", 3);
            tetrahedralRenderingComputeShader.SetTexture(computeShaderTetrahedralFaceCombine, "TetrahedronFaceRender", probeCameraRender);
            tetrahedralRenderingComputeShader.SetTexture(computeShaderTetrahedralFaceCombine, "TetrahedronFaceMapOutput", tetrahedronMap);
            tetrahedralRenderingComputeShader.Dispatch(computeShaderTetrahedralFaceCombine, Mathf.CeilToInt(tetrahedronMap.width / 4), Mathf.CeilToInt(tetrahedronMap.height / 4), 1);

            //we are done rendering, and since we converted each of the rendered faces into a texture2D we don't need this render texture anymore
            probeCameraRender.Release();

            renderTextureConverter.SaveRenderTexture2DAsTexture2D(tetrahedronMap, "Assets/TetrahedronMap.asset");

            //|||||||||||||||||||||||||||||||||||||| TETRAHEDRON MAP TO CUBEMAP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| TETRAHEDRON MAP TO CUBEMAP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| TETRAHEDRON MAP TO CUBEMAP ||||||||||||||||||||||||||||||||||||||

            //NOTE: Since there is no native "RWTextureCube" we use a Tex2DArray with 6 slices which is similar to a cubemap setup.
            RenderTexture intermediateCubemap = new RenderTexture(reflectionProbe.resolution, reflectionProbe.resolution, renderTargetDepthBits, renderTextureFormat);
            intermediateCubemap.filterMode = FilterMode.Trilinear;
            intermediateCubemap.wrapMode = TextureWrapMode.Clamp;
            intermediateCubemap.volumeDepth = 6; //6 faces in cubemap
            intermediateCubemap.dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray;
            intermediateCubemap.enableRandomWrite = true;
            intermediateCubemap.isPowerOfTwo = true;
            intermediateCubemap.useMipMap = false;
            intermediateCubemap.autoGenerateMips = false;
            intermediateCubemap.Create();
            
            int computeShaderTetrahedralMapToCubemap = tetrahedralRenderingComputeShader.FindKernel("TetrahedralMapToCubemap");
            tetrahedralRenderingComputeShader.SetTexture(computeShaderTetrahedralMapToCubemap, "TetrahedralColorMap", tetrahedronMap);
            tetrahedralRenderingComputeShader.SetTexture(computeShaderTetrahedralMapToCubemap, "TetrahedralCubemapLUT", cubemapToTetrahedralNaiveLUT);
            tetrahedralRenderingComputeShader.SetTexture(computeShaderTetrahedralMapToCubemap, "CubemapOutput", intermediateCubemap);
            tetrahedralRenderingComputeShader.Dispatch(computeShaderTetrahedralMapToCubemap, Mathf.CeilToInt(intermediateCubemap.width / 4), Mathf.CeilToInt(intermediateCubemap.height / 4), 6);

            RenderTexture cubemap2D = new RenderTexture(reflectionProbe.resolution * 6, reflectionProbe.resolution, renderTargetDepthBits, renderTextureFormat);
            cubemap2D.filterMode = FilterMode.Trilinear;
            cubemap2D.wrapMode = TextureWrapMode.Clamp;
            cubemap2D.dimension = UnityEngine.Rendering.TextureDimension.Tex2D;
            cubemap2D.enableRandomWrite = true;
            cubemap2D.isPowerOfTwo = true;
            cubemap2D.useMipMap = false;
            cubemap2D.autoGenerateMips = false;
            cubemap2D.Create();

            int computeShaderCubemapToTexture2D = tetrahedralRenderingComputeShader.FindKernel("CubemapToTexture2D");
            tetrahedralRenderingComputeShader.SetVector("CubemapOutput2DResolution", new Vector4(cubemap2D.width, cubemap2D.height, 0.0f, 0.0f));
            tetrahedralRenderingComputeShader.SetTexture(computeShaderCubemapToTexture2D, "CubemapInput", intermediateCubemap);
            tetrahedralRenderingComputeShader.SetTexture(computeShaderCubemapToTexture2D, "CubemapOutput2D", cubemap2D);
            tetrahedralRenderingComputeShader.Dispatch(computeShaderCubemapToTexture2D, Mathf.CeilToInt(cubemap2D.width / 4), Mathf.CeilToInt(cubemap2D.height / 4), 1);

            //|||||||||||||||||||||||||||||||||||||| SAVE FINAL CUBEMAP TO DISK ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SAVE FINAL CUBEMAP TO DISK ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SAVE FINAL CUBEMAP TO DISK ||||||||||||||||||||||||||||||||||||||
            //here we save our final cubemap into the project/disk!
            //NOTE: you could save as a .asset file, however an .exr or other common image format is more user/artist friendly and you can be able to edit it externally if you wanted to.

            Texture2D cubemap2D_Saved = renderTextureConverter.ConvertRenderTexture2DToTexture2D(cubemap2D, false, true);
            byte[] newCubemapRenderEXR = cubemap2D_Saved.EncodeToEXR();

            //NOTE TO SELF: Application.dataPath returns the absolute system path up to the Assets folder
            string systemAssetPath = string.Format("{0}/ImprovedCubemapRendering/StaticTetrahedralRenderingV1/Data/{1}_{2}.exr", Application.dataPath, SceneManager.GetActiveScene().name, gameObject.name);
            string unityAssetPath = string.Format("Assets/ImprovedCubemapRendering/StaticTetrahedralRenderingV1/Data/{0}_{1}.exr", SceneManager.GetActiveScene().name, gameObject.name);

            if (System.IO.File.Exists(systemAssetPath))
                System.IO.File.Delete(systemAssetPath);

            System.IO.File.WriteAllBytes(systemAssetPath, newCubemapRenderEXR);
            AssetDatabase.ImportAsset(unityAssetPath);

            //|||||||||||||||||||||||||||||||||||||| UNITY SPECULAR CONVOLUTION ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| UNITY SPECULAR CONVOLUTION ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| UNITY SPECULAR CONVOLUTION ||||||||||||||||||||||||||||||||||||||
            //here we do some funky-ish logic, where we utilize unity's built-in tools to take our rendered cubemap
            //to interpet it as a unity cubemap, generate mip-maps, and specularly convolve the cubemap

            TextureImporter textureImporter = (TextureImporter)TextureImporter.GetAtPath(unityAssetPath);
            TextureImporterSettings textureImporterSettings = new TextureImporterSettings();
            textureImporter.ReadTextureSettings(textureImporterSettings);
            textureImporterSettings.mipmapEnabled = true;
            textureImporterSettings.filterMode = FilterMode.Trilinear;
            textureImporterSettings.textureShape = TextureImporterShape.TextureCube;
            textureImporterSettings.cubemapConvolution = TextureImporterCubemapConvolution.Specular;
            textureImporterSettings.generateCubemap = TextureImporterGenerateCubemap.FullCubemap;
            textureImporter.SetTextureSettings(textureImporterSettings);
            textureImporter.SaveAndReimport();

            intermediateCubemap.Release();
            tetrahedronMap.Release();
            cubemap2D.Release();

            //cleanup our mess because we are done!
            Cleanup();
        }

        //|||||||||||||||||||||||||||||||||||||| RENDER STATIC TETRAHEDRON COMPACT ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| RENDER STATIC TETRAHEDRON COMPACT ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| RENDER STATIC TETRAHEDRON COMPACT ||||||||||||||||||||||||||||||||||||||

        // Function to determine if a point is inside a triangle using barycentric coordinates
        private bool IsPointInTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
        {
            // Calculate barycentric coordinates
            float d = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);

            // First barycentric coordinate
            float alpha = ((b.y - c.y) * (point.x - c.x) + (c.x - b.x) * (point.y - c.y)) / d;

            // Second barycentric coordinate
            float beta = ((c.y - a.y) * (point.x - c.x) + (a.x - c.x) * (point.y - c.y)) / d;

            // Third barycentric coordinate
            float gamma = 1.0f - alpha - beta;

            // If all coordinates are between 0 and 1, the point is inside the triangle
            return alpha >= 0f && beta >= 0f && gamma >= 0f;
        }

        // Function to rotate texture by any angle (in degrees, clockwise)
        public Color[] RotateTexture(Color[] sourceColors, int width, int height, float angleDegrees)
        {
            Color[] rotatedColors = new Color[sourceColors.Length];

            // Set empty pixels to black
            for (int i = 0; i < rotatedColors.Length; i++)
            {
                rotatedColors[i] = Color.black;
            }

            // Convert angle to radians and calculate sine/cosine
            float angleRadians = angleDegrees * Mathf.Deg2Rad;
            float cosAngle = Mathf.Cos(angleRadians);
            float sinAngle = Mathf.Sin(angleRadians);

            // Calculate the center of the texture
            float centerX = (width - 1) / 2.0f;
            float centerY = (height - 1) / 2.0f;

            // Process each pixel in the destination texture
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Translate pixel to origin (center of texture)
                    float transX = x - centerX;
                    float transY = y - centerY;

                    // Rotate around origin
                    float rotX = transX * cosAngle - transY * sinAngle;
                    float rotY = transX * sinAngle + transY * cosAngle;

                    // Translate back
                    float srcX = rotX + centerX;
                    float srcY = rotY + centerY;

                    // Check if the pixel is within bounds of source texture
                    if (srcX >= 0 && srcX < width - 1 && srcY >= 0 && srcY < height - 1)
                    {
                        // Bilinear interpolation for smoother results
                        int x1 = (int)srcX;
                        int y1 = (int)srcY;
                        int x2 = x1 + 1;
                        int y2 = y1 + 1;

                        float dx = srcX - x1;
                        float dy = srcY - y1;

                        // Get colors of the four surrounding pixels
                        Color c11 = sourceColors[y1 * width + x1];
                        Color c12 = sourceColors[y1 * width + x2];
                        Color c21 = sourceColors[y2 * width + x1];
                        Color c22 = sourceColors[y2 * width + x2];

                        // Bilinear interpolation
                        Color topMix = Color.Lerp(c11, c12, dx);
                        Color bottomMix = Color.Lerp(c21, c22, dx);
                        Color finalColor = Color.Lerp(topMix, bottomMix, dy);

                        // Set the color in the rotated texture
                        rotatedColors[y * width + x] = finalColor;
                    }
                }
            }

            return rotatedColors;
        }

        /*
        /// <summary>
        /// Main cubemap rendering function
        /// </summary>
        [ContextMenu("RenderStaticTetrahedronCompact")]
        public void RenderStaticTetrahedronCompact()
        {
            //setup before we start
            Setup();

            //if we have this render target still around, make sure we clean it up before we start
            if (probeCameraRender != null && probeCameraRender.IsCreated())
                probeCameraRender.Release();

            //start with no reflection data in the scene (at least on meshes within bounds of this reflection probe)
            //NOTE: Not implemented here, but if you want multi-bounce static reflections, we would just loop this entire function, and assign the previous cubemap texture here.
            reflectionProbe.customBakedTexture = null;

            //create our render target that our reflection probe camera will render into
            probeCameraRender = new RenderTexture(reflectionProbe.resolution, reflectionProbe.resolution, 32, renderTextureFormat);
            probeCameraRender.filterMode = FilterMode.Bilinear;
            probeCameraRender.wrapMode = TextureWrapMode.Clamp;
            probeCameraRender.Create();

            //assign the render target to the camera so when rendering on the camera, it gets fed into our texture
            probeCamera.targetTexture = probeCameraRender;

            //|||||||||||||||||||||||||||||||||||||| RENDER TETRAHEDRON FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER TETRAHEDRON FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER TETRAHEDRON FACES ||||||||||||||||||||||||||||||||||||||
            //here we actually render the scene in 4 different axis

            probeCameraGameObject.transform.rotation = TetrahedronGreenFaceUnityRotation;
            probeCamera.Render();
            Texture2D TetrahedronGreenFace = renderTextureConverter.ConvertRenderTexture2DToTexture2D(probeCameraRender, false, true);

            probeCameraGameObject.transform.rotation = TetrahedronYellowFaceUnityRotation;
            probeCamera.Render();
            Texture2D TetrahedronYellowFace = renderTextureConverter.ConvertRenderTexture2DToTexture2D(probeCameraRender, false, true);

            probeCameraGameObject.transform.rotation = TetrahedronBlueFaceUnityRotation;
            probeCamera.Render();
            Texture2D TetrahedronBlueFace = renderTextureConverter.ConvertRenderTexture2DToTexture2D(probeCameraRender, false, true);

            probeCameraGameObject.transform.rotation = TetrahedronRedFaceUnityRotation;
            probeCamera.Render();
            Texture2D TetrahedronRedFace = renderTextureConverter.ConvertRenderTexture2DToTexture2D(probeCameraRender, false, true);

            //we are done rendering, and since we converted each of the rendered faces into a texture2D we don't need this render texture anymore
            probeCameraRender.Release();

            AssetDatabase.CreateAsset(TetrahedronGreenFace, "Assets/0_TetrahedronGreenFace.asset");
            AssetDatabase.CreateAsset(TetrahedronYellowFace, "Assets/1_TetrahedronYellowFace.asset");
            AssetDatabase.CreateAsset(TetrahedronBlueFace, "Assets/2_TetrahedronBlueFace.asset");
            AssetDatabase.CreateAsset(TetrahedronRedFace, "Assets/3_TetrahedronRedFace.asset");

            byte[] TetrahedronGreenFaceEXR = TetrahedronGreenFace.EncodeToEXR();
            byte[] TetrahedronYellowFaceEXR = TetrahedronYellowFace.EncodeToEXR();
            byte[] TetrahedronBlueFaceEXR = TetrahedronBlueFace.EncodeToEXR();
            byte[] TetrahedronRedFaceEXR = TetrahedronRedFace.EncodeToEXR();

            //NOTE TO SELF: Application.dataPath returns the absolute system path up to the Assets folder
            string TetrahedronGreenFaceEXR_systemAssetPath = string.Format("{0}/ImprovedCubemapRendering/StaticTetrahedralRenderingV1/Data/{1}_{2}.exr", Application.dataPath, SceneManager.GetActiveScene().name, "0_TetrahedronGreenFaceEXR");
            string TetrahedronYellowFaceEXR_systemAssetPath = string.Format("{0}/ImprovedCubemapRendering/StaticTetrahedralRenderingV1/Data/{1}_{2}.exr", Application.dataPath, SceneManager.GetActiveScene().name, "1_TetrahedronYellowFaceEXR");
            string TetrahedronBlueFaceEXR_systemAssetPath = string.Format("{0}/ImprovedCubemapRendering/StaticTetrahedralRenderingV1/Data/{1}_{2}.exr", Application.dataPath, SceneManager.GetActiveScene().name, "2_TetrahedronBlueFaceEXR");
            string TetrahedronRedFaceEXR_systemAssetPath = string.Format("{0}/ImprovedCubemapRendering/StaticTetrahedralRenderingV1/Data/{1}_{2}.exr", Application.dataPath, SceneManager.GetActiveScene().name, "3_TetrahedronRedFaceEXR");

            if (System.IO.File.Exists(TetrahedronGreenFaceEXR_systemAssetPath))
                System.IO.File.Delete(TetrahedronGreenFaceEXR_systemAssetPath);

            if (System.IO.File.Exists(TetrahedronYellowFaceEXR_systemAssetPath))
                System.IO.File.Delete(TetrahedronYellowFaceEXR_systemAssetPath);

            if (System.IO.File.Exists(TetrahedronBlueFaceEXR_systemAssetPath))
                System.IO.File.Delete(TetrahedronBlueFaceEXR_systemAssetPath);

            if (System.IO.File.Exists(TetrahedronRedFaceEXR_systemAssetPath))
                System.IO.File.Delete(TetrahedronRedFaceEXR_systemAssetPath);

            System.IO.File.WriteAllBytes(TetrahedronGreenFaceEXR_systemAssetPath, TetrahedronGreenFaceEXR);
            System.IO.File.WriteAllBytes(TetrahedronYellowFaceEXR_systemAssetPath, TetrahedronYellowFaceEXR);
            System.IO.File.WriteAllBytes(TetrahedronBlueFaceEXR_systemAssetPath, TetrahedronBlueFaceEXR);
            System.IO.File.WriteAllBytes(TetrahedronRedFaceEXR_systemAssetPath, TetrahedronRedFaceEXR);

            string TetrahedronGreenFaceEXR_unityAssetPath = string.Format("Assets/ImprovedCubemapRendering/StaticTetrahedralRenderingV1/Data/{0}_{1}.exr", SceneManager.GetActiveScene().name, "0_TetrahedronGreenFaceEXR");
            string TetrahedronYellowFaceEXR_unityAssetPath = string.Format("Assets/ImprovedCubemapRendering/StaticTetrahedralRenderingV1/Data/{0}_{1}.exr", SceneManager.GetActiveScene().name, "1_TetrahedronYellowFaceEXR");
            string TetrahedronBlueFaceEXR_unityAssetPath = string.Format("Assets/ImprovedCubemapRendering/StaticTetrahedralRenderingV1/Data/{0}_{1}.exr", SceneManager.GetActiveScene().name, "2_TetrahedronBlueFaceEXR");
            string TetrahedronRedFaceEXR_unityAssetPath = string.Format("Assets/ImprovedCubemapRendering/StaticTetrahedralRenderingV1/Data/{0}_{1}.exr", SceneManager.GetActiveScene().name, "3_TetrahedronRedFaceEXR");

            AssetDatabase.ImportAsset(TetrahedronGreenFaceEXR_unityAssetPath);
            AssetDatabase.ImportAsset(TetrahedronYellowFaceEXR_unityAssetPath);
            AssetDatabase.ImportAsset(TetrahedronBlueFaceEXR_unityAssetPath);
            AssetDatabase.ImportAsset(TetrahedronRedFaceEXR_unityAssetPath);

            Color[] TetrahedronGreenFaceColors = TetrahedronGreenFace.GetPixels(0); //mip level 0
            Color[] TetrahedronGreenFaceColorsFlipped = new Color[TetrahedronGreenFaceColors.Length];

            for (int y = 0; y < TetrahedronGreenFace.height; y++)
            {
                for (int x = 0; x < TetrahedronGreenFace.width; x++)
                {
                    // Calculate the original index
                    int originalIndex = y * TetrahedronGreenFace.width + x;

                    // Calculate the flipped index (flip on Y axis)
                    int flippedY = TetrahedronGreenFace.height - 1 - y;
                    int flippedIndex = flippedY * TetrahedronGreenFace.width + x;

                    // Copy the color from original to flipped position
                    TetrahedronGreenFaceColorsFlipped[flippedIndex] = TetrahedronGreenFaceColors[originalIndex];
                }
            }

            //TetrahedronGreenFace.SetPixels(TetrahedronGreenFaceColorsFlipped, 0);

            Color[] TetrahedronYellowFaceColors = TetrahedronYellowFace.GetPixels(0); //mip level 0
            Color[] TetrahedronYellowFaceColorsFlipped = new Color[TetrahedronYellowFaceColors.Length];

            for (int y = 0; y < TetrahedronYellowFace.height; y++)
            {
                for (int x = 0; x < TetrahedronYellowFace.width; x++)
                {
                    // Calculate the original index
                    int originalIndex = y * TetrahedronYellowFace.width + x;

                    // Calculate the flipped index (flip on Y axis)
                    int flippedY = TetrahedronYellowFace.height - 1 - y;
                    int flippedIndex = flippedY * TetrahedronYellowFace.width + x;

                    // Copy the color from original to flipped position
                    TetrahedronYellowFaceColorsFlipped[flippedIndex] = TetrahedronYellowFaceColors[originalIndex];
                }
            }

            TetrahedronYellowFace.SetPixels(TetrahedronYellowFaceColorsFlipped, 0);

            Color[] TetrahedronRedFaceColors = TetrahedronRedFace.GetPixels(0); //mip level 0
            Color[] TetrahedronRedFaceRotated = RotateTexture(TetrahedronRedFaceColors, TetrahedronRedFace.width, TetrahedronRedFace.height, -90.0f);
            TetrahedronRedFace.SetPixels(TetrahedronRedFaceRotated, 0);

            Color[] TetrahedronBlueFaceColors = TetrahedronBlueFace.GetPixels(0); //mip level 0
            Color[] TetrahedronBlueFaceRotated = RotateTexture(TetrahedronBlueFaceColors, TetrahedronBlueFace.width, TetrahedronBlueFace.height, 90.0f);
            TetrahedronBlueFace.SetPixels(TetrahedronBlueFaceRotated, 0);

            //|||||||||||||||||||||||||||||||||||||| COMBINE RENDERED TETRAHEDRON FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| COMBINE RENDERED TETRAHEDRON FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| COMBINE RENDERED TETRAHEDRON FACES ||||||||||||||||||||||||||||||||||||||
            //here we combine our rendered faces into a tetrahedron map!

            //create a new texture2D asset.
            //here we will combine the tetrahedron faces into a final map
            Texture2D newTetrahedronRender = new Texture2D(reflectionProbe.resolution * 2, reflectionProbe.resolution * 2, textureFormat, false);

            //iterate for every horizontal pixel
            for (int x = 0; x < newTetrahedronRender.width; x++)
            {
                //iterate for every vertical pixel
                for (int y = 0; y < newTetrahedronRender.height; y++)
                {
                    //compute normalized UV coordinates
                    float xNormalized = (1.0f / newTetrahedronRender.width) * x;
                    float yNormalized = (1.0f / newTetrahedronRender.height) * y;

                    Color finalColor = Color.black; // Default to black (just in case)

                    //YELLOW TOP TRIANGLE
                    if (IsPointInTriangle(new Vector2(xNormalized, yNormalized), new Vector2(0.0f, 1.0f), new Vector2(0.5f, 0.5f), new Vector2(1.0f, 1.0f)))
                    {
                        //finalColor = Color.yellow; //debug triangle checking
                        finalColor = TetrahedronYellowFace.GetPixelBilinear(xNormalized, Mathf.Clamp(yNormalized * 2.0f - 1.0f, 0.0f, 1.0f));
                    }
                    //GREEN BOTTOM TRIANGLE
                    else if (IsPointInTriangle(new Vector2(xNormalized, yNormalized), new Vector2(0.0f, 0.0f), new Vector2(0.5f, 0.5f), new Vector2(1.0f, 0.0f)))
                    {
                        //finalColor = Color.green; //debug triangle checking
                        finalColor = TetrahedronGreenFace.GetPixelBilinear(xNormalized, Mathf.Clamp(yNormalized * 2.0f, 0.0f, 1.0f));
                    }
                    //RED LEFT TRIANGLE
                    else if (IsPointInTriangle(new Vector2(xNormalized, yNormalized), new Vector2(0.0f, 1.0f), new Vector2(0.5f, 0.5f), new Vector2(0.0f, 0.0f)))
                    {
                        //finalColor = Color.red; //debug triangle checking
                        finalColor = TetrahedronRedFace.GetPixelBilinear(Mathf.Clamp(xNormalized * 2.0f, 0.0f, 1.0f), yNormalized);
                    }
                    //BLUE RIGHT TRIANGLE
                    else if (IsPointInTriangle(new Vector2(xNormalized, yNormalized), new Vector2(1.0f, 1.0f), new Vector2(0.5f, 0.5f), new Vector2(1.0f, 0.0f)))
                    {
                        //finalColor = Color.blue; //debug triangle checking
                        finalColor = TetrahedronBlueFace.GetPixelBilinear(Mathf.Clamp(xNormalized * 2.0f - 1.0f, 0.0f, 1.0f), yNormalized);
                    }

                    // Assign the computed color to the final texture
                    newTetrahedronRender.SetPixel(x, y, finalColor);
                }
            }

            //apply changes
            newTetrahedronRender.Apply();

            AssetDatabase.CreateAsset(newTetrahedronRender, "Assets/4_TetrahedronMap.asset");

            //|||||||||||||||||||||||||||||||||||||| TETRAHEDRON MAP TO CUBEMAP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| TETRAHEDRON MAP TO CUBEMAP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| TETRAHEDRON MAP TO CUBEMAP ||||||||||||||||||||||||||||||||||||||

            //NOTE: Since there is no native "RWTextureCube" we use a Tex2DArray with 6 slices which is similar to a cubemap setup.
            intermediateCubemap = new RenderTexture(reflectionProbe.resolution, reflectionProbe.resolution, renderTargetDepthBits, renderTextureFormat);
            intermediateCubemap.filterMode = FilterMode.Trilinear;
            intermediateCubemap.wrapMode = TextureWrapMode.Clamp;
            intermediateCubemap.volumeDepth = 6; //6 faces in cubemap
            intermediateCubemap.dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray;
            intermediateCubemap.enableRandomWrite = true;
            intermediateCubemap.isPowerOfTwo = true;
            intermediateCubemap.useMipMap = true;
            intermediateCubemap.autoGenerateMips = false;
            intermediateCubemap.Create();

            //int computeShaderTetrahedralMapToCubemap = tetrahedralRenderingComputeShader.FindKernel("TetrahedralMapToCubemap");
            //tetrahedralRenderingComputeShader.SetTexture(computeShaderTetrahedralMapToCubemap, "TetrahedralMapLUT", compactLUT);
            //tetrahedralRenderingComputeShader.SetTexture(computeShaderTetrahedralMapToCubemap, "TetrahedralMapColor", newTetrahedronRender);
            //tetrahedralRenderingComputeShader.SetTexture(computeShaderTetrahedralMapToCubemap, "CubemapOutput", intermediateCubemap);
            //tetrahedralRenderingComputeShader.Dispatch(computeShaderTetrahedralMapToCubemap, Mathf.CeilToInt(newTetrahedronRender.width / 4), Mathf.CeilToInt(newTetrahedronRender.height / 4), 6);

            Cubemap finalCubemap = new Cubemap(intermediateCubemap.width, textureFormat, false);

            Graphics.CopyTexture(intermediateCubemap, 0, 0, finalCubemap, 0, 0); //X Positive (X+)
            Graphics.CopyTexture(intermediateCubemap, 1, 0, finalCubemap, 1, 0); //X Negative (X-)
            Graphics.CopyTexture(intermediateCubemap, 2, 0, finalCubemap, 2, 0); //Y Positive (Y+)
            Graphics.CopyTexture(intermediateCubemap, 3, 0, finalCubemap, 3, 0); //Y Negative (Y-)
            Graphics.CopyTexture(intermediateCubemap, 4, 0, finalCubemap, 4, 0); //Z Positive (Z+)
            Graphics.CopyTexture(intermediateCubemap, 5, 0, finalCubemap, 5, 0); //Z Negative (Z-)

            intermediateCubemap.Release();

            AssetDatabase.CreateAsset(finalCubemap, "Assets/5_Cubemap.asset");

            //cleanup our mess because we are done!
            Cleanup();
        }
        */

        // Multiplies two 3x3 matrices represented as 3 Vector3s each (row-major)
        public static Vector3[] MultiplyMatrix3x3(Vector3[] A, Vector3[] B)
        {
            Vector3[] result = new Vector3[3];

            // Transpose B to access its columns easily
            Vector3 bCol0 = new Vector3(B[0].x, B[1].x, B[2].x);
            Vector3 bCol1 = new Vector3(B[0].y, B[1].y, B[2].y);
            Vector3 bCol2 = new Vector3(B[0].z, B[1].z, B[2].z);

            // Perform row * column dot products
            result[0] = new Vector3(Vector3.Dot(A[0], bCol0), Vector3.Dot(A[0], bCol1), Vector3.Dot(A[0], bCol2));
            result[1] = new Vector3(Vector3.Dot(A[1], bCol0), Vector3.Dot(A[1], bCol1), Vector3.Dot(A[1], bCol2));
            result[2] = new Vector3(Vector3.Dot(A[2], bCol0), Vector3.Dot(A[2], bCol1), Vector3.Dot(A[2], bCol2));

            return result;
        }

        public void CalculateRotationMatrix(float eulerDegreesX, float eulerDegreesY, float eulerDegreesZ)
        {
            Vector3 eulerDegrees = new Vector3(eulerDegreesX, eulerDegreesY, eulerDegreesZ);
            Vector3 eulerRadians = eulerDegrees * Mathf.Deg2Rad;
            Vector3 eulerRadiansSin = new Vector3(Mathf.Sin(eulerRadians.x), Mathf.Sin(eulerRadians.y), Mathf.Sin(eulerRadians.z));
            Vector3 eulerRadiansCos = new Vector3(Mathf.Cos(eulerRadians.x), Mathf.Cos(eulerRadians.y), Mathf.Cos(eulerRadians.z));

            Vector3[] rotationX = new Vector3[3] 
            {
                new Vector3(1, 0, 0),
                new Vector3(0, eulerRadiansCos.x, -eulerRadiansSin.x),
                new Vector3(0, eulerRadiansSin.x, eulerRadiansCos.x),
            };

            Vector3[] rotationY = new Vector3[3]
            {
                new Vector3(eulerRadiansCos.y, 0, eulerRadiansSin.y),
                new Vector3(0, 1, 0),
                new Vector3(-eulerRadiansSin.y, 0, eulerRadiansCos.y),
            };

            Vector3[] rotationZ = new Vector3[3]
            {
                new Vector3(eulerRadiansCos.z, -eulerRadiansSin.z, 0),
                new Vector3(eulerRadiansSin.z, eulerRadiansCos.z, 0),
                new Vector3(0, 0, 1),
            };

            Vector3[] rotation = MultiplyMatrix3x3(rotationY, MultiplyMatrix3x3(rotationX, rotationZ));

            string logOutput = "";

            logOutput += string.Format("eulerDegrees: {0} {1} {2} \n", eulerDegrees.x, eulerDegrees.y, eulerDegrees.z);
            logOutput += string.Format("eulerRadians: {0} {1} {2} \n", eulerRadians.x, eulerRadians.y, eulerRadians.z);
            logOutput += string.Format("eulerRadiansSin: {0} {1} {2} \n", eulerRadiansSin.x, eulerRadiansSin.y, eulerRadiansSin.z);
            logOutput += string.Format("eulerRadiansCos: {0} {1} {2} \n", eulerRadiansCos.x, eulerRadiansCos.y, eulerRadiansCos.z);

            logOutput += "\n";
            logOutput += "rotationX \n";
            logOutput += string.Format("{0}, {1}, {2} \n", rotationX[0].x, rotationX[0].y, rotationX[0].z);
            logOutput += string.Format("{0}, {1}, {2} \n", rotationX[1].x, rotationX[1].y, rotationX[1].z);
            logOutput += string.Format("{0}, {1}, {2} \n", rotationX[2].x, rotationX[2].y, rotationX[2].z);

            logOutput += "\n";
            logOutput += "rotationY \n";
            logOutput += string.Format("{0}, {1}, {2}, \n", rotationY[0].x, rotationY[0].y, rotationY[0].z);
            logOutput += string.Format("{0}, {1}, {2}, \n", rotationY[1].x, rotationY[1].y, rotationY[1].z);
            logOutput += string.Format("{0}, {1}, {2}, \n", rotationY[2].x, rotationY[2].y, rotationY[2].z);

            logOutput += "\n";
            logOutput += "rotationZ \n";
            logOutput += string.Format("{0}, {1}, {2}, \n", rotationZ[0].x, rotationZ[0].y, rotationZ[0].z);
            logOutput += string.Format("{0}, {1}, {2}, \n", rotationZ[1].x, rotationZ[1].y, rotationZ[1].z);
            logOutput += string.Format("{0}, {1}, {2}, \n", rotationZ[2].x, rotationZ[2].y, rotationZ[2].z);

            logOutput += "\n";
            logOutput += "rotationMatrix \n";
            logOutput += string.Format("{0}, {1}, {2}, \n", rotation[0].x, rotation[0].y, rotation[0].z);
            logOutput += string.Format("{0}, {1}, {2}, \n", rotation[1].x, rotation[1].y, rotation[1].z);
            logOutput += string.Format("{0}, {1}, {2}, \n", rotation[2].x, rotation[2].y, rotation[2].z);

            Debug.Log(logOutput);
        }

        [ContextMenu("Test")]
        public void Test()
        {
            CalculateRotationMatrix(27.36780516f, 0.0f, 0.0f); //Pitch Yaw Roll (GREEN TOP LEFT QUAD)
            CalculateRotationMatrix(27.36780516f, 180.0f, 0.0f); //Pitch Yaw Roll (YELLOW TOP RIGHT QUAD
            CalculateRotationMatrix(-27.36780516f, -90.0f, 0.0f); //Pitch Yaw Roll (BLUE BOTTOM LEFT QUAD)
            CalculateRotationMatrix(-27.36780516f, 90.0f, 0.0f); //Pitch Yaw Roll (RED BOTTOM RIGHT QUAD)
        }
    }
}