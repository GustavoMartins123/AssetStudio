using System;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace AssetStudio.Avalonia.Services;

internal static class MaterialPreviewGeometry
{
    internal static (NumericsVector3[] Positions, NumericsVector2[] Uvs, int[] Indices) CreateSphere(int latitudes, int longitudes)
    {
        int stride = longitudes + 1;
        var positions = new NumericsVector3[(latitudes + 1) * stride];
        var uvs = new NumericsVector2[positions.Length];
        var indices = new int[latitudes * longitudes * 6];

        for (int lat = 0; lat <= latitudes; lat++)
        {
            float theta = lat * MathF.PI / latitudes;
            float sinTheta = MathF.Sin(theta);
            float cosTheta = MathF.Cos(theta);
            for (int lon = 0; lon <= longitudes; lon++)
            {
                float phi = lon * MathF.Tau / longitudes;
                int index = lat * stride + lon;
                positions[index] = new NumericsVector3(MathF.Cos(phi) * sinTheta, cosTheta, MathF.Sin(phi) * sinTheta);
                uvs[index] = new NumericsVector2((float)lon / longitudes, 1f - (float)lat / latitudes);
            }
        }

        int written = 0;
        for (int lat = 0; lat < latitudes; lat++)
        {
            for (int lon = 0; lon < longitudes; lon++)
            {
                int top = lat * stride + lon;
                int bottom = top + stride;
                indices[written++] = top;
                indices[written++] = bottom;
                indices[written++] = top + 1;
                indices[written++] = bottom;
                indices[written++] = bottom + 1;
                indices[written++] = top + 1;
            }
        }

        return (positions, uvs, indices);
    }
}
