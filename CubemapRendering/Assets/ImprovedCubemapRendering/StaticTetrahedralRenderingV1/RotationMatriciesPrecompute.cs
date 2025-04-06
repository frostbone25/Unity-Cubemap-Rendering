using System.Collections;
using System.Collections.Generic;

using UnityEngine;

namespace ImprovedCubemapRendering
{
    public static class RotationMatriciesPrecompute
    {
        // Multiplies two 3x3 matrices represented as 3 Vector3s each (row-major)
        public static Vector3[] MultiplyMatrix3x3(Vector3[] A, Vector3[] B)
        {
            Vector3[] result = new Vector3[3];

            // Transpose B to access its columns easily
            Vector3 bCol0 = new Vector3(B[0].x, B[1].x, B[2].x);
            Vector3 bCol1 = new Vector3(B[0].y, B[1].y, B[2].y);
            Vector3 bCol2 = new Vector3(B[0].z, B[1].z, B[2].z);

            // Perform row * column dot products
            result[0] = new Vector3(Vector3.Dot(A[0], bCol0), Vector3.Dot(A[0], bCol1), Vector3.Dot(A[0], bCol2));
            result[1] = new Vector3(Vector3.Dot(A[1], bCol0), Vector3.Dot(A[1], bCol1), Vector3.Dot(A[1], bCol2));
            result[2] = new Vector3(Vector3.Dot(A[2], bCol0), Vector3.Dot(A[2], bCol1), Vector3.Dot(A[2], bCol2));

            return result;
        }

        public static void CalculateRotationMatrix(float eulerDegreesX, float eulerDegreesY, float eulerDegreesZ)
        {
            Vector3 eulerDegrees = new Vector3(eulerDegreesX, eulerDegreesY, eulerDegreesZ);
            Vector3 eulerRadians = eulerDegrees * Mathf.Deg2Rad;
            Vector3 eulerRadiansSin = new Vector3(Mathf.Sin(eulerRadians.x), Mathf.Sin(eulerRadians.y), Mathf.Sin(eulerRadians.z));
            Vector3 eulerRadiansCos = new Vector3(Mathf.Cos(eulerRadians.x), Mathf.Cos(eulerRadians.y), Mathf.Cos(eulerRadians.z));

            Vector3[] rotationX = new Vector3[3]
            {
                new Vector3(1, 0, 0),
                new Vector3(0, eulerRadiansCos.x, -eulerRadiansSin.x),
                new Vector3(0, eulerRadiansSin.x, eulerRadiansCos.x),
            };

            Vector3[] rotationY = new Vector3[3]
            {
                new Vector3(eulerRadiansCos.y, 0, eulerRadiansSin.y),
                new Vector3(0, 1, 0),
                new Vector3(-eulerRadiansSin.y, 0, eulerRadiansCos.y),
            };

            Vector3[] rotationZ = new Vector3[3]
            {
                new Vector3(eulerRadiansCos.z, -eulerRadiansSin.z, 0),
                new Vector3(eulerRadiansSin.z, eulerRadiansCos.z, 0),
                new Vector3(0, 0, 1),
            };

            Vector3[] rotation = MultiplyMatrix3x3(rotationY, MultiplyMatrix3x3(rotationX, rotationZ));

            string logOutput = "";

            logOutput += string.Format("eulerDegrees: {0} {1} {2} \n", eulerDegrees.x, eulerDegrees.y, eulerDegrees.z);
            logOutput += string.Format("eulerRadians: {0} {1} {2} \n", eulerRadians.x, eulerRadians.y, eulerRadians.z);
            logOutput += string.Format("eulerRadiansSin: {0} {1} {2} \n", eulerRadiansSin.x, eulerRadiansSin.y, eulerRadiansSin.z);
            logOutput += string.Format("eulerRadiansCos: {0} {1} {2} \n", eulerRadiansCos.x, eulerRadiansCos.y, eulerRadiansCos.z);

            logOutput += "\n";
            logOutput += "rotationX \n";
            logOutput += string.Format("{0}, {1}, {2} \n", rotationX[0].x, rotationX[0].y, rotationX[0].z);
            logOutput += string.Format("{0}, {1}, {2} \n", rotationX[1].x, rotationX[1].y, rotationX[1].z);
            logOutput += string.Format("{0}, {1}, {2} \n", rotationX[2].x, rotationX[2].y, rotationX[2].z);

            logOutput += "\n";
            logOutput += "rotationY \n";
            logOutput += string.Format("{0}, {1}, {2}, \n", rotationY[0].x, rotationY[0].y, rotationY[0].z);
            logOutput += string.Format("{0}, {1}, {2}, \n", rotationY[1].x, rotationY[1].y, rotationY[1].z);
            logOutput += string.Format("{0}, {1}, {2}, \n", rotationY[2].x, rotationY[2].y, rotationY[2].z);

            logOutput += "\n";
            logOutput += "rotationZ \n";
            logOutput += string.Format("{0}, {1}, {2}, \n", rotationZ[0].x, rotationZ[0].y, rotationZ[0].z);
            logOutput += string.Format("{0}, {1}, {2}, \n", rotationZ[1].x, rotationZ[1].y, rotationZ[1].z);
            logOutput += string.Format("{0}, {1}, {2}, \n", rotationZ[2].x, rotationZ[2].y, rotationZ[2].z);

            logOutput += "\n";
            logOutput += "rotationMatrix \n";
            logOutput += string.Format("{0}, {1}, {2}, \n", rotation[0].x, rotation[0].y, rotation[0].z);
            logOutput += string.Format("{0}, {1}, {2}, \n", rotation[1].x, rotation[1].y, rotation[1].z);
            logOutput += string.Format("{0}, {1}, {2}, \n", rotation[2].x, rotation[2].y, rotation[2].z);

            Debug.Log(logOutput);
        }
    }
}