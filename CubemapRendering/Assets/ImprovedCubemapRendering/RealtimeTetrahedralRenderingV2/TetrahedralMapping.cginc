//||||||||||||||||||||||||| UTILITY - ROTATE DIRECTION BY EULER DEGREES |||||||||||||||||||||||||
//||||||||||||||||||||||||| UTILITY - ROTATE DIRECTION BY EULER DEGREES |||||||||||||||||||||||||
//||||||||||||||||||||||||| UTILITY - ROTATE DIRECTION BY EULER DEGREES |||||||||||||||||||||||||

float3 RotateVectorByEuler(float3 direction, float3 eulerDegrees)
{
    float3 eulerRadians = radians(eulerDegrees);
    float3 eulerSin = sin(eulerRadians);
    float3 eulerCos = cos(eulerRadians);

    float3x3 rotationX = float3x3(
        1, 0, 0,
        0, eulerCos.x, -eulerSin.x,
        0, eulerSin.x, eulerCos.x
    );

    float3x3 rotationY = float3x3(
        eulerCos.y, 0, eulerSin.y,
        0, 1, 0,
        -eulerSin.y, 0, eulerCos.y
    );

    float3x3 rotationZ = float3x3(
        eulerCos.z, -eulerSin.z, 0,
        eulerSin.z, eulerCos.z, 0,
        0, 0, 1
    );

    float3x3 rotation = mul(rotationY, mul(rotationX, rotationZ)); // Y * (X * Z)
    
    return mul(rotation, direction);
}

//||||||||||||||||||||||||| UV TO TETRAHEDRAL RAY DIRECTION (NAIVE) |||||||||||||||||||||||||
//||||||||||||||||||||||||| UV TO TETRAHEDRAL RAY DIRECTION (NAIVE) |||||||||||||||||||||||||
//||||||||||||||||||||||||| UV TO TETRAHEDRAL RAY DIRECTION (NAIVE) |||||||||||||||||||||||||
/*
float3 GetNaiveTetrahedronRayDirectionFromUV(float2 uv, float horizontalFOV, float verticalFOV)
{
    float3 tetrahedronRayDirection = float3(0, 0, 0);
    
    float tanHalfVertFOV = tan(radians(verticalFOV * 0.5f));
    float tanHalfHorzFOV = tan(radians(horizontalFOV * 0.5f));

    //GREEN TOP LEFT QUAD
    if (uv.x < 0.5f && uv.y > 0.5f)
    {
        float2 localUV = float2(uv.x * 2.0f, uv.y * 2.0f - 1.0f);
        float2 localNormalizedDeviceCoordinates = localUV * 2.0f - 1.0f;

        //calculate camera ray direction
        tetrahedronRayDirection = float3(localNormalizedDeviceCoordinates.x * tanHalfHorzFOV, localNormalizedDeviceCoordinates.y * tanHalfVertFOV, 1.0f);
        tetrahedronRayDirection = normalize(tetrahedronRayDirection);

        //rotate camera ray direction
        tetrahedronRayDirection = RotateVectorByEuler(tetrahedronRayDirection, float3(27.36780516f, 0.0f, 0.0f)); //Pitch Yaw Roll
        tetrahedronRayDirection = normalize(tetrahedronRayDirection);
    }
    //YELLOW TOP RIGHT QUAD
    else if (uv.x > 0.5f && uv.y > 0.5f)
    {
        float2 localUV = float2(uv.x * 2.0f - 1.0f, uv.y * 2.0f - 1.0f);
        float2 localNormalizedDeviceCoordinates = localUV * 2.0f - 1.0f;

        //calculate camera ray direction
        tetrahedronRayDirection = float3(localNormalizedDeviceCoordinates.x * tanHalfHorzFOV, localNormalizedDeviceCoordinates.y * tanHalfVertFOV, 1.0f);
        tetrahedronRayDirection = normalize(tetrahedronRayDirection);

        //rotate camera ray direction
        tetrahedronRayDirection = RotateVectorByEuler(tetrahedronRayDirection, float3(27.36780516f, 180.0f, 0.0f)); //Pitch Yaw Roll
        tetrahedronRayDirection = normalize(tetrahedronRayDirection);
    }
    //BLUE BOTTOM LEFT QUAD
    else if (uv.x < 0.5f && uv.y < 0.5f)
    {
        float2 localUV = float2(uv.x * 2.0f, uv.y * 2.0f);
        float2 localNormalizedDeviceCoordinates = localUV * 2.0f - 1.0f;

        //calculate camera ray direction
        tetrahedronRayDirection = float3(localNormalizedDeviceCoordinates.x * tanHalfHorzFOV, localNormalizedDeviceCoordinates.y * tanHalfVertFOV, 1.0f);
        tetrahedronRayDirection = normalize(tetrahedronRayDirection);

        //rotate camera ray direction
        tetrahedronRayDirection = RotateVectorByEuler(tetrahedronRayDirection, float3(-27.36780516f, -90.0f, 0.0f)); //Pitch Yaw Roll
        tetrahedronRayDirection = normalize(tetrahedronRayDirection);
    }
    //RED TOP RIGHT QUAD
    else if (uv.x > 0.5f && uv.y < 0.5f)
    {
        float2 localUV = float2(uv.x * 2.0f - 1.0f, uv.y * 2.0f);
        float2 localNormalizedDeviceCoordinates = localUV * 2.0f - 1.0f;

        //calculate camera ray direction
        tetrahedronRayDirection = float3(localNormalizedDeviceCoordinates.x * tanHalfHorzFOV, localNormalizedDeviceCoordinates.y * tanHalfVertFOV, 1.0f);
        tetrahedronRayDirection = normalize(tetrahedronRayDirection);

        //rotate camera ray direction
        tetrahedronRayDirection = RotateVectorByEuler(tetrahedronRayDirection, float3(-27.36780516f, 90.0f, 0.0f)); //Pitch Yaw Roll
        tetrahedronRayDirection = normalize(tetrahedronRayDirection);
    }

    return tetrahedronRayDirection;
}
*/

