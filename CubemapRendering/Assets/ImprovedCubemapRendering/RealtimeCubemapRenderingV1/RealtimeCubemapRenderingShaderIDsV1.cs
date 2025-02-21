using UnityEngine;

/*
 * REFERENCE - https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Shader.PropertyToID.html
 * Using Shader.PropertyToID is more efficent than just passing regular strings according to unity docs
*/

namespace ImprovedCubemapRendering
{
    public static class RealtimeCubemapRenderingShaderIDsV1
    {
        public static int InputResolutionSquare = Shader.PropertyToID("InputResolutionSquare");
        public static int Input = Shader.PropertyToID("Input");
        public static int Output = Shader.PropertyToID("Output");
    }
}