using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;
using Sandbox.Rendering;

namespace RedSnail.WaterTool;

[Icon("water"), Group("Water"), Title("Water Quad")]
public sealed class WaterQuad : Component, Component.ExecuteInEditor, Component.DontExecuteOnServer
{
	#pragma warning disable CS0649

	private struct WaterVertex
	{
		[VertexLayout.Position] public Vector3 Position;
		[VertexLayout.Normal] public Vector3 Normal;
		[VertexLayout.Tangent] public Vector4 Tangent;
		[VertexLayout.TexCoord] public Vector2 TexCoord;
		[VertexLayout.Color] public Color Color;
	}

	#pragma warning restore CS0649

	// GPU buffers (per-quad, owned here — WaterQuadManager owns SceneCustomObject and ComputeShader)
	private GpuBuffer<WaterVertex> m_VertexBuffer;
	private GpuBuffer<uint> m_IndexBuffer;
	private int m_TotalIndexCount;
	private readonly RenderAttributes m_DrawAttributes = new RenderAttributes();
	private CommandList m_CommandList;
	private Texture m_CachedFrameBufferCopy;
	private GpuBuffer<Vector4> m_WaterExclusionVolumeBuffer;
	private readonly Vector4[] m_WaterExclusionVolumeData = new Vector4[MAX_WATER_EXCLUSION_VOLUMES * WATER_EXCLUSION_VOLUME_ROWS];
	private GpuBuffer<Vector4> m_HullExclusionBuffer;
	private readonly Vector4[] m_HullExclusionData = new Vector4[HULL_EXCLUSION_META_SIZE + MAX_HULL_EXCLUSION_TRIS * 3];

	private HullCollider m_HullCollider;
	private int m_LastConfigHash;
	private float m_LastWidth;
	private float m_LastLength;
	private float m_LastDepth;
	private bool m_LastIsCircleShape;
	private int m_LastNumCircleSegments;
	private Vector3 m_LastHullCenter;
	private Vector3 m_LastHullBoxSize;
	private Material m_LastMaterial;

	private const float BASE_TILE_SIZE = 100.0f;

	private const int MAX_RINGS = 8;

	private const int MAX_WATER_EXCLUSION_VOLUMES = 512;
	private const int WATER_EXCLUSION_VOLUME_ROWS = 3;

	private const int MAX_HULL_EXCLUSION_VOLUMES = 8;
	private const int HULL_EXCLUSION_META_ROWS = 6;
	private const int HULL_EXCLUSION_META_SIZE = MAX_HULL_EXCLUSION_VOLUMES * HULL_EXCLUSION_META_ROWS;
	private const int MAX_HULL_EXCLUSION_TRIS = 16384;

	[Property, Group("General"), Order(0)] public WaterBodyType WaterType { get; set; } = WaterBodyType.Ocean;
	[Property, Group("General"), Order(0)] public Material Material { get; set; }
	[Property, Group("General"), Step(1), Order(0)] public float Width { get; set; } = 5000.0f;
	[Property, Group("General"), Step(1), Order(0)] public float Length { get; set; } = 5000.0f;
	[Property, Group("General"), Step(1), Order(0)] public float Depth { get; set; } = 300.0f;

	[Property, Group("Clipmap"), Order(2)] public float BaseCellSize { get; set { field = value.Clamp(8, 4096); } } = 32.0f;
	[Property, Group("Clipmap"), Order(2), Range(16, 512)] public int CellsPerRing { get; set { field = value.Clamp(16, 512); } } = 256;
	[Property(Title = "Use Camera For Clipmap"), Group("Clipmap"), Order(2)] public bool FollowCameraForClipmap { get; set; } = true;
	
	[Property, Group("Shape"), Order(3)] public bool CircleShape { get; set; } = false;
	[Property, Group("Shape"), Order(3), Range(5, 32), ShowIf(nameof(CircleShape), true)] public int CircleSegments { get; set { field = value.Clamp(5, 32); } } = 16;

	[Property, Group("Texture"), Order(4), Range(0.1f, 2.0f)] public float TextureTilingMultiplier { get; set; } = 1.0f;