//Precomputed Euler Degree Rotation Matrix of... 
//float3(27.36780516f, 0.0f, 0.0f)); //Pitch Yaw Roll
static const float3x3 GreenTopLeftQuadRotation = float3x3(
    1, 0, 0,
    0, 0.8880739, -0.4597008,
    0, 0.4597008, 0.8880739
);

//Precomputed Euler Degree Rotation Matrix of... 
//float3(27.36780516f, 180.0f, 0.0f)); //Pitch Yaw Roll
static const float3x3 YellowTopRightQuadRotation = float3x3(
    -1, -4.018832E-08, -7.763789E-08,
    0, 0.8880739, -0.4597008,
    8.742278E-08, -0.4597008, -0.8880739
);

//Precomputed Euler Degree Rotation Matrix of... 
//float3(-27.36780516f, -90.0f, 0.0f)); //Pitch Yaw Roll
static const float3x3 BlueBottomLeftQuadRotation = float3x3(
    -4.371139E-08, 0.4597008, -0.8880739,
    0, 0.8880739, 0.4597008,
    1, 2.009416E-08, -3.881894E-08
);

//Precomputed Euler Degree Rotation Matrix of... 
//float3(-27.36780516f, 90.0f, 0.0f)); //Pitch Yaw Roll
static const float3x3 RedBottomRightQuadRotation = float3x3(
    -4.371139E-08, -0.4597008, 0.8880739,
    0, 0.8880739, 0.4597008,
    -1, 2.009416E-08, -3.881894E-08
);

