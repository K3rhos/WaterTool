HEADER
{
	Description = "Water Clipmap Grid Compute Shader";
}

MODES
{
	Default();
}

COMMON
{
	#include "common/shared.hlsl"
}

CS
{
	struct WaterVertex
	{
		float3 Position;
		float3 Normal;
		float4 Tangent;
		float2 TexCoord;
		float4 Color;
	};
	
    RWStructuredBuffer<WaterVertex> VertexBuffer < Attribute("VertexBuffer"); > ;

    bool DiscMode < Attribute("DiscMode"); Default(0); > ;

	int VertexOffset < Attribute("VertexOffset"); Default(0); >;
	int GridWidth < Attribute("GridWidth"); Default(64); >;
    float CellSize < Attribute("CellSize"); Default(8); > ;
    float2 SnapPosition < Attribute("SnapPosition"); Default2(0, 0); > ;
	float WaterZ < Attribute("WaterZ"); Default(0); >;
	float TilingScale < Attribute("TilingScale"); Default(0.01); >;
	bool ClampToBounds < Attribute("ClampToBounds"); Default(1); >;

    // Water quad world-space bounds
    float2 BoundsMin < Attribute("BoundsMin"); Default2(-5000, -5000); > ;
    float2 BoundsMax < Attribute("BoundsMax"); Default2(5000, 5000); > ;

	int CircleSegments < Attribute("CircleSegments"); Default(16); >;
	float2 CircleCenter < Attribute("CircleCenter"); Default2(0, 0); >;
	float CircleRadius < Attribute("CircleRadius"); Default(5000); >;

	[numthreads(64, 1, 1)]
	void MainCs(uint3 id : SV_DispatchThreadID)
	{
		WaterVertex v;
		v.Normal = float3(0, 0, 1);
		v.Tangent = float4(1, 0, 0, 1);
		v.Color = float4(1, 1, 1, 1);

		if (DiscMode)
		{
			if (id.x >= (uint)(CircleSegments + 1))
				return;

			if (id.x == 0)
			{
                // Center vertex
                v.Position = float3(CircleCenter.x, CircleCenter.y, WaterZ);
                v.TexCoord = float2(CircleCenter.x, CircleCenter.y) * TilingScale;
			}
			else
			{
				// Edge vertex — evenly spaced around the circle
                float angle = (float)(id.x - 1) / (float)CircleSegments * 6.28318530718f;
                float ex = CircleCenter.x + cos(angle) * CircleRadius;
                float ey = CircleCenter.y + sin(angle) * CircleRadius;
				
				v.Position = float3(ex, ey, WaterZ);
				v.TexCoord = float2(ex, ey) * TilingScale;
			}

			VertexBuffer[VertexOffset + (int)id.x] = v;

			return;
		}

		int verticesPerSide = GridWidth + 1;
		int totalVertices = verticesPerSide * verticesPerSide;

		if (id.x >= (uint)totalVertices)
			return;

		int ix = (int)(id.x % (uint)verticesPerSide);
		int iy = (int)(id.x / (uint)verticesPerSide);

		float halfGrid = GridWidth * CellSize * 0.5;

        float wx = SnapPosition.x + ix * CellSize - halfGrid;
        float wy = SnapPosition.y + iy * CellSize - halfGrid;

        // Clamp to water quad bounds — vertices outside collapse to the edge
        // forming degenerate triangles that don't render
        if (ClampToBounds)
        {
            wx = clamp(wx, BoundsMin.x, BoundsMax.x);
            wy = clamp(wy, BoundsMin.y, BoundsMax.y);
        }

		v.Position = float3(wx, wy, WaterZ);
		v.TexCoord = float2(wx, wy) * TilingScale;

		VertexBuffer[VertexOffset + (int)id.x] = v;
	}
}