	public HullCollider HullCollider => m_HullCollider;
	private int VerticesPerRing => (CellsPerRing + 1) * (CellsPerRing + 1);



	protected override void OnEnabled()
	{
		RefreshRenderBuffers();
		UpdateColliderState();

		m_LastWidth = Width;
		m_LastLength = Length;
		m_LastDepth = Depth;
		m_LastIsCircleShape = CircleShape;
		m_LastNumCircleSegments = CircleSegments;
		m_LastMaterial = Material;

		WaterManager.Current?.Register(this);
	}



	protected override void OnDisabled()
	{
		WaterManager.Current?.Unregister(this);

		m_HullCollider?.Destroy();

		m_VertexBuffer = default;
		m_IndexBuffer = default;
		m_WaterExclusionVolumeBuffer?.Dispose();
		m_WaterExclusionVolumeBuffer = null;
		m_HullExclusionBuffer?.Dispose();
		m_HullExclusionBuffer = null;
	}



	protected override void OnUpdate()
	{
		if (WaterManager.Current == null)
			return;

		// Material was just assigned after the component was already enabled, register now.
		if (m_LastMaterial == null && Material != null)
			WaterManager.Current.Register(this);

		m_LastMaterial = Material;

		if (Material == null)
			return;

		UpdateBuffers();

		if (Width != m_LastWidth || Length != m_LastLength || Depth != m_LastDepth || CircleShape != m_LastIsCircleShape || m_LastNumCircleSegments != CircleSegments)
		{
			UpdateColliderState();

			m_LastWidth = Width;
			m_LastLength = Length;
			m_LastDepth = Depth;
			m_LastIsCircleShape = CircleShape;
			m_LastNumCircleSegments = CircleSegments;
		}

		if (m_HullCollider.IsValid())
		{
			if (m_HullCollider.Center != m_LastHullCenter)
			{
				m_HullCollider.Center = m_LastHullCenter;
				
				Log.Warning("[WaterTool] Do not use S&box gizmos to control the size of the water quad, please use the intended: Width, Length & Depth property in the editor!");
			}

			if (m_HullCollider.BoxSize != m_LastHullBoxSize)
			{
				m_HullCollider.BoxSize = m_LastHullBoxSize;
				
				Log.Warning("[WaterTool] Do not use S&box gizmos to control the size of the water quad, please use the intended: Width, Length & Depth property in the editor!");
			}
		}

		UpdateShaderAttributes();
	}



	protected override void DrawGizmos()
	{
		if (!Gizmo.IsSelected)
			return;

		if (!m_HullCollider.IsValid())
			return;

		Gizmo.Draw.Color = Color.Cyan;

		if (CircleShape)
		{
			Vector3 pointA = m_HullCollider.Center;
			pointA.z -= m_HullCollider.Height / 2.0f;

			Vector3 pointB = m_HullCollider.Center;
			pointB.z += m_HullCollider.Height / 2.0f;

			Gizmo.Draw.LineCylinder(pointA, pointB, m_HullCollider.Radius, m_HullCollider.Radius2, CircleSegments);
		}
		else
		{
			Gizmo.Draw.LineBBox(m_HullCollider.LocalBounds);
		}
	}
	
	
	
	public void CacheCommandList(CommandList _CommandList)
	{
		m_CommandList ??= _CommandList;
	}



	private int ComputeConfigHash()
	{
		return HashCode.Combine(Width, Length, BaseCellSize, CellsPerRing, CircleShape, CircleSegments);
	}



	private int ComputeRingCount()
	{
		return ComputeRingCount(Width, Length);
	}



	private int ComputeRingCount(float _Width, float _Length)
	{
		float maxDim = MathF.Max(_Length, _Width);
		float innerExtent = CellsPerRing * BaseCellSize;

		float requiredExtent = maxDim * 2.0f;

		if (requiredExtent <= innerExtent)
			return 1;

		int rings = (int)MathF.Ceiling(MathF.Log2(requiredExtent / innerExtent)) + 1;

		return Math.Clamp(rings, 1, MAX_RINGS);
	}



