#define PI 3.14159265359f

//||||||||||||||||||||||||||||| UTILITY FUNCTIONS |||||||||||||||||||||||||||||
//||||||||||||||||||||||||||||| UTILITY FUNCTIONS |||||||||||||||||||||||||||||
//||||||||||||||||||||||||||||| UTILITY FUNCTIONS |||||||||||||||||||||||||||||

//https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl
uint BitFieldInsert(uint mask, uint src, uint dst)
{
    return (src & mask) | (dst & ~mask);
}

float CopySign(float x, float s, bool ignoreNegZero = true)
{
    if (ignoreNegZero)
    {
        return (s >= 0) ? abs(x) : -abs(x);
    }
    else
    {
        uint negZero = 0x80000000u;
        uint signBit = negZero & asuint(s);
        return asfloat(BitFieldInsert(negZero, signBit, asuint(x)));
    }
}

float FastSign(float s, bool ignoreNegZero = true)
{
    return CopySign(1.0, s, ignoreNegZero);
}

//https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonLighting.hlsl
float3x3 GetLocalFrame(float3 localZ)
{
    float x = localZ.x;
    float y = localZ.y;
    float z = localZ.z;
    float sz = FastSign(z);
    float a = 1 / (sz + z);
    float ya = y * a;
    float b = x * ya;
    float c = x * sz;

    float3 localX = float3(c * x * a - 1, sz * b, c);
    float3 localY = float3(b, y * ya - sz, y);

    return float3x3(localX, localY, localZ);
}

//||||||||||||||||||||||||| SAMPLING |||||||||||||||||||||||||
//||||||||||||||||||||||||| SAMPLING |||||||||||||||||||||||||
//||||||||||||||||||||||||| SAMPLING |||||||||||||||||||||||||

// ----------------------------------------------------------------------------
// http://holger.dammertz.org/stuff/notes_HammersleyOnHemisphere.html
// efficient VanDerCorpus calculation.
float RadicalInverse_VdC(uint bits)
{
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
    bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
    bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
    return float(bits) * 2.3283064365386963e-10; // / 0x100000000
}
// ----------------------------------------------------------------------------
float2 Hammersley(uint i, uint N)
{
    return float2(float(i) / float(N), RadicalInverse_VdC(i));
}

//||||||||||||||||||||||||||||| PBR SHADING FUNCTIONS |||||||||||||||||||||||||||||
//||||||||||||||||||||||||||||| PBR SHADING FUNCTIONS |||||||||||||||||||||||||||||
//||||||||||||||||||||||||||||| PBR SHADING FUNCTIONS |||||||||||||||||||||||||||||

void SampleAnisoGGXVisibleNormal(float2 u, float3 V, float3x3 localToWorld, float roughnessX, float roughnessY, out float3 localV, out float3 localH, out float VdotH)
{
    localV = mul(V, transpose(localToWorld));

    // Construct an orthonormal basis around the stretched view direction
    float3 N = normalize(float3(roughnessX * localV.x, roughnessY * localV.y, localV.z));
    float3 T = (N.z < 0.9999) ? normalize(cross(float3(0, 0, 1), N)) : float3(1, 0, 0);
    float3 B = cross(N, T);

    // Compute a sample point with polar coordinates (r, phi)
    float r = sqrt(u.x);
    float phi = 2.0 * PI * u.y;
    float t1 = r * cos(phi);
    float t2 = r * sin(phi);
    float s = 0.5 * (1.0 + N.z);
    t2 = (1.0 - s) * sqrt(1.0 - t1 * t1) + s * t2;

    // Reproject onto hemisphere
    localH = t1 * T + t2 * B + sqrt(max(0.0, 1.0 - t1 * t1 - t2 * t2)) * N;

    // Transform the normal back to the ellipsoid configuration
    localH = normalize(float3(roughnessX * localH.x, roughnessY * localH.y, max(0.0, localH.z)));

    VdotH = saturate(dot(localV, localH));
}

// GGX VNDF via importance sampling
half3 ImportanceSampleGGX_VNDF(float2 random, half3 normalWS, half3 viewDirWS, half roughness)
{
    half3x3 localToWorld = GetLocalFrame(normalWS);

    half VdotH;
    half3 localV, localH;
    SampleAnisoGGXVisibleNormal(random, viewDirWS, localToWorld, roughness, roughness, localV, localH, VdotH);

    // Compute the reflection direction
    half3 localL = 2.0 * VdotH * localH - localV;
    half3 outgoingDir = mul(localL, localToWorld);

    half NdotL = dot(normalWS, outgoingDir);

    return outgoingDir;
}