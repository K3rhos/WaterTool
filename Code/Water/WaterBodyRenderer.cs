using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;
using Sandbox.Rendering;

namespace RedSnail.WaterTool;

[Icon("water"), Group("Environment"), Title("Water Body Renderer")]
public sealed class WaterBodyRenderer : Component, Component.ExecuteInEditor, Component.DontExecuteOnServer
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

	private const float BASE_TILE_SIZE = 100.0f;

	private const int MAX_RINGS = 8;

	private const int MAX_WATER_INCLUSION_VOLUMES = 1024;
	private const int WATER_INCLUSION_VOLUME_ROWS = 3;

	private const int MAX_WATER_EXCLUSION_VOLUMES = 512;
	private const int WATER_EXCLUSION_VOLUME_ROWS = 3;

	private const int MAX_HULL_EXCLUSION_VOLUMES = 8;
	private const int HULL_EXCLUSION_META_ROWS = 6;
	private const int HULL_EXCLUSION_META_SIZE = MAX_HULL_EXCLUSION_VOLUMES * HULL_EXCLUSION_META_ROWS;
	private const int MAX_HULL_EXCLUSION_TRIS = 16384;

	private GpuBuffer<WaterVertex> m_VertexBuffer;
	private GpuBuffer<uint> m_IndexBuffer;
	private GpuBuffer<Vector4> m_WaterInclusionVolumeBuffer;
	private GpuBuffer<Vector4> m_WaterExclusionVolumeBuffer;
	private int m_TotalIndexCount;
	private readonly RenderAttributes m_DrawAttributes = new();
	private CommandList m_CommandList;
	private Texture m_CachedFrameBufferCopy;
	private int m_LastConfigHash;
	private readonly Vector4[] m_WaterInclusionVolumeData = new Vector4[MAX_WATER_INCLUSION_VOLUMES * WATER_INCLUSION_VOLUME_ROWS];
	private readonly Vector4[] m_WaterExclusionVolumeData = new Vector4[MAX_WATER_EXCLUSION_VOLUMES * WATER_EXCLUSION_VOLUME_ROWS];
	private GpuBuffer<Vector4> m_HullExclusionBuffer;
	private readonly Vector4[] m_HullExclusionData = new Vector4[HULL_EXCLUSION_META_SIZE + MAX_HULL_EXCLUSION_TRIS * 3];

	[Property, Group("General"), Order(0)] public WaterBodyType WaterType { get; set; } = WaterBodyType.Ocean;
	[Property, Group("General"), Order(0)] public Material Material { get; set; }
	[Property, Group("General"), Order(0)] public float Width { get; set; } = 10000.0f;
	[Property, Group("General"), Order(0)] public float Length { get; set; } = 10000.0f;
	[Property, Group("General"), Order(0)] public float Depth { get; set; } = 300.0f;
	[Property(Title = "Infinite Rendering"), Group("General"), Order(0)] public bool UseHybridInclusionBounds { get; set; } = true;
	[Property, Group("Clipmap"), Order(1)] public float BaseCellSize { get; set; } = 8.0f;
	[Property, Group("Clipmap"), Order(1), Range(16, 512)] public int CellsPerRing { get; set; } = 64;
	[Property(Title = "Use Camera For Clipmap"), Group("Clipmap"), Order(1)] public bool FollowCameraForClipmap { get; set; } = true;
	[Property, Group("Texture"), Order(2), Range(0.1f, 2.0f)] public float TextureTilingMultiplier { get; set; } = 1.0f;

	private int VerticesPerRing => (CellsPerRing + 1) * (CellsPerRing + 1);
	private float OuterExtent => CellsPerRing * BaseCellSize * (1 << (ComputeRingCount() - 1));

	internal bool ParticipatesInRendering => Active && Material.IsValid();
	internal bool HasValidBuffers => m_VertexBuffer.IsValid() && m_IndexBuffer.IsValid();

	protected override void OnEnabled()
	{
		if (!ParticipatesInRendering)
			return;

		CreateBuffers();

		m_LastConfigHash = ComputeConfigHash();

		WaterManager.Current?.Register(this);
	}

	protected override void OnDisabled()
	{
		WaterManager.Current?.Unregister(this);

		m_VertexBuffer = default;
		m_IndexBuffer = default;
		m_CommandList = null;
		m_WaterInclusionVolumeBuffer?.Dispose();
		m_WaterInclusionVolumeBuffer = null;
		m_WaterExclusionVolumeBuffer?.Dispose();
		m_WaterExclusionVolumeBuffer = null;
		m_HullExclusionBuffer?.Dispose();
		m_HullExclusionBuffer = null;
	}

	protected override void OnUpdate()
	{
		if (!ParticipatesInRendering)
			return;

		int configHash = ComputeConfigHash();
		if (!HasValidBuffers || configHash != m_LastConfigHash)
		{
			CreateBuffers();
			m_LastConfigHash = configHash;
		}

		UpdateShaderAttributes();
	}

	public void CacheCommandList(CommandList commandList)
	{
		m_CommandList ??= commandList;
	}

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

	internal void DispatchCompute(ComputeShader shader, Vector3 cameraPosition)
	{
		if (!ParticipatesInRendering || !HasValidBuffers)
			return;

		int ringCount = ComputeRingCount();
		int verticesPerRing = VerticesPerRing;

		var localBounds = GetWorldBounds2D();

		for (int ring = 0; ring < ringCount; ring++)
		{
			float cellSize = BaseCellSize * (1 << ring);
			Vector3 clipmapAnchor = FollowCameraForClipmap ? cameraPosition : WorldPosition;
			float snapX = MathF.Floor(clipmapAnchor.x / cellSize) * cellSize;
			float snapY = MathF.Floor(clipmapAnchor.y / cellSize) * cellSize;

			shader.Attributes.Set("DiscMode", false);
			shader.Attributes.Set("VertexBuffer", m_VertexBuffer);
			shader.Attributes.Set("VertexOffset", ring * verticesPerRing);
			shader.Attributes.Set("GridWidth", CellsPerRing);
			shader.Attributes.Set("CellSize", cellSize);
			shader.Attributes.Set("SnapPosition", new Vector2(snapX, snapY));
			shader.Attributes.Set("WaterZ", WorldPosition.z);
			shader.Attributes.Set("TilingScale", 1.0f / OuterExtent);
			shader.Attributes.Set("ClampToBounds", false);
			shader.Attributes.Set("BoundsMin", new Vector2(localBounds.Mins.x, localBounds.Mins.y));
			shader.Attributes.Set("BoundsMax", new Vector2(localBounds.Maxs.x, localBounds.Maxs.y));
			shader.Dispatch(verticesPerRing, 1, 1);
		}
	}

	internal void BarrierTransition()
	{
		if (m_VertexBuffer.IsValid())
			m_CommandList.ResourceBarrierTransition(m_VertexBuffer, ResourceState.UnorderedAccess, ResourceState.VertexOrIndexBuffer);
	}

	internal void Draw(Texture frameBufferCopy)
	{
		if (!ParticipatesInRendering || !HasValidBuffers)
			return;

		m_CachedFrameBufferCopy = frameBufferCopy;
		m_CommandList.DrawIndexed(m_VertexBuffer, m_IndexBuffer, Material, 0, m_TotalIndexCount, m_DrawAttributes);
	}

	private void UpdateShaderAttributes()
	{
		if (m_CachedFrameBufferCopy.IsValid())
			m_DrawAttributes.Set("FrameBufferCopyTexture", m_CachedFrameBufferCopy);

		BBox localBounds = GetWorldBounds2D();

		m_DrawAttributes.Set("RequireWaterInclusionVolumes", UseHybridInclusionBounds);
		m_DrawAttributes.Set("UseHybridInclusionBounds", UseHybridInclusionBounds);
		m_DrawAttributes.Set("HybridInclusionBoundsMin", new Vector2(localBounds.Mins.x, localBounds.Mins.y));
		m_DrawAttributes.Set("HybridInclusionBoundsMax", new Vector2(localBounds.Maxs.x, localBounds.Maxs.y));

		WaterDefinition profile = WaterManager.GetWaveProfile(WaterType);

		if (profile.IsValid())
			profile.ApplyTo(m_DrawAttributes);

		m_DrawAttributes.Set("WaterTime", Time.Now);
		m_DrawAttributes.Set("DepthMax", Depth);

		float tilingScalar = (OuterExtent / BASE_TILE_SIZE) * TextureTilingMultiplier;
		m_DrawAttributes.Set("NormalTiling", new Vector2(tilingScalar, tilingScalar));

		SetWaterInclusionVolumes(Scene.Camera.WorldPosition);
		SetWaterExclusionVolumes(Scene.Camera.WorldPosition);
		SetHullExclusionVolumes();
	}

	private void SetWaterInclusionVolumes(Vector3 referencePosition)
	{
		EnsureWaterInclusionVolumeBuffer();

		var volumes = WaterManager.Current.Bodies
			.Where(v => v.IsValid() && v.Active && v.WaterType == WaterType)
			.OrderBy(v => v.WorldPosition.DistanceSquared(referencePosition))
			.Take(MAX_WATER_INCLUSION_VOLUMES)
			.ToList();

		for (int i = 0; i < volumes.Count; i++)
		{
			var (center, forward, up, half) = volumes[i].GetWorldOBB();

			int rowOffset = i * WATER_INCLUSION_VOLUME_ROWS;

			m_WaterInclusionVolumeData[rowOffset + 0] = new Vector4(forward.x, forward.y, forward.z, half.x);
			m_WaterInclusionVolumeData[rowOffset + 1] = new Vector4(up.x, up.y, up.z, half.y);
			m_WaterInclusionVolumeData[rowOffset + 2] = new Vector4(center.x, center.y, center.z, half.z);
		}

		m_WaterInclusionVolumeBuffer.SetData(m_WaterInclusionVolumeData.AsSpan(0, volumes.Count * WATER_INCLUSION_VOLUME_ROWS));

		m_DrawAttributes.Set("WaterInclusionVolumeCount", volumes.Count);
		m_DrawAttributes.Set("WaterInclusionVolumeRows", m_WaterInclusionVolumeBuffer);
	}

	private void SetWaterExclusionVolumes(Vector3 referencePosition)
	{
		EnsureWaterExclusionVolumeBuffer();

		var volumes = WaterManager.Current.ExclusionVolumes
			.Where(v => v.IsValid() && v.Enabled && v.Active)
			.OrderBy(v => v.WorldPosition.DistanceSquared(referencePosition))
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

		m_WaterExclusionVolumeBuffer = new GpuBuffer<Vector4>(MAX_WATER_EXCLUSION_VOLUMES * WATER_EXCLUSION_VOLUME_ROWS, GpuBuffer.UsageFlags.Structured);
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



	private void EnsureWaterInclusionVolumeBuffer()
	{
		if (m_WaterInclusionVolumeBuffer.IsValid())
			return;

		m_WaterInclusionVolumeBuffer = new GpuBuffer<Vector4>(MAX_WATER_INCLUSION_VOLUMES * WATER_INCLUSION_VOLUME_ROWS, GpuBuffer.UsageFlags.Structured);
	}

	private int ComputeConfigHash()
	{
		return HashCode.Combine(Width, Length, BaseCellSize, CellsPerRing);
	}

	private int ComputeRingCount()
	{
		return ComputeRingCount(Width, Length);
	}

	private int ComputeRingCount(float width, float length)
	{
		float maxDim = MathF.Max(length, width);
		float innerExtent = CellsPerRing * BaseCellSize;
		float requiredExtent = maxDim * 2.0f;

		if (requiredExtent <= innerExtent)
			return 1;

		int rings = (int)MathF.Ceiling(MathF.Log2(requiredExtent / innerExtent)) + 1;
		return Math.Clamp(rings, 1, MAX_RINGS);
	}

	private void CreateBuffers()
	{
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

	private void UploadIndexBuffer(int ringCount)
	{
		int n = CellsPerRing;
		int verticesPerRing = VerticesPerRing;
		int innerStart = n / 4 + 1;
		int innerEnd = n * 3 / 4 - 1;

		var indices = new List<uint>();

		for (int ring = 0; ring < ringCount; ring++)
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
}
