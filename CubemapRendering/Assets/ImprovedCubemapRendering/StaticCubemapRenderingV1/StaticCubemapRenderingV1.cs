using UnityEditor;

using UnityEngine;
using UnityEngine.SceneManagement;

namespace ImprovedCubemapRendering
{
    public class StaticCubemapRenderingV1 : MonoBehaviour
    {
        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||

        //NOTE: since we are in a static context (or baked) we don't have to worry about performance.
        //so if you are in a scene that uses physical light units, you may want to retain floating point precison to avoid errors
        //otherwise, you could probably render in half and not notice a difference.
        [Header("Properties")]
        [Tooltip("Should we render our scene in half precison rather than float?")]
        public bool renderInHalfPrecison;

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

        //|||||||||||||||||||||||||||||||||||||| RENDER STATIC CUBEMAP ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| RENDER STATIC CUBEMAP ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| RENDER STATIC CUBEMAP ||||||||||||||||||||||||||||||||||||||

        /// <summary>
        /// Main cubemap rendering function
        /// </summary>
        [ContextMenu("RenderStaticCubemap")]
        public void RenderStaticCubemap()
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
            probeCameraRender.Create();

            //assign the render target to the camera so when rendering on the camera, it gets fed into our texture
            probeCamera.targetTexture = probeCameraRender;

            //|||||||||||||||||||||||||||||||||||||| RENDER CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //here we actually render the scene in 6 different axis

            //X Positive (X+)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.right, Vector3.up);
            probeCamera.Render();
            Texture2D XPOS = renderTextureConverter.ConvertRenderTexture2DToTexture2D(probeCameraRender, false, true);

            //X Negative (X-)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.left, Vector3.up);
            probeCamera.Render();
            Texture2D XNEG = renderTextureConverter.ConvertRenderTexture2DToTexture2D(probeCameraRender, false, true);

            //Y Positive (Y+)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.up);
            probeCamera.Render();
            Texture2D YPOS = renderTextureConverter.ConvertRenderTexture2DToTexture2D(probeCameraRender, false, true);

            //Y Negative (Y-)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.up);
            probeCamera.Render();
            Texture2D YNEG = renderTextureConverter.ConvertRenderTexture2DToTexture2D(probeCameraRender, false, true);

            //Z Positive (Z+)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
            probeCamera.Render();
            Texture2D ZPOS = renderTextureConverter.ConvertRenderTexture2DToTexture2D(probeCameraRender, false, true);

            //Z Negative (Z-)
            probeCameraGameObject.transform.rotation = Quaternion.LookRotation(Vector3.back, Vector3.up);
            probeCamera.Render();
            Texture2D ZNEG = renderTextureConverter.ConvertRenderTexture2DToTexture2D(probeCameraRender, false, true);

            //we are done rendering, and since we converted each of the rendered faces into a texture2D we don't need this render texture anymore
            probeCameraRender.Release();

            //|||||||||||||||||||||||||||||||||||||| COMBINE RENDERED CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| COMBINE RENDERED CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| COMBINE RENDERED CUBEMAP FACES ||||||||||||||||||||||||||||||||||||||
            //here we combine our rendered faces into a cubemap!

            //create a new texture2D asset.
            //here we will stack each of the rendered cubemap faces horizontally.
            //unity can accept a number of different cubemap layout conventions, but stacking all 6 horizontally is the easiest/simplest
            Texture2D newCubemapRender = new Texture2D(reflectionProbe.resolution * 6, reflectionProbe.resolution, textureFormat, false);

            //iterate for every horizontal pixel
            for (int x = 0; x < newCubemapRender.width; x++)
            {
                //iterate for every vertical pixel
                for (int y = 0; y < newCubemapRender.height; y++)
                {
                    //compute normalized UV coordinates
                    float xNormalized = (1.0f / newCubemapRender.width) * x;
                    float yNormalized = (1.0f / newCubemapRender.height) * y;

                    //NOTE TO SELF: Might need to look into changing the rotation axis's when capturing so we can avoid doing this later
                    yNormalized = 1.0f - yNormalized; //flip as for whatever reason when combining into a final cubemap later the faces are flipped.

                    //compute a normalized X coordinate (so for each "face" we are on, it's 0..1 relative to each face)
                    float xNormalizedFace = (xNormalized * 6.0f) % 1.0f;
                    int xNormalizedFaceInt = (int)(xNormalizedFace * newCubemapRender.height);

                    //compute face index (remember we are doing a 6:1 cubemap layout)
                    int faceIndex = ((int)(xNormalized * 6));

                    //with each face index, sample the corresponding cubemap face and add it to our final cubemap
                    switch (faceIndex)
                    {
                        case 0:
                            newCubemapRender.SetPixel(x, y, XPOS.GetPixel(xNormalizedFaceInt, y));
                            break;
                        case 1:
                            newCubemapRender.SetPixel(x, y, XNEG.GetPixel(xNormalizedFaceInt, y));
                            break;
                        case 2:
                            newCubemapRender.SetPixel(x, y, YPOS.GetPixel(xNormalizedFaceInt, y));
                            break;
                        case 3:
                            newCubemapRender.SetPixel(x, y, YNEG.GetPixel(xNormalizedFaceInt, y));
                            break;
                        case 4:
                            newCubemapRender.SetPixel(x, y, ZPOS.GetPixel(xNormalizedFaceInt, y));
                            break;
                        case 5:
                            newCubemapRender.SetPixel(x, y, ZNEG.GetPixel(xNormalizedFaceInt, y));
                            break;
                    }
                }
            }

            //apply changes
            newCubemapRender.Apply();

            //|||||||||||||||||||||||||||||||||||||| SAVE FINAL CUBEMAP TO DISK ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SAVE FINAL CUBEMAP TO DISK ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SAVE FINAL CUBEMAP TO DISK ||||||||||||||||||||||||||||||||||||||
            //here we save our final cubemap into the project/disk!
            //NOTE: you could save as a .asset file, however an .exr or other common image format is more user/artist friendly and you can be able to edit it externally if you wanted to.

            byte[] newCubemapRenderEXR = newCubemapRender.EncodeToEXR();

            //NOTE TO SELF: Application.dataPath returns the absolute system path up to the Assets folder
            string systemAssetPath = string.Format("{0}/ImprovedCubemapRendering/StaticCubemapRenderingV1/Data/{1}_{2}.exr", Application.dataPath, SceneManager.GetActiveScene().name, gameObject.name);
            string unityAssetPath = string.Format("Assets/ImprovedCubemapRendering/StaticCubemapRenderingV1/Data/{0}_{1}.exr", SceneManager.GetActiveScene().name, gameObject.name);

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

            //|||||||||||||||||||||||||||||||||||||| FINISHED! ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| FINISHED! ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| FINISHED! ||||||||||||||||||||||||||||||||||||||

            //load the final imported cubemap so we can assign it to the reflection probe
            Texture finalCubemapAsset = AssetDatabase.LoadAssetAtPath<Texture>(unityAssetPath);
            reflectionProbe.customBakedTexture = finalCubemapAsset;

            //cleanup our mess because we are done!
            Cleanup();
        }
    }
}