float3 GetNaiveTetrahedronRayDirectionFromUV(float2 uv, float horizontalFOV, float verticalFOV)
{
    float3 tetrahedronRayDirection = float3(0, 0, 0);
    
    float tanHalfVertFOV = tan(radians(verticalFOV * 0.5f));
    float tanHalfHorzFOV = tan(radians(horizontalFOV * 0.5f));

    //GREEN TOP LEFT QUAD
    if (uv.x < 0.5f && uv.y > 0.5f)
    {
        float2 localUV = float2(uv.x * 2.0f, uv.y * 2.0f - 1.0f);
        float2 localNormalizedDeviceCoordinates = localUV * 2.0f - 1.0f;

        //calculate camera ray direction
        tetrahedronRayDirection = float3(localNormalizedDeviceCoordinates.x * tanHalfHorzFOV, localNormalizedDeviceCoordinates.y * tanHalfVertFOV, 1.0f);
        tetrahedronRayDirection = normalize(tetrahedronRayDirection);

        //rotate camera ray direction
        tetrahedronRayDirection = mul(GreenTopLeftQuadRotation, tetrahedronRayDirection);
    }
    //YELLOW TOP RIGHT QUAD
    else if (uv.x > 0.5f && uv.y > 0.5f)
    {
        float2 localUV = float2(uv.x * 2.0f - 1.0f, uv.y * 2.0f - 1.0f);
        float2 localNormalizedDeviceCoordinates = localUV * 2.0f - 1.0f;

        //calculate camera ray direction
        tetrahedronRayDirection = float3(localNormalizedDeviceCoordinates.x * tanHalfHorzFOV, localNormalizedDeviceCoordinates.y * tanHalfVertFOV, 1.0f);
        tetrahedronRayDirection = normalize(tetrahedronRayDirection);

        //rotate camera ray direction
        tetrahedronRayDirection = mul(YellowTopRightQuadRotation, tetrahedronRayDirection);
    }
    //BLUE BOTTOM LEFT QUAD
    else if (uv.x < 0.5f && uv.y < 0.5f)
    {
        float2 localUV = float2(uv.x * 2.0f, uv.y * 2.0f);
        float2 localNormalizedDeviceCoordinates = localUV * 2.0f - 1.0f;

        //calculate camera ray direction
        tetrahedronRayDirection = float3(localNormalizedDeviceCoordinates.x * tanHalfHorzFOV, localNormalizedDeviceCoordinates.y * tanHalfVertFOV, 1.0f);
        tetrahedronRayDirection = normalize(tetrahedronRayDirection);

        //rotate camera ray direction
        tetrahedronRayDirection = mul(BlueBottomLeftQuadRotation, tetrahedronRayDirection);
    }
    //RED BOTTOM RIGHT QUAD
    else if (uv.x > 0.5f && uv.y < 0.5f)
    {
        float2 localUV = float2(uv.x * 2.0f - 1.0f, uv.y * 2.0f);
        float2 localNormalizedDeviceCoordinates = localUV * 2.0f - 1.0f;

        //calculate camera ray direction
        tetrahedronRayDirection = float3(localNormalizedDeviceCoordinates.x * tanHalfHorzFOV, localNormalizedDeviceCoordinates.y * tanHalfVertFOV, 1.0f);
        tetrahedronRayDirection = normalize(tetrahedronRayDirection);

        //rotate camera ray direction
        tetrahedronRayDirection = mul(RedBottomRightQuadRotation, tetrahedronRayDirection);
    }

    return tetrahedronRayDirection;
}

//||||||||||||||||||||||||| UV TO TETRAHEDRAL RAY DIRECTION (COMPACT) |||||||||||||||||||||||||
//||||||||||||||||||||||||| UV TO TETRAHEDRAL RAY DIRECTION (COMPACT) |||||||||||||||||||||||||
//||||||||||||||||||||||||| UV TO TETRAHEDRAL RAY DIRECTION (COMPACT) |||||||||||||||||||||||||

// Function to determine if a point is inside a triangle using barycentric coordinates
bool IsPointInTriangle(float2 samplePoint, float2 pointA, float2 pointB, float2 pointC)
{
    // Calculate barycentric coordinates
    float d = (pointB.y - pointC.y) * (pointA.x - pointC.x) + (pointC.x - pointB.x) * (pointA.y - pointC.y);

    // First barycentric coordinate
    float alpha = ((pointB.y - pointC.y) * (samplePoint.x - pointC.x) + (pointC.x - pointB.x) * (samplePoint.y - pointC.y)) / d;

    // Second barycentric coordinate
    float beta = ((pointC.y - pointA.y) * (samplePoint.x - pointC.x) + (pointA.x - pointC.x) * (samplePoint.y - pointC.y)) / d;

    // Third barycentric coordinate
    float gamma = 1.0f - alpha - beta;

    // If all coordinates are between 0 and 1, the point is inside the triangle
    return alpha >= 0.0f && beta >= 0.0f && gamma >= 0.0f;
}

