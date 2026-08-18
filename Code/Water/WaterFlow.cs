using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;
using Sandbox.Rendering;

namespace RedSnail.WaterTool;

/// <summary>
/// Spline-driven flowing water surface (rivers, streams, canals).
///
/// A ribbon mesh is generated along the spline; flow speed is automatically
/// regulated by the spline's slope using a simple energy model: descending
/// sections accelerate the water (gravity), flat sections gradually calm it
/// down (friction). <see cref="InitialVelocity"/> seeds the flow at the source
/// (the first spline point).
///
/// Flow direction and speed are baked per-vertex (in the vertex color) and the
/// water shader makes waves travel downstream and scrolls the surface normals
/// along the flow.
/// </summary>
[Icon("water"), Group("Water"), Title("Water Flow")]
public sealed class WaterFlow : Component, Component.ExecuteInEditor, Component.DontExecuteOnServer
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

	/// <summary>
	/// A point along the river centerline with its resolved flow state.
	/// </summary>
	public struct FlowSample
	{
		public Vector3 Position;    // world-space centerline position
		public Vector2 Direction;   // world-space flow direction (XY, normalized)
		public float Speed;         // flow speed in units/s
		public float Distance;      // distance along the spline
		// Travel-time advected along-coordinate: FlowSpeedReference * seconds the water
		// takes to reach this row from the source. Waves/textures are parameterized on
		// THIS instead of raw distance, so a uniform scroll moves the surface pattern
		// at the correct local speed everywhere — scaling time by a per-vertex speed
		// would shear the pattern unboundedly over time (compressed bands, patches
		// appearing to flow backwards).
		public float Advected;
	}

	private const float BASE_TILE_SIZE = 100.0f;

	private const int MAX_ROWS = 4096;
	private const int MAX_COLUMNS = 128;

	private const int MAX_WATER_EXCLUSION_VOLUMES = 512;
	private const int WATER_EXCLUSION_VOLUME_ROWS = 3;

	private const int MAX_HULL_EXCLUSION_VOLUMES = 8;
	private const int HULL_EXCLUSION_META_ROWS = 6;
	private const int HULL_EXCLUSION_META_SIZE = MAX_HULL_EXCLUSION_VOLUMES * HULL_EXCLUSION_META_ROWS;
	private const int MAX_HULL_EXCLUSION_TRIS = 16384;

	// The spline is authored source -> mouth, in the GameObject's local space.
	// Hidden from the inspector — edited through the Water Flow editor tool.
	[Property, Hide] public Spline Spline { get; set; } = new();

	[Property, Group("General"), Order(0)] public WaterBodyType WaterType { get; set; } = WaterBodyType.River;
	[Property, Group("General"), Order(0)] public Material Material { get; set; }
	[Property, Group("General"), Step(1), Order(0)] public float Width { get; set { field = value.Clamp(16.0f, 8192.0f); } } = 256.0f;
	[Property, Group("General"), Step(1), Order(0)] public float Depth { get; set { field = value.Clamp(1.0f, 4096.0f); } } = 100.0f;
	[Property, Group("General"), Order(0)] public float BaseCellSize { get; set { field = value.Clamp(8.0f, 512.0f); } } = 32.0f;

	// Flow speed at the source (first spline point), in units/s.
	[Property, Group("Flow"), Order(1)] public float InitialVelocity { get; set { field = value.Clamp(0.0f, 2000.0f); } } = 100.0f;
	// Acceleration applied while the spline descends — steeper drop = faster water.
	[Property, Group("Flow"), Order(1)] public float FlowGravity { get; set { field = value.Clamp(0.0f, 2000.0f); } } = 400.0f;
	// Energy lost per unit travelled — calms the water down on flat stretches.
	[Property, Group("Flow"), Order(1)] public float FlowFriction { get; set { field = value.Clamp(0.0f, 0.1f); } } = 0.002f;
	[Property, Group("Flow"), Order(1)] public float MinFlowSpeed { get; set { field = value.Clamp(0.0f, 500.0f); } } = 20.0f;
	[Property, Group("Flow"), Order(1)] public float MaxFlowSpeed { get; set { field = value.Clamp(10.0f, 4000.0f); } } = 800.0f;
	// Flow speed (units/s) that reads as "normal agitation" in the shader — sections
	// flowing faster than this look rougher, slower sections look calmer.
	[Property, Group("Flow"), Order(1)] public float FlowSpeedReference { get; set { field = value.Clamp(1.0f, 2000.0f); } } = 100.0f;

	[Property, Group("Texture"), Order(2), Range(0.1f, 2.0f)] public float TextureTilingMultiplier { get; set; } = 1.0f;

	private GpuBuffer<WaterVertex> m_VertexBuffer;
	private GpuBuffer<uint> m_IndexBuffer;
	private int m_TotalIndexCount;
	private readonly RenderAttributes m_DrawAttributes = new RenderAttributes();
	private GpuBuffer<Vector4> m_WaterExclusionVolumeBuffer;
	private readonly Vector4[] m_WaterExclusionVolumeData = new Vector4[MAX_WATER_EXCLUSION_VOLUMES * WATER_EXCLUSION_VOLUME_ROWS];
	private GpuBuffer<Vector4> m_HullExclusionBuffer;
	private readonly Vector4[] m_HullExclusionData = new Vector4[HULL_EXCLUSION_META_SIZE + MAX_HULL_EXCLUSION_TRIS * 3];

	private readonly List<FlowSample> m_Samples = [];
	private int m_LastBuildHash;
	private bool m_SplineDirty;

	/// <summary>Resolved centerline samples (world space) — one per mesh row.</summary>
	public IReadOnlyList<FlowSample> Samples => m_Samples;

	internal bool ParticipatesInRendering => Material.IsValid() && m_TotalIndexCount > 0;
	internal bool HasValidBuffers => m_VertexBuffer.IsValid() && m_IndexBuffer.IsValid();



	protected override void OnEnabled()
	{
		EnsureDefaultSpline();

		Spline.SplineChanged += OnSplineChanged;

		RebuildMesh();

		WaterManager.Current?.RefreshWaterFlowsList();
	}



	protected override void OnDisabled()
	{
		Spline.SplineChanged -= OnSplineChanged;

		WaterManager.Current?.RefreshWaterFlowsList();

		m_VertexBuffer = default;
		m_IndexBuffer = default;
		m_TotalIndexCount = 0;
		m_Samples.Clear();
		m_WaterExclusionVolumeBuffer?.Dispose();
		m_WaterExclusionVolumeBuffer = null;
		m_HullExclusionBuffer?.Dispose();
		m_HullExclusionBuffer = null;
	}



	protected override void OnUpdate()
	{
		// The source point is pinned to the GameObject origin (see EnforceSourcePoint)
		EnforceSourcePoint();

		// Keep the mesh and flow samples in sync with the spline even before a
		// material is assigned — the editor tool and CPU queries rely on them.
		int buildHash = ComputeBuildHash();

		if (m_SplineDirty || buildHash != m_LastBuildHash)
			RebuildMesh();

		if (Material == null)
			return;

		UpdateShaderAttributes();
	}



	private void OnSplineChanged()
	{
		m_SplineDirty = true;
	}



	// The first spline point is the river's source: its world position IS the
	// GameObject's transform, so it's pinned to local (0,0,0). This keeps the source
	// from drifting away from the object that owns it — move the GameObject to move
	// the source. Enforced here so it holds no matter how the point was edited
	// (gizmo, inspector, undo, script). Only the point's tangent/mode stay editable.
	private void EnforceSourcePoint()
	{
		if (Spline.PointCount == 0)
			return;

		var source = Spline.GetPoint(0);

		if (source.Position.LengthSquared > 1e-6f)
		{
			source.Position = Vector3.Zero;
			Spline.UpdatePoint(0, source);
		}
	}



	private void EnsureDefaultSpline()
	{
		if (Spline.PointCount >= 2)
			return;

		Spline.Clear();
		Spline.AddPoint(new Spline.Point { Position = Vector3.Zero });
		Spline.AddPoint(new Spline.Point { Position = new Vector3(512.0f, 0.0f, -16.0f) });
	}



	[Button("Add Point", "add")]
	public void AddPointAtEnd()
	{
		Vector3 newPosition;

		if (Spline.PointCount >= 2)
		{
			Vector3 last = Spline.GetPoint(Spline.PointCount - 1).Position;
			Vector3 previous = Spline.GetPoint(Spline.PointCount - 2).Position;
			Vector3 direction = (last - previous).Normal;

			newPosition = last + direction * 256.0f;
		}
		else
		{
			newPosition = Spline.PointCount > 0
				? Spline.GetPoint(Spline.PointCount - 1).Position + new Vector3(256, 0, 0)
				: Vector3.Zero;
		}

		Spline.AddPoint(new Spline.Point { Position = newPosition });
	}

	[Button("Remove Last Point", "remove")]
	public void RemoveLastPoint()
	{
		if (Spline.PointCount <= 2)
			return;

		Spline.RemovePoint(Spline.PointCount - 1);
	}



	private int ComputeBuildHash()
	{
		var hash = new HashCode();

		hash.Add(Width);
		hash.Add(BaseCellSize);
		hash.Add(InitialVelocity);
		hash.Add(FlowGravity);
		hash.Add(FlowFriction);
		hash.Add(MinFlowSpeed);
		hash.Add(MaxFlowSpeed);
		hash.Add(FlowSpeedReference);
		hash.Add(WorldPosition);
		hash.Add(WorldRotation);

		for (int i = 0; i < Spline.PointCount; i++)
		{
			var p = Spline.GetPoint(i);
			hash.Add(p.Position);
			hash.Add(p.In);
			hash.Add(p.Out);
		}

		return hash.ToHashCode();
	}



	// ─────────────────────────────────────────────────────────────────────────
	// Mesh generation
	// ─────────────────────────────────────────────────────────────────────────

	private void RebuildMesh()
	{
		m_SplineDirty = false;
		m_LastBuildHash = ComputeBuildHash();

		m_Samples.Clear();
		m_TotalIndexCount = 0;

		if (Spline.PointCount < 2)
			return;

		float length = Spline.Length;

		if (length < 1.0f)
			return;

		int rows = Math.Clamp((int)MathF.Ceiling(length / BaseCellSize) + 1, 2, MAX_ROWS);
		int columns = Math.Clamp((int)MathF.Ceiling(Width / BaseCellSize) + 1, 2, MAX_COLUMNS);

		BuildFlowSamples(length, rows);
		BuildBuffers(rows, columns);
	}



	// Walks the spline from source to mouth resolving the flow speed at each row
	// with a simple energy model:
	//   - descending adds kinetic energy (v² += 2 g Δh)
	//   - friction bleeds energy per unit travelled (v² *= e^(-friction·ds))
	// so steep sections rush and long flat stretches settle back down.
	private void BuildFlowSamples(float _Length, int _Rows)
	{
		float step = _Length / (_Rows - 1);

		float v2 = InitialVelocity * InitialVelocity;
		float previousZ = 0.0f;
		Vector2 previousDir = Vector2.Zero;
		float flowTime = 0.0f;

		for (int r = 0; r < _Rows; r++)
		{
			float distance = r * step;

			Spline.Sample sample = Spline.SampleAtDistance(distance);

			Vector3 worldPos = WorldTransform.PointToWorld(sample.Position);
			Vector3 worldTangent = WorldRotation * sample.Tangent;

			// Flow direction is the horizontal part of the spline tangent
			Vector2 dir = new(worldTangent.x, worldTangent.y);
			dir = dir.LengthSquared > 1e-6f ? dir.Normal : (previousDir.LengthSquared > 0.0f ? previousDir : new Vector2(1, 0));

			if (r > 0)
			{
				float drop = previousZ - worldPos.z;   // positive while descending

				v2 += 2.0f * FlowGravity * drop;
				v2 *= MathF.Exp(-FlowFriction * step);
			}

			v2 = Math.Clamp(v2, MinFlowSpeed * MinFlowSpeed, MaxFlowSpeed * MaxFlowSpeed);

			float speed = MathF.Sqrt(v2);

			// Accumulate the water's travel time from the source (see FlowSample.Advected)
			if (r > 0)
				flowTime += step / MathF.Max(speed, 1.0f);

			m_Samples.Add(new FlowSample
			{
				Position = worldPos,
				Direction = dir,
				Speed = speed,
				Distance = distance,
				Advected = flowTime * FlowSpeedReference
			});

			previousZ = worldPos.z;
			previousDir = dir;
		}
	}



	private void BuildBuffers(int _Rows, int _Columns)
	{
		int vertexCount = _Rows * _Columns;

		var vertices = new WaterVertex[vertexCount];
		var indices = new List<uint>((_Rows - 1) * (_Columns - 1) * 6);

		for (int r = 0; r < _Rows; r++)
		{
			FlowSample sample = m_Samples[r];

			// Horizontal across vector (the water surface stays level across the channel)
			Vector3 across = new(sample.Direction.y, -sample.Direction.x, 0.0f);

			for (int c = 0; c < _Columns; c++)
			{
				float offset = ((float)c / (_Columns - 1) - 0.5f) * Width;

				vertices[r * _Columns + c] = new WaterVertex
				{
					Position = sample.Position + across * offset,
					Normal = Vector3.Up,
					Tangent = new Vector4(1, 0, 0, 1),
					// River-space UV: x = across offset, y = travel-time advected
					// along-coordinate (NOT raw distance — see FlowSample.Advected).
					// The shader evaluates waves and scrolls uniformly in this space;
					// the advection makes the pattern move at the local flow speed.
					TexCoord = new Vector2(offset, sample.Advected),
					// Flow state: rg = direction, b = speed. Raw floats, decoded by the shader.
					Color = new Color(sample.Direction.x, sample.Direction.y, sample.Speed, 1.0f)
				};
			}
		}

		// Same winding as the rectangular water grid (across = +X-like, along = +Y-like)
		for (int r = 0; r < _Rows - 1; r++)
		{
			for (int c = 0; c < _Columns - 1; c++)
			{
				uint i0 = (uint)(r * _Columns + c);
				uint i1 = i0 + 1;
				uint i2 = i0 + (uint)_Columns;
				uint i3 = i2 + 1;

				indices.Add(i0); indices.Add(i1); indices.Add(i2);
				indices.Add(i1); indices.Add(i3); indices.Add(i2);
			}
		}

		m_VertexBuffer = new GpuBuffer<WaterVertex>(vertexCount, GpuBuffer.UsageFlags.Vertex);
		m_VertexBuffer.SetData(vertices);

		m_IndexBuffer = new GpuBuffer<uint>(indices.Count, GpuBuffer.UsageFlags.Index);
		m_IndexBuffer.SetData(indices);

		m_TotalIndexCount = indices.Count;
	}



	// ─────────────────────────────────────────────────────────────────────────
	// Rendering (driven by WaterManager, same pipeline as WaterQuad)
	// ─────────────────────────────────────────────────────────────────────────

	internal void Draw(CommandList _CommandList)
	{
		if (!ParticipatesInRendering || !HasValidBuffers)
			return;

		_CommandList?.DrawIndexed(m_VertexBuffer, m_IndexBuffer, Material, 0, m_TotalIndexCount, m_DrawAttributes);
	}



	private void UpdateShaderAttributes()
	{
		m_DrawAttributes.Set("RequireWaterInclusionVolumes", false);

		WaterDefinition profile = WaterManager.GetWaveProfile(WaterType);

		if (profile.IsValid())
			profile.ApplyTo(m_DrawAttributes);

		m_DrawAttributes.Set("WaterTime", Time.Now);
		m_DrawAttributes.Set("DepthMax", Depth);

		// River UVs are raw world units, so the tiling factor alone sets the density
		// (one repeat per BASE_TILE_SIZE units, like the static quads).
		float tiling = TextureTilingMultiplier / BASE_TILE_SIZE;
		m_DrawAttributes.Set("NormalTiling", new Vector2(tiling, tiling));

		// Flow mode: waves travel downstream, normals scroll with the current
		m_DrawAttributes.Set("FlowWater", true);
		m_DrawAttributes.Set("FlowSpeedRef", FlowSpeedReference);

		// The river mesh is a uniform grid (no clipmap rings), so the wave-normal
		// finite-difference step is simply the cell size everywhere.
		m_DrawAttributes.Set("WaveNormalEpsScale", 0.0f);
		m_DrawAttributes.Set("WaveNormalEpsMin", BaseCellSize);

		WaterManager.Current?.ApplyRippleAttributes(m_DrawAttributes);
		WaterManager.Current?.ApplyCalmAttributes(m_DrawAttributes);

		SetWaterExclusionVolumes(WaterManager.GetViewPosition(Scene, WorldPosition));
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



	// ─────────────────────────────────────────────────────────────────────────
	// CPU queries (buoyancy, underwater detection, gameplay)
	// ─────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Finds the river surface under/over a world position. Returns false when the
	/// position is outside the river's footprint (further than Width/2 from the
	/// centerline). Height is the level water surface; direction/speed describe the
	/// local current.
	/// </summary>
	public bool TryGetSurfaceAt(Vector3 _WorldPosition, out float _SurfaceHeight, out Vector2 _FlowDirection, out float _FlowSpeed)
	{
		_SurfaceHeight = float.MinValue;
		_FlowDirection = Vector2.Zero;
		_FlowSpeed = 0.0f;

		if (m_Samples.Count < 2)
			return false;

		Vector2 p = new(_WorldPosition.x, _WorldPosition.y);

		float bestDistSq = float.MaxValue;
		int bestSegment = -1;
		float bestT = 0.0f;

		for (int i = 0; i < m_Samples.Count - 1; i++)
		{
			Vector2 a = new(m_Samples[i].Position.x, m_Samples[i].Position.y);
			Vector2 b = new(m_Samples[i + 1].Position.x, m_Samples[i + 1].Position.y);

			Vector2 ab = b - a;
			float lenSq = ab.LengthSquared;

			float t = lenSq > 1e-6f ? Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0.0f, 1.0f) : 0.0f;

			Vector2 closest = a + ab * t;
			float distSq = (p - closest).LengthSquared;

			if (distSq < bestDistSq)
			{
				bestDistSq = distSq;
				bestSegment = i;
				bestT = t;
			}
		}
		
		if (bestSegment < 0 || bestDistSq > (Width * 0.5f) * (Width * 0.5f))
			return false;
		
		const float endpointEpsilon = 0.001f;
		
		// Start cap
		if (bestSegment == 0 && bestT <= endpointEpsilon)
		{
			Vector2 start = new(m_Samples[0].Position.x, m_Samples[0].Position.y);

			Vector2 forward = m_Samples[0].Direction;

			float along = Vector2.Dot(p - start, forward);

			if (along < 0.0f)
				return false;
		}

		// End cap
		if (bestSegment == m_Samples.Count - 2 && bestT >= 1.0f - endpointEpsilon)
		{
			Vector2 end = new(m_Samples[^1].Position.x, m_Samples[^1].Position.y);

			Vector2 forward = m_Samples[^1].Direction;

			float along = Vector2.Dot(p - end, forward);

			if (along > 0.0f)
				return false;
		}

		FlowSample s0 = m_Samples[bestSegment];
		FlowSample s1 = m_Samples[bestSegment + 1];

		_SurfaceHeight = float.Lerp(s0.Position.z, s1.Position.z, bestT);
		_FlowDirection = Vector2.Lerp(s0.Direction, s1.Direction, bestT).Normal;
		_FlowSpeed = float.Lerp(s0.Speed, s1.Speed, bestT);

		return true;
	}



	/// <summary>World-space flow velocity (XY current) at a position, Zero outside the river.</summary>
	public Vector3 GetFlowVelocityAt(Vector3 _WorldPosition)
	{
		if (!TryGetSurfaceAt(_WorldPosition, out _, out Vector2 dir, out float speed))
			return Vector3.Zero;

		return new Vector3(dir.x, dir.y, 0.0f) * speed;
	}



	/// <summary>Whether the position is inside the river volume (footprint + depth).</summary>
	public bool ContainsPoint(Vector3 _WorldPosition)
	{
		if (!TryGetSurfaceAt(_WorldPosition, out float height, out _, out _))
			return false;

		return _WorldPosition.z <= height && _WorldPosition.z >= height - Depth;
	}



	/// <summary>World-space AABB enclosing the river ribbon — used for frustum culling.</summary>
	public BBox GetWorldBounds()
	{
		if (m_Samples.Count == 0)
			return new BBox(WorldPosition, WorldPosition);

		Vector3 min = m_Samples[0].Position;
		Vector3 max = min;

		for (int i = 1; i < m_Samples.Count; i++)
		{
			min = Vector3.Min(min, m_Samples[i].Position);
			max = Vector3.Max(max, m_Samples[i].Position);
		}

		// Widen for the channel half-width, then drop the floor by the river depth
		BBox bounds = new BBox(min, max).Grow(Width * 0.5f);

		Vector3 mins = bounds.Mins;
		mins.z -= Depth;

		return new BBox(mins, bounds.Maxs);
	}



	// ─────────────────────────────────────────────────────────────────────────
	// Gizmos
	// ─────────────────────────────────────────────────────────────────────────

	protected override void DrawGizmos()
	{
		if (!Gizmo.IsSelected && !Gizmo.IsHovered)
			return;

		// Read-only flow visualization. Interactive spline point/tangent editing is
		// owned by the WaterFlowTool editor tool (activates when this is selected).

		// Centerline (spline points are local, so default gizmo space is correct)
		Gizmo.Draw.Color = Color.Cyan;

		var polyline = Spline.ConvertToPolyline();

		for (int i = 0; i < polyline.Count - 1; i++)
			Gizmo.Draw.Line(polyline[i], polyline[i + 1]);

		if (!Gizmo.IsSelected || m_Samples.Count < 2)
			return;

		// Banks
		Transform wt = WorldTransform;
		float halfWidth = Width * 0.5f;

		for (int i = 0; i < m_Samples.Count - 1; i++)
		{
			FlowSample s0 = m_Samples[i];
			FlowSample s1 = m_Samples[i + 1];

			Vector3 across0 = new(s0.Direction.y, -s0.Direction.x, 0.0f);
			Vector3 across1 = new(s1.Direction.y, -s1.Direction.x, 0.0f);
			
			Vector3 positionA = s0.Position;
			Vector3 positionB = s1.Position;
			
			Gizmo.Draw.Line(wt.PointToLocal(positionA + across0 * halfWidth), wt.PointToLocal(positionB + across1 * halfWidth));
			Gizmo.Draw.Line(wt.PointToLocal(positionA - across0 * halfWidth), wt.PointToLocal(positionB - across1 * halfWidth));
			
			Vector3 positionC = positionA.WithZ(positionA.z - Depth);
			Vector3 positionD = positionB.WithZ(positionB.z - Depth);
			
			Gizmo.Draw.Line(wt.PointToLocal(positionC + across0 * halfWidth), wt.PointToLocal(positionD + across1 * halfWidth));
			Gizmo.Draw.Line(wt.PointToLocal(positionC - across0 * halfWidth), wt.PointToLocal(positionD - across1 * halfWidth));

			if (i == 0 || i == m_Samples.Count - 2)
			{
				Gizmo.Draw.Line(wt.PointToLocal(positionA + across0 * halfWidth), wt.PointToLocal(positionC + across0 * halfWidth));
				Gizmo.Draw.Line(wt.PointToLocal(positionB - across0 * halfWidth), wt.PointToLocal(positionD - across0 * halfWidth));
				
				Gizmo.Draw.Line(wt.PointToLocal(positionA + across0 * halfWidth), wt.PointToLocal(positionA - across0 * halfWidth));
				Gizmo.Draw.Line(wt.PointToLocal(positionC + across0 * halfWidth), wt.PointToLocal(positionC - across0 * halfWidth));
			}
		}

		// Flow arrows colored by speed (cyan = calm, red = rushing)
		int arrowStep = Math.Max(1, m_Samples.Count / 24);

		for (int i = 0; i < m_Samples.Count; i += arrowStep)
		{
			FlowSample s = m_Samples[i];

			float intensity = Math.Clamp(s.Speed / MathF.Max(FlowSpeedReference * 2.0f, 1.0f), 0.0f, 1.0f);

			Vector3 dir3 = new(s.Direction.x, s.Direction.y, 0.0f);
			Vector3 tip = s.Position + dir3 * MathF.Min(Width * 0.4f, 24.0f + s.Speed * 0.3f);

			Gizmo.Draw.Color = Color.Lerp(Color.Cyan, Color.Red, intensity);
			Gizmo.Draw.Arrow(wt.PointToLocal(s.Position), wt.PointToLocal(tip), 8.0f, 4.0f);
		}
	}
}