	private float OuterExtent
	{
		get
		{
			if (CircleShape)
				return MathF.Min(Width, Length) / 2.0f;

			int ringCount = ComputeRingCount();

			return CellsPerRing * BaseCellSize * (1 << (ringCount - 1));
		}
	}



	private void UpdateBuffers()
	{
		int configHash = ComputeConfigHash();

		if (configHash != m_LastConfigHash)
		{
			CreateBuffers();

			m_LastConfigHash = configHash;
		}
	}



	private void CreateBuffers()
	{
		if (CircleShape)
		{
			BuildCircleBuffers();

			return;
		}

		int ringCount = ComputeRingCount();
		int n = CellsPerRing;
		int verticesPerRing = VerticesPerRing;

		int innerStart = n / 4 + 1;
		int innerEnd = n * 3 / 4 - 1;
		int innerBlockSize = innerEnd - innerStart;
		int filledCells = n * n;
		int hollowCells = filledCells - (innerBlockSize * innerBlockSize);

		int totalIndices = filledCells * 6;
		totalIndices += (ringCount - 1) * hollowCells * 6;

		m_VertexBuffer = new GpuBuffer<WaterVertex>(ringCount * verticesPerRing, GpuBuffer.UsageFlags.Vertex | GpuBuffer.UsageFlags.Structured);
		m_IndexBuffer = new GpuBuffer<uint>(totalIndices, GpuBuffer.UsageFlags.Index | GpuBuffer.UsageFlags.Structured);

		UploadIndexBuffer(ringCount);
	}



	private void RefreshRenderBuffers()
	{
		CreateBuffers();

		m_LastConfigHash = ComputeConfigHash();
	}



	private void BuildCircleBuffers()
	{
		int N = CircleSegments;

		// center + N edge points
		m_VertexBuffer = new GpuBuffer<WaterVertex>(N + 1, GpuBuffer.UsageFlags.Vertex | GpuBuffer.UsageFlags.Structured);

		// N pizza-slice triangles: [center, edge[i], edge[i+1]], wrapping last back to edge[1]
		var indices = new List<uint>(N * 3);

		for (int i = 0; i < N; i++)
		{
			indices.Add(0);
			indices.Add((uint)(i + 1));
			indices.Add((uint)((i + 1) % N + 1));
		}

		m_IndexBuffer = new GpuBuffer<uint>(indices.Count, GpuBuffer.UsageFlags.Index | GpuBuffer.UsageFlags.Structured);
		m_IndexBuffer.SetData(indices);
		m_TotalIndexCount = indices.Count;
	}



	private void UploadIndexBuffer(int _RingCount)
	{
		int n = CellsPerRing;
		int verticesPerRing = VerticesPerRing;

		int innerStart = n / 4 + 1;
		int innerEnd = n * 3 / 4 - 1;

		var indices = new List<uint>();

		for (int ring = 0; ring < _RingCount; ring++)
		{
			uint baseVertex = (uint)(ring * verticesPerRing);

			for (int y = 0; y < n; y++)
			{
				for (int x = 0; x < n; x++)
				{
					if (ring > 0 && x >= innerStart && x < innerEnd && y >= innerStart && y < innerEnd)
						continue;

					uint i0 = baseVertex + (uint)(y * (n + 1) + x);
					uint i1 = i0 + 1;
					uint i2 = i0 + (uint)(n + 1);
					uint i3 = i2 + 1;

					indices.Add(i0);
					indices.Add(i1);
					indices.Add(i2);
					indices.Add(i1);
					indices.Add(i3);
					indices.Add(i2);
				}
			}
		}

		m_IndexBuffer.SetData(indices);

		m_TotalIndexCount = indices.Count;
	}



	internal bool HasValidBuffers => m_VertexBuffer.IsValid() && m_IndexBuffer.IsValid();

	internal bool ParticipatesInRendering => Material.IsValid();
	
	
	
