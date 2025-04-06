Shader "Unlit/RayDirectionTruth"
{
    Properties
    {

    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }

        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag


            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 cameraRelativeWorldPosition : TEXCOORD1; 
            };

            v2f vert (appdata v)
            {
                v2f o;

                o.vertex = UnityObjectToClipPos(v.vertex);

                o.cameraRelativeWorldPosition = mul(unity_ObjectToWorld, fixed4(v.vertex.xyz, 1.0)).xyz - _WorldSpaceCameraPos;

                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 cameraWorldPositionViewPlane = i.cameraRelativeWorldPosition.xyz / dot(i.cameraRelativeWorldPosition.xyz, unity_WorldToCamera._m20_m21_m22);
                float3 rayDirection = normalize(cameraWorldPositionViewPlane);
                //rayDirection = pow(rayDirection, 1.0f / 2.2);
                //rayDirection = rayDirection * 0.5 + 0.5;
                return float4(rayDirection.xyz, 1);
            }
            ENDCG
        }
    }
}
