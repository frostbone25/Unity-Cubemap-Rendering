using UnityEngine;

/*
 * REFERENCE - https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Shader.PropertyToID.html
 * Using Shader.PropertyToID is more efficent than just passing regular strings according to unity docs
*/

namespace ImprovedCubemapRendering
{
    public static class RealtimeTetrahedralRenderingV2ShaderIDs
    {
        public static int TetrahedronFaceResolution = Shader.PropertyToID("TetrahedronFaceResolution");
        public static int TetrahedronMapResolution = Shader.PropertyToID("TetrahedronMapResolution");
        public static int TetrahedronFaceIndex = Shader.PropertyToID("TetrahedronFaceIndex");
        public static int TetrahedronFaceRender = Shader.PropertyToID("TetrahedronFaceRender");
        public static int TetrahedronFaceMapOutput = Shader.PropertyToID("TetrahedronFaceMapOutput");
        public static int TetrahedralColorMap = Shader.PropertyToID("TetrahedralColorMap");
        public static int TetrahedralCubemapLUT = Shader.PropertyToID("TetrahedralCubemapLUT");
        public static int CubemapOutput = Shader.PropertyToID("CubemapOutput");

        public static int CubemapInput = Shader.PropertyToID("CubemapInput");
        public static int CubemapMipFaceResolution = Shader.PropertyToID("CubemapMipFaceResolution");
        public static int SpecularConvolutionSamples = Shader.PropertyToID("SpecularConvolutionSamples");
        public static int SpecularRoughness = Shader.PropertyToID("SpecularRoughness");
        public static int GaussianSampleRadius = Shader.PropertyToID("GaussianSampleRadius");
    }
}