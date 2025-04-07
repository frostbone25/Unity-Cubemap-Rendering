using UnityEngine;

/*
 * REFERENCE - https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Shader.PropertyToID.html
 * Using Shader.PropertyToID is more efficent than just passing regular strings according to unity docs
*/

namespace ImprovedCubemapRendering
{
    public static class RealtimeCubemapRenderingShaderIDsV3
    {
        public static int SpecularConvolutionSamples = Shader.PropertyToID("SpecularConvolutionSamples");
        public static int SpecularRoughness = Shader.PropertyToID("SpecularRoughness");
        public static int CubemapFaceIndex = Shader.PropertyToID("CubemapFaceIndex");
        public static int CubemapFaceResolution = Shader.PropertyToID("CubemapFaceResolution");
        public static int CubemapMipFaceResolution = Shader.PropertyToID("CubemapMipFaceResolution");
        public static int CubemapFace = Shader.PropertyToID("CubemapFace");
        public static int CubemapOutput = Shader.PropertyToID("CubemapOutput");
        public static int CubemapInput = Shader.PropertyToID("CubemapInput");
        public static int GaussianSampleRadius = Shader.PropertyToID("GaussianSampleRadius");
        public static int GaussianSampleOffset = Shader.PropertyToID("GaussianSampleOffset");
    }
}