float3 GetCompactTetrahedronRayDirectionFromUV(float2 uv, float horizontalFOV, float verticalFOV)
{
    float3 tetrahedronRayDirection = float3(0, 0, 0);

    float tanHalfVertFOV = tan(radians(verticalFOV * 0.5f));
    float tanHalfHorzFOV = tan(radians(horizontalFOV * 0.5f));

    //YELLOW TOP TRIANGLE (CORRECT)
    if (IsPointInTriangle(uv, float2(0.0f, 1.0f), float2(0.5f, 0.5f), float2(1.0f, 1.0f)))
    {
        tetrahedronRayDirection = float3(1, 1, 0);

        float2 localUV = float2(uv.x, uv.y * 2.0f - 1.0f);
        localUV.y = 1.0f - localUV.y;
        float2 localNormalizedDeviceCoordinates = localUV * 2.0f - 1.0f;

        //calculate camera ray direction
        tetrahedronRayDirection = float3(localNormalizedDeviceCoordinates.x * tanHalfHorzFOV, localNormalizedDeviceCoordinates.y * tanHalfVertFOV, 1.0f);
        tetrahedronRayDirection = normalize(tetrahedronRayDirection);

        //rotate camera ray direction
        tetrahedronRayDirection = RotateVectorByEuler(tetrahedronRayDirection, float3(27.36780516f, 180.0f, 0.0f)); //Pitch Yaw Roll
    }
    //GREEN BOTTOM TRIANGLE (CORRECT)
    else if (IsPointInTriangle(uv, float2(0.0f, 0.0f), float2(0.5f, 0.5f), float2(1.0f, 0.0f)))
    {
        tetrahedronRayDirection = float3(0, 1, 0);

        float2 localUV = float2(uv.x, uv.y * 2.0f);
        float2 localNormalizedDeviceCoordinates = localUV * 2.0f - 1.0f;

        //calculate camera ray direction
        tetrahedronRayDirection = float3(localNormalizedDeviceCoordinates.x * tanHalfHorzFOV, localNormalizedDeviceCoordinates.y * tanHalfVertFOV, 1.0f);
        tetrahedronRayDirection = normalize(tetrahedronRayDirection);

        //rotate camera ray direction
        tetrahedronRayDirection = RotateVectorByEuler(tetrahedronRayDirection, float3(27.36780516f, 0.0f, 0.0f)); //Pitch Yaw Roll
    }
    //RED LEFT TRIANGLE (NOT CORRECT)
    else if (IsPointInTriangle(uv, float2(0.0f, 1.0f), float2(0.5f, 0.5f), float2(0.0f, 0.0f)))
    {
        tetrahedronRayDirection = float3(1, 0, 0);

        float2 localUV = float2(uv.x * 2.0f, uv.y);
        float2 localNormalizedDeviceCoordinates = localUV * 2.0f - 1.0f;

        //calculate camera ray direction
        tetrahedronRayDirection = float3(localNormalizedDeviceCoordinates.x * tanHalfVertFOV, localNormalizedDeviceCoordinates.y * tanHalfHorzFOV, 1.0f);
        tetrahedronRayDirection = normalize(tetrahedronRayDirection);

        //rotate camera ray direction
        tetrahedronRayDirection = RotateVectorByEuler(tetrahedronRayDirection, float3(-27.36780516f, 90.0f, 0.0f)); //Pitch Yaw Roll
        tetrahedronRayDirection = RotateVectorByEuler(tetrahedronRayDirection, float3(-90.0f, -27.36780516f, 0.0f)); //Pitch Yaw Roll
        tetrahedronRayDirection = RotateVectorByEuler(tetrahedronRayDirection, float3(0.0f, 0.0f, 27.36780516f)); //Pitch Yaw Roll
    }
    //BLUE RIGHT TRIANGLE (NOT CORRECT)
    else if (IsPointInTriangle(uv, float2(1.0f, 1.0f), float2(0.5f, 0.5f), float2(1.0f, 0.0f)))
    {
        tetrahedronRayDirection = float3(0, 0, 1);

        float2 localUV = float2(uv.x * 2.0f - 1.0f, uv.y);
        float2 localNormalizedDeviceCoordinates = localUV * 2.0f - 1.0f;

        //calculate camera ray direction
        tetrahedronRayDirection = float3(localNormalizedDeviceCoordinates.x * tanHalfVertFOV, localNormalizedDeviceCoordinates.y * tanHalfHorzFOV, 1.0f);
        tetrahedronRayDirection = normalize(tetrahedronRayDirection);

        //rotate camera ray direction
        tetrahedronRayDirection = RotateVectorByEuler(tetrahedronRayDirection, float3(27.36780516f, -90.0f, -90.0f)); //Pitch Yaw Roll
        tetrahedronRayDirection = RotateVectorByEuler(tetrahedronRayDirection, float3(180.0f, 0.0f, 0.0f)); //Pitch Yaw Roll
    }

    return tetrahedronRayDirection;
}