	internal BBox GetWorldBounds2D()
	{
		Vector3 right = WorldRotation.Right * (Length / 2.0f);
		Vector3 forward = WorldRotation.Forward * (Width / 2.0f);

		Vector3 c0 = WorldPosition + right + forward;
		Vector3 c1 = WorldPosition - right + forward;
		Vector3 c2 = WorldPosition + right - forward;
		Vector3 c3 = WorldPosition - right - forward;

		float minX = MathF.Min(MathF.Min(c0.x, c1.x), MathF.Min(c2.x, c3.x));
		float maxX = MathF.Max(MathF.Max(c0.x, c1.x), MathF.Max(c2.x, c3.x));
		float minY = MathF.Min(MathF.Min(c0.y, c1.y), MathF.Min(c2.y, c3.y));
		float maxY = MathF.Max(MathF.Max(c0.y, c1.y), MathF.Max(c2.y, c3.y));

		return new BBox(new Vector3(minX, minY, WorldPosition.z - Depth), new Vector3(maxX, maxY, WorldPosition.z));
	}



	internal void DispatchCompute(ComputeShader _Shader, Vector3 _CameraPosition)
	{
		if (!ParticipatesInRendering || !HasValidBuffers)
			return;

		float outerExtent = OuterExtent;

		if (CircleShape)
		{
			float radius = MathF.Min(Width, Length) / 2.0f;

			_Shader.Attributes.Set("DiscMode", true);

			_Shader.Attributes.Set("VertexBuffer", m_VertexBuffer);
			_Shader.Attributes.Set("VertexOffset", 0);

			_Shader.Attributes.Set("WaterZ", WorldPosition.z);

			_Shader.Attributes.Set("TilingScale", 1.0f / outerExtent);
			_Shader.Attributes.Set("ClampToBounds", false);

			_Shader.Attributes.Set("CircleSegments", CircleSegments);
			_Shader.Attributes.Set("CircleCenter", (Vector2)WorldPosition);
			_Shader.Attributes.Set("CircleRadius", radius);

			_Shader.Dispatch(CircleSegments + 1, 1, 1);

			return;
		}

		int ringCount = ComputeRingCount();
		int verticesPerRing = VerticesPerRing;

		var localBounds = GetWorldBounds2D();
		float boundsMinX = localBounds.Mins.x;
		float boundsMaxX = localBounds.Maxs.x;
		float boundsMinY = localBounds.Mins.y;
		float boundsMaxY = localBounds.Maxs.y;

		for (int ring = 0; ring < ringCount; ring++)
		{
			float cellSize = BaseCellSize * (1 << ring);

			Vector3 clipmapAnchor = FollowCameraForClipmap ? _CameraPosition : WorldPosition;

			float snapX = MathF.Floor(clipmapAnchor.x / cellSize) * cellSize;
			float snapY = MathF.Floor(clipmapAnchor.y / cellSize) * cellSize;

			_Shader.Attributes.Set("DiscMode", false);

			_Shader.Attributes.Set("VertexBuffer", m_VertexBuffer);
			_Shader.Attributes.Set("VertexOffset", ring * verticesPerRing);

			_Shader.Attributes.Set("GridWidth", CellsPerRing);
			_Shader.Attributes.Set("CellSize", cellSize);

			_Shader.Attributes.Set("SnapPosition", new Vector2(snapX, snapY));
			_Shader.Attributes.Set("WaterZ", WorldPosition.z);

			_Shader.Attributes.Set("TilingScale", 1.0f / outerExtent);
			_Shader.Attributes.Set("ClampToBounds", true);

			_Shader.Attributes.Set("BoundsMin", new Vector2(boundsMinX, boundsMinY));
			_Shader.Attributes.Set("BoundsMax", new Vector2(boundsMaxX, boundsMaxY));

			_Shader.Dispatch(verticesPerRing, 1, 1);
		}
	}



	internal void BarrierTransition()
	{
		if (m_VertexBuffer.IsValid())
			m_CommandList.ResourceBarrierTransition(m_VertexBuffer, ResourceState.UnorderedAccess, ResourceState.VertexOrIndexBuffer);
	}



