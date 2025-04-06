using System;
using System.Reflection;
using Unity.Collections;

#if UNITY_EDITOR
using UnityEditor;
#endif

using UnityEngine;
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
        public Texture2DArray cubemapToTetrahedralLUT;

        [Header("Resources")]
        public ComputeShader tetrahedralRenderingComputeShader;

        [Header("LUT Generation")]
        [Range(1, 4)] public int lutSupersampling = 2;
        public ComputeShader tetrahedralLutComputeShader;

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
        private static readonly float TetrahedronFaceAspect = 1.1493626833688518353833739467634f; //TetrahedronFaceFovX / TetrahedronFaceFovY

        private int TetrahedronMapWidth => reflectionProbe.resolution * 2;
        private int TetrahedronMapHeight => reflectionProbe.resolution * 2;

        private Matrix4x4 TetrahedronLightFacePerspectiveMatrix => Matrix4x4.Perspective(TetrahedronFaceFovY, TetrahedronFaceAspect, reflectionProbe.nearClipPlane, reflectionProbe.farClipPlane);

        //top left
        private static Vector3 TetrahedronGreenFaceCenter = new Vector3(0.0f, -0.57735026f, 0.81649661f);
        private static Vector3 TetrahedronGreenFaceRotation = new Vector3(0.0f, 27.36780516f, 0.0f); //Roll Pitch Yaw (Directly From Paper)
        private Quaternion TetrahedronGreenFaceUnityRotation = Quaternion.Euler(TetrahedronGreenFaceRotation.y, TetrahedronGreenFaceRotation.z, TetrahedronGreenFaceRotation.x);

        private Matrix4x4 TetrahedronGreenFaceViewMatrix => Matrix4x4.TRS(reflectionProbe.bounds.center, Quaternion.Euler(TetrahedronGreenFaceRotation), Vector3.one);

        //top right
        private static Vector3 TetrahedronYellowFaceCenter = new Vector3(0.0f, -0.57735026f, -0.81649661f);
        private static Vector3 TetrahedronYellowFaceRotation = new Vector3(0.0f, 27.36780516f, 180.0f); //Roll Pitch Yaw (Directly From Paper)
        private Quaternion TetrahedronYellowFaceUnityRotation = Quaternion.Euler(TetrahedronYellowFaceRotation.y, TetrahedronYellowFaceRotation.z, TetrahedronYellowFaceRotation.x);

        private Matrix4x4 TetrahedronYellowFaceViewMatrix => Matrix4x4.TRS(reflectionProbe.bounds.center, Quaternion.Euler(TetrahedronYellowFaceRotation), Vector3.one);

        //bottom left
        private static Vector3 TetrahedronBlueFaceCenter = new Vector3(-0.81649661f, 0.57735026f, 0.0f);
        private static Vector3 TetrahedronBlueFaceRotation = new Vector3(0.0f, -27.36780516f, -90.0f); //Roll Pitch Yaw (Directly From Paper)
        private Quaternion TetrahedronBlueFaceUnityRotation = Quaternion.Euler(TetrahedronBlueFaceRotation.y, TetrahedronBlueFaceRotation.z, TetrahedronBlueFaceRotation.x);

        private Matrix4x4 TetrahedronBlueFaceViewMatrix => Matrix4x4.TRS(reflectionProbe.bounds.center, Quaternion.Euler(TetrahedronBlueFaceRotation), Vector3.one);

        //bottom right
        private static Vector3 TetrahedronRedFaceCenter = new Vector3(0.81649661f, 0.57735026f, 0.0f);
        private static Vector3 TetrahedronRedFaceRotation = new Vector3(0.0f, -27.36780516f, 90.0f); //Roll Pitch Yaw (Directly From Paper)
        private Quaternion TetrahedronRedFaceUnityRotation = Quaternion.Euler(TetrahedronRedFaceRotation.y, TetrahedronRedFaceRotation.z, TetrahedronRedFaceRotation.x);

        private Matrix4x4 TetrahedronRedFaceViewMatrix => Matrix4x4.TRS(reflectionProbe.bounds.center, Quaternion.Euler(TetrahedronRedFaceRotation), Vector3.one);

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
            probeCamera.fieldOfView = TetrahedronFaceFovY;
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

        //|||||||||||||||||||||||||||||||||||||| GENERATE TETRAHEDRON LUT ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| GENERATE TETRAHEDRON LUT ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| GENERATE TETRAHEDRON LUT ||||||||||||||||||||||||||||||||||||||
