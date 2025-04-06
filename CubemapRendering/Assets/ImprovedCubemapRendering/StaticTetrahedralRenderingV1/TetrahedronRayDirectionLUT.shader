Shader "Unlit/TetrahedronRayDirectionLUT"
{
    Properties
    {
        [Header(Properties)]
        _FOV_X("FOV X", Float) = 143.98570868
        _FOV_Y("FOV Y", Float) = 125.27438968
        _FOV_Overdraw("FOV Overdraw", Float) = 1

        [Header(Ground Truth)]
        _GroundTruth("Ground Truth", 2D) = "white"
        [Toggle(_CHECK)] _Check("Check Ground Truth", Float) = 0

        [Header(Debug Value Checking)]
        _Add("Add", Float) = 0
        _Multiply("Multiply", Float) = 1

        [Header(LUT Cubemap Test)]
        [Toggle(_CHECK_CUBE)] _CheckCube("Check Ground Truth", Float) = 0
        _CubemapTest("Cubemap Test", CUBE) = "white"
    }
    SubShader
    {
        Tags 
        { 
            "RenderType" = "Opaque" 
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma shader_feature_local _CHECK
            #pragma shader_feature_local _CHECK_CUBE

            #include "UnityCG.cginc"
            #include "TetrahedralMapping.cginc"

            float _FOV_X;
            float _FOV_Y;
            float _FOV_Overdraw;

            sampler2D _GroundTruth;
            float _Add;
            float _Multiply;

            samplerCUBE _CubemapTest;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;

                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;

                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                #if defined (_CHECK)
                    return (tex2D(_GroundTruth, i.uv) + _Add) * _Multiply;
                #endif

                float4 finalColor = float4(0, 0, 0, 0);
                float2 uv = i.uv;

                //NOTE TO SELF: the provided horizontal FOV is apparently off by a wee bit
                //To get it to match the ground truth, this is the value I ended up with was 131.55f
                //This was achieved by exposing the value and eye-ball adjusting it to be as close as I could get it to the ground truth.

                float horizontalFOV = _FOV_X; //131.55 | Original: 143.98570868
                float verticalFOV = _FOV_Y * _FOV_Overdraw; //125.27438968

                //float3 rayDirection = GetNaiveTetrahedronRayDirectionFromUV(uv, horizontalFOV, verticalFOV);
                float3 rayDirection = GetCompactTetrahedronRayDirectionFromUV(uv, horizontalFOV, verticalFOV);

                //float2 reconstructedUV = GetNaiveTetrahedronUVFromRayDirection(rayDirection, horizontalFOV, verticalFOV);
                //float2 reconstructedUV = GetNaiveTetrahedronUVFromRayDirection(uv, rayDirection, horizontalFOV, verticalFOV);

                finalColor.rgb = rayDirection;
                //finalColor.rg = reconstructedUV;

                #if defined (_CHECK_CUBE)
                    return texCUBElod(_CubemapTest, float4(rayDirection, 0));
                #endif

                finalColor += _Add;
                finalColor *= _Multiply;

                return finalColor;
            }
            ENDCG
        }
    }
}