	internal void Draw(Texture _FrameBufferCopy)
	{
		if (!ParticipatesInRendering || !HasValidBuffers)
			return;

		m_CachedFrameBufferCopy = _FrameBufferCopy;

		m_CommandList.DrawIndexed(m_VertexBuffer, m_IndexBuffer, Material, 0, m_TotalIndexCount, m_DrawAttributes);
	}



	private void UpdateColliderState()
	{
		m_HullCollider = GetOrAddComponent<HullCollider>();
		m_HullCollider.Flags |= ComponentFlags.Hidden;
		m_HullCollider.Static = true;

		m_HullCollider.Type = CircleShape ? HullCollider.PrimitiveType.Cylinder : HullCollider.PrimitiveType.Box;

		m_HullCollider.Center = new Vector3(0, 0, -Depth / 2.0f);

		if (CircleShape)
		{
			m_HullCollider.Radius = MathF.Min(Width, Length) / 2.0f;
			m_HullCollider.Radius2 = MathF.Min(Width, Length) / 2.0f;
			m_HullCollider.Height = Depth;
			m_HullCollider.Slices = CircleSegments;
		}
		else
		{
			m_HullCollider.BoxSize = new Vector3(Width, Length, Depth);
		}
		
		m_LastHullCenter = m_HullCollider.Center;
		m_LastHullBoxSize = m_HullCollider.BoxSize;

		m_HullCollider.IsTrigger = true;

		Tags.Add("water");
	}



	internal (Vector3 Center, Vector3 Forward, Vector3 Up, Vector3 HalfExtents) GetWorldOBB()
	{
		return (
			WorldPosition + (WorldTransform.Up * (-Depth * 0.5f)),
			WorldRotation.Forward,
			WorldTransform.Up,
			new Vector3(Width * 0.5f, Length * 0.5f, Depth * 0.5f)
		);
	}



	private void UpdateShaderAttributes()
	{
		if (m_CachedFrameBufferCopy.IsValid())
			m_DrawAttributes.Set("FrameBufferCopyTexture", m_CachedFrameBufferCopy);

		m_DrawAttributes.Set("RequireWaterInclusionVolumes", false);

		WaterDefinition profile = WaterManager.GetWaveProfile(WaterType);

		if (profile.IsValid())
			profile.ApplyTo(m_DrawAttributes);

		m_DrawAttributes.Set("WaterTime", Time.Now);
		m_DrawAttributes.Set("DepthMax", Depth);

		float outerExtent = OuterExtent;

		Vector2 tiling = new Vector2((outerExtent / BASE_TILE_SIZE) * TextureTilingMultiplier, (outerExtent / BASE_TILE_SIZE) * TextureTilingMultiplier);

		m_DrawAttributes.Set("NormalTiling", tiling);

		SetWaterExclusionVolumes(Scene.Camera.WorldPosition);
		SetHullExclusionVolumes();
	}



	private void SetWaterExclusionVolumes(Vector3 _ReferencePosition)
	{
		if (WaterManager.Current == null)
			return;

		EnsureWaterExclusionVolumeBuffer();

		var volumes = WaterManager.Current.ExclusionVolumes
			.Where(v => v.IsValid() && v.Active)
			.OrderBy(v => v.WorldPosition.DistanceSquared(_ReferencePosition))
			.Take(MAX_WATER_EXCLUSION_VOLUMES)
			.ToList();

		for (int i = 0; i < volumes.Count; i++)
		{
			var (center, forward, up, half) = volumes[i].GetWorldOBB();

			int rowOffset = i * WATER_EXCLUSION_VOLUME_ROWS;

			m_WaterExclusionVolumeData[rowOffset + 0] = new Vector4(forward.x, forward.y, forward.z, half.x);
			m_WaterExclusionVolumeData[rowOffset + 1] = new Vector4(up.x, up.y, up.z, half.y);
			m_WaterExclusionVolumeData[rowOffset + 2] = new Vector4(center.x, center.y, center.z, half.z);
		}

		m_WaterExclusionVolumeBuffer.SetData(m_WaterExclusionVolumeData.AsSpan(0, volumes.Count * WATER_EXCLUSION_VOLUME_ROWS));

		m_DrawAttributes.Set("WaterExclusionVolumeCount", volumes.Count);
		m_DrawAttributes.Set("WaterExclusionVolumeRows", m_WaterExclusionVolumeBuffer);
	}



	private void EnsureWaterExclusionVolumeBuffer()
	{
		if (m_WaterExclusionVolumeBuffer.IsValid())
			return;

		m_WaterExclusionVolumeBuffer = new GpuBuffer<Vector4>(MAX_WATER_EXCLUSION_VOLUMES * WATER_EXCLUSION_VOLUME_ROWS);
	}



	private void SetHullExclusionVolumes()
	{
		if (WaterManager.Current == null)
			return;

		var hulls = WaterManager.Current.HullExclusionVolumes
			.Where(h => h.IsValid() && h.Active && h.LocalTriangles.Length > 0)
			.Take(MAX_HULL_EXCLUSION_VOLUMES)
			.ToList();

		if (hulls.Count == 0)
		{
			m_DrawAttributes.Set("WaterHullExclusionCount", 0);
			return;
		}

		EnsureHullExclusionBuffers();

		// Triangles are written after the fixed-size metadata section
		int triWriteCursor = HULL_EXCLUSION_META_SIZE;

		for (int h = 0; h < hulls.Count; h++)
		{
			var hull = hulls[h];
			var tris = hull.LocalTriangles;
			int triCount = tris.Length / 3;

			if (triWriteCursor + tris.Length > m_HullExclusionData.Length)
				break;

			hull.GetWorldToLocalRows(out var r0, out var r1, out var r2, out var r3);

			int meta = h * HULL_EXCLUSION_META_ROWS;
			m_HullExclusionData[meta + 0] = r0;
			m_HullExclusionData[meta + 1] = r1;
			m_HullExclusionData[meta + 2] = r2;
			m_HullExclusionData[meta + 3] = r3;

			var aabb = hull.LocalAABB;
			// vertStart is an absolute index into the combined buffer
			m_HullExclusionData[meta + 4] = new Vector4(triWriteCursor, triCount, aabb.Mins.x, aabb.Mins.y);
			m_HullExclusionData[meta + 5] = new Vector4(aabb.Mins.z, aabb.Maxs.x, aabb.Maxs.y, aabb.Maxs.z);

			for (int i = 0; i < tris.Length; i++)
				m_HullExclusionData[triWriteCursor + i] = new Vector4(tris[i].x, tris[i].y, tris[i].z, 0f);

			triWriteCursor += tris.Length;
		}

		m_HullExclusionBuffer.SetData(m_HullExclusionData.AsSpan(0, triWriteCursor));

		m_DrawAttributes.Set("WaterHullExclusionCount", hulls.Count);
		m_DrawAttributes.Set("WaterHullExclusionData", m_HullExclusionBuffer);
	}



	private void EnsureHullExclusionBuffers()
	{
		if (!m_HullExclusionBuffer.IsValid())
			m_HullExclusionBuffer = new GpuBuffer<Vector4>(HULL_EXCLUSION_META_SIZE + MAX_HULL_EXCLUSION_TRIS * 3, GpuBuffer.UsageFlags.Structured);
	}



	public Vector3 GetWaveDisplacementAt(Vector3 _WorldPosition)
	{
		WaterDefinition profile = WaterManager.GetWaveProfile(WaterType);

		return WaterWaveUtility.ComputeDisplacementAt(_WorldPosition, profile);
	}



	public Vector3 GetWaveVelocityAt(Vector3 _WorldPosition)
	{
		WaterDefinition profile = WaterManager.GetWaveProfile(WaterType);

		return WaterWaveUtility.ComputeVelocityAt(_WorldPosition, profile);
	}



	public float GetWaveHeightAt(Vector3 _WorldPosition)
	{
		return WorldPosition.z + GetWaveDisplacementAt(_WorldPosition).z;
	}
}
