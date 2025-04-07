//||||||||||||||||||||||||| CUBEMAP TEXEL TO RAY DIRECTION |||||||||||||||||||||||||
//||||||||||||||||||||||||| CUBEMAP TEXEL TO RAY DIRECTION |||||||||||||||||||||||||
//||||||||||||||||||||||||| CUBEMAP TEXEL TO RAY DIRECTION |||||||||||||||||||||||||
//slimmed down version from SRP Core Sampling.hlsl - https://github.com/needle-mirror/com.unity.render-pipelines.core/blob/master/ShaderLibrary/Sampling/Sampling.hlsl

float3 CubemapTexelToDirection(float2 positionNVC, uint faceId)
{
    float3 dir = float3(0, 0, 0);
    
    switch (faceId)
    {
        case 0: //XPOS face
            dir = float3(0.0, 0.0, -1.0) * positionNVC.x + float3(0.0, -1.0, 0.0) * positionNVC.y + float3(1.0, 0.0, 0.0);
            break;
        case 1: //XNEG face
            dir = float3(0.0, 0.0, 1.0) * positionNVC.x + float3(0.0, -1.0, 0.0) * positionNVC.y + float3(-1.0, 0.0, 0.0);
            break;
        case 2: //YPOS face
            dir = float3(1.0, 0.0, 0.0) * positionNVC.x + float3(0.0, 0.0, 1.0) * positionNVC.y + float3(0.0, 1.0, 0.0);
            break;
        case 3: //YNEG face
            dir = float3(1.0, 0.0, 0.0) * positionNVC.x + float3(0.0, 0.0, -1.0) * positionNVC.y + float3(0.0, -1.0, 0.0);
            break;
        case 4: //ZPOS face
            dir = float3(1.0, 0.0, 0.0) * positionNVC.x + float3(0.0, -1.0, 0.0) * positionNVC.y + float3(0.0, 0.0, 1.0);
            break;
        case 5: //ZNEG face
            dir = float3(-1.0, 0.0, 0.0) * positionNVC.x + float3(0.0, -1.0, 0.0) * positionNVC.y + float3(0.0, 0.0, -1.0);
            break;
    }
    
    return normalize(dir);
}

//||||||||||||||||||||||||| RAY DIRECTION TO CUBEMAP TEXEL |||||||||||||||||||||||||
//||||||||||||||||||||||||| RAY DIRECTION TO CUBEMAP TEXEL |||||||||||||||||||||||||
//||||||||||||||||||||||||| RAY DIRECTION TO CUBEMAP TEXEL |||||||||||||||||||||||||
//custom function that reverses a given ray direction, into a TEX2DArray cubemap coordinate

uint3 RayDirectionToCubemapTexel(float3 direction, int cubemapFaceResolution)
{
    uint faceId = 0;
    float2 texelCoord = float2(0.0, 0.0);
    
    //determine which face the direction vector points to
    if (abs(direction.x) >= abs(direction.y) && abs(direction.x) >= abs(direction.z))
    {
        //X
        if (direction.x > 0.0)
        {
            faceId = 0; //XPOS face
            texelCoord = float2(direction.z, direction.y) / direction.x * 0.5 + 0.5;
            texelCoord.x = 1 - texelCoord.x;
            texelCoord.y = 1 - texelCoord.y;
        }
        else
        {
            faceId = 1; //XNEG face
            texelCoord = float2(-direction.z, direction.y) / -direction.x * 0.5 + 0.5;
            texelCoord.x = 1 - texelCoord.x;
            texelCoord.y = 1 - texelCoord.y;
        }
    }
    else if (abs(direction.y) >= abs(direction.x) && abs(direction.y) >= abs(direction.z))
    {
        //Y
        if (direction.y > 0.0)
        {
            faceId = 2; //YPOS face
            texelCoord = float2(direction.x, direction.z) / direction.y * 0.5 + 0.5;
        }
        else
        {
            faceId = 3; //YNEG face
            texelCoord = float2(direction.x, -direction.z) / -direction.y * 0.5 + 0.5;
        }
    }
    else
    {
        //Z
        if (direction.z > 0.0)
        {
            faceId = 4; //ZPOS face
            texelCoord = float2(direction.x, direction.y) / direction.z * 0.5 + 0.5;
            texelCoord.y = 1 - texelCoord.y;

        }
        else
        {
            faceId = 5; //ZNEG face
            texelCoord = float2(-direction.x, direction.y) / -direction.z * 0.5 + 0.5;
            texelCoord.y = 1 - texelCoord.y;
        }
    }
    
    //clamp texel coordinates to ensure they are within [0, 1]
    texelCoord = saturate(texelCoord);
    
    //return texel coordinates as uint3(texelCoord, faceId)
    return uint3(texelCoord * cubemapFaceResolution, faceId);
}

void DirectionToCubemapUV(float3 dir, out uint face, out float2 uv)
{
    float3 absDir = abs(dir);
    if (absDir.x > absDir.y && absDir.x > absDir.z)
    {
        face = dir.x > 0 ? 0 : 1;
        uv = (dir.x > 0 ? float2(-dir.z, -dir.y) : float2(dir.z, -dir.y)) / absDir.x;
    }
    else if (absDir.y > absDir.z)
    {
        face = dir.y > 0 ? 2 : 3;
        uv = (dir.y > 0 ? float2(dir.x, dir.z) : float2(dir.x, -dir.z)) / absDir.y;
    }
    else
    {
        face = dir.z > 0 ? 4 : 5;
        uv = (dir.z > 0 ? float2(dir.x, -dir.y) : float2(-dir.x, -dir.y)) / absDir.z;
    }
    uv = uv * 0.5 + 0.5; // [-1,1] -> [0,1]
}

//||||||||||||||||||||||||| CUBEMAP FACE INDEX TO TANGENT/BI-TANGENTS |||||||||||||||||||||||||
//||||||||||||||||||||||||| CUBEMAP FACE INDEX TO TANGENT/BI-TANGENTS |||||||||||||||||||||||||
//||||||||||||||||||||||||| CUBEMAP FACE INDEX TO TANGENT/BI-TANGENTS |||||||||||||||||||||||||

void GetCubemapFaceBasis(uint face, out float3 tangent, out float3 bitangent)
{
    switch (face)
    {
        case 0:
            tangent = float3(0, 0, -1);
            bitangent = float3(0, -1, 0);
            break; // +X
        case 1:
            tangent = float3(0, 0, 1);
            bitangent = float3(0, -1, 0);
            break; // -X
        case 2:
            tangent = float3(1, 0, 0);
            bitangent = float3(0, 0, 1);
            break; // +Y
        case 3:
            tangent = float3(1, 0, 0);
            bitangent = float3(0, 0, -1);
            break; // -Y
        case 4:
            tangent = float3(1, 0, 0);
            bitangent = float3(0, -1, 0);
            break; // +Z
        case 5:
            tangent = float3(-1, 0, 0);
            bitangent = float3(0, -1, 0);
            break; // -Z
    }
}