#if UNITY_EDITOR
        /// <summary>
        /// Main cubemap rendering function
        /// </summary>
        [ContextMenu("GenerateTetrahedronLUT")]
        public void GenerateTetrahedronLUT()
        {
            RenderTextureFormat rtFormat = RenderTextureFormat.RGHalf;
            TextureFormat tFormat = TextureFormat.RGHalf;

            reflectionProbe = GetComponent<ReflectionProbe>();

            int cubemapFaceResolution = reflectionProbe.resolution;

            Texture2DArray outputTexture2DArray = new Texture2DArray(cubemapFaceResolution, cubemapFaceResolution, 6, tFormat, false, true, true);

            int computeShaderCubemapToTetrahedralUV = tetrahedralLutComputeShader.FindKernel("CubemapToTetrahedralUV");
            tetrahedralLutComputeShader.SetInt("CubemapFaceResolution", cubemapFaceResolution);
            tetrahedralLutComputeShader.SetVector("TetrahedralMapResolution", new Vector4(cubemapFaceResolution * lutSupersampling, cubemapFaceResolution * lutSupersampling, 0, 0));
            tetrahedralLutComputeShader.SetFloat("VerticalFOV", 125.27438968f);

            //NOTE HERE: While reconstruting a math function for the LUT (rather than just doing a capture with the RayDirectionTruth.shader)
            //The FOV value that was most accurate to the actual capture was this.
            //This value was also eyeballed and tweaked by hand, so I'm not sure how to get to this value mathematically.
            //But I adjusted the values and played with it until it looked really close to the actual ground truth capture... and it works so to hell with it!
            tetrahedralLutComputeShader.SetFloat("HorizontalFOV", 131.55f); // Original Paper Value: 143.98570868

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

            AssetDatabase.CreateAsset(outputTexture2DArray, string.Format("Assets/ImprovedCubemapRendering/StaticTetrahedralRenderingV1/Data/CubemapToTetrahedronLUT_{0}.asset", reflectionProbe.resolution));

            //cleanup our mess because we are done!
            Cleanup();
        }
#endif
        //|||||||||||||||||||||||||||||||||||||| RENDER STATIC TETRAHEDRON ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| RENDER STATIC TETRAHEDRON ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| RENDER STATIC TETRAHEDRON ||||||||||||||||||||||||||||||||||||||

        /// <summary>
        /// Main cubemap rendering function
        /// </summary>
        [ContextMenu("RenderStaticTetrahedron")]
        public void RenderStaticTetrahedron()
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

            int computeShaderTetrahedralFaceCombine = tetrahedralRenderingComputeShader.FindKernel("TetrahedralFaceCombineNaive");
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

            renderTextureConverter.SaveRenderTexture2DAsTexture2D(tetrahedronMap, "Assets/ImprovedCubemapRendering/StaticTetrahedralRenderingV1/Data/TetrahedronMap.asset");

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
            tetrahedralRenderingComputeShader.SetTexture(computeShaderTetrahedralMapToCubemap, "TetrahedralCubemapLUT", cubemapToTetrahedralLUT);
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

        //|||||||||||||||||||||||||||||||||||||| DEBUG ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| DEBUG ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| DEBUG ||||||||||||||||||||||||||||||||||||||
        //nothing critical here, this can be ignored.
        //this is logic written that helped during the implementation.
        //since the rotation matricies of the camera ray direction never change, for efficency you can just precompute these.
        //which is what was happening here, building the rotation matricies and getting the final float3x3 matrix value output.

        [ContextMenu("DEBUG: Rotation Matrix Print")]
        public void DebugRotationMatrixPrint()
        {
            RotationMatriciesPrecompute.CalculateRotationMatrix(27.36780516f, 0.0f, 0.0f); //Pitch Yaw Roll (GREEN TOP LEFT QUAD)
            RotationMatriciesPrecompute.CalculateRotationMatrix(27.36780516f, 180.0f, 0.0f); //Pitch Yaw Roll (YELLOW TOP RIGHT QUAD
            RotationMatriciesPrecompute.CalculateRotationMatrix(-27.36780516f, -90.0f, 0.0f); //Pitch Yaw Roll (BLUE BOTTOM LEFT QUAD)
            RotationMatriciesPrecompute.CalculateRotationMatrix(-27.36780516f, 90.0f, 0.0f); //Pitch Yaw Roll (RED BOTTOM RIGHT QUAD)
        }
    }
}