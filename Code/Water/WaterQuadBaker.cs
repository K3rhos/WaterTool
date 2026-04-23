using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Audio;

namespace RedSnail.WaterTool;

[Icon("water_drop"), Group("Water"), Title("Water Quad Baker")]
public sealed class WaterQuadBaker : Component, Component.ExecuteInEditor
{
	private const string BakedContainerName = "Water Volumes";
	private const string BakedTag = "water_quad_bake";

	private readonly List<Terrain> _terrains = new();
	private readonly HashSet<Collider> _solidColliders = new();
	private float _insideTraceDistance;
	private int _physicsCreatedCount;
	private int _skippedInsideCount;
	private int _subdividedCount;

	[Property, Group("Water"), Order(0)] public WaterBodyType WaterType { get; set; } = WaterBodyType.Ocean;

	[Property, Group("Bake Bounds")] public Vector2 BakeSizeXY { get; set; } = new(10000.0f, 10000.0f);
	[Property, Group("Bake Bounds")] public float WaterSurfaceZ { get; set; } = 0.0f;
	[Property, Group("Bake Bounds")] public float WaterDepth { get; set; } = 1000.0f;

	[Property, Group("Strict Pass"), Range(256.0f, 8192.0f), Order(2)] public float MinCellSize { get; set; } = 4096.0f;
	[Property, Group("Strict Pass"), Range(1, 12)] public int MaxDepth { get; set; } = 6;
	[Property, Group("Strict Pass"), Range(0.0f, 64.0f)] public float QuadInset { get; set; } = 0.0f;
	[Property, Group("Strict Pass"), Range(1.0f, 128.0f)] public float SolidProbeRadius { get; set; } = 8.0f;
	[Property, Group("Strict Pass"), Range(0.0f, 256.0f)] public float TerrainPadding { get; set; } = 16.0f;
	[Property, Group("Strict Pass")] public bool IgnoreTerrainBelowWaterSurface { get; set; } = true;
	[Property, Group("Strict Pass"), Range(0.0f, 5000.0f)] public float TerrainDepthIgnoreDistance { get; set; } = 512.0f;

	[Property, Group("Coastal Fill"), Order(3)] public bool EnableCoastalFill { get; set; } = true;
	[Property, Group("Coastal Fill"), Range(256.0f, 8192.0f)] public float CoastalFillMaxCellSize { get; set; } = 4096.0f;
	[Property, Group("Coastal Fill"), Range(0.0f, 5000.0f)] public float CoastalFillPenetrationDistance { get; set; } = 192.0f;
	[Property, Group("Coastal Fill"), Range(0.1f, 1.0f)] public float CoastalFillInlandThreshold { get; set; } = 1.0f;

	[Property, ToggleGroup("Soundscape"), Order(4)] public bool Soundscape { get; set; } = false;
	[Property, Group("Soundscape")] public Soundscape SoundscapeAsset { get; set; }
	[Property, Group("Soundscape")] public MixerHandle SoundscapeTargetMixer { get; set; }
	[Property, Group("Soundscape")] public bool SoundscapeStayActiveOnExit { get; set; } = true;
	[Property, Group("Soundscape"), Range(0.0f, 2.0f)] public float SoundscapeVolume { get; set; } = 1.0f;
	
	[Property, Group("Miscellaneous")] public bool ExcludeMeshGeometry { get; set; } = false;



	[Button]
	private async Task Bake()
	{
		CacheSceneGeometry();
		ClearBaked();

		_physicsCreatedCount = 0;
		_skippedInsideCount = 0;
		_subdividedCount = 0;

		// Traverse the octree synchronously to collect candidate boxes.
		var pending = new List<BBox>();

		CollectPhysicsNodes(GetLocalBakeBox(), 0, pending);

		// Create volumes with an editor progress bar.
		var container = GetOrCreateBakedContainer();

		await Application.Editor.ForEachAsync(pending, "Baking Water Volumes", async (box, ct) =>
		{
			if (CreateWaterBody(container, box))
				_physicsCreatedCount++;

			await Task.Delay(1, ct);
		});

		Log.Info($"{nameof(WaterQuadBaker)}: baked {_physicsCreatedCount} water volume set(s), skipped {_skippedInsideCount} node(s), subdivided {_subdividedCount} node(s).");
	}



	[Button]
	private void ClearBaked()
	{
		FindBakedContainer()?.Destroy();
	}



	protected override void DrawGizmos()
	{
		if (!Gizmo.IsSelected)
			return;

		Gizmo.Draw.Color = Color.Green;
		Gizmo.Draw.LineBBox(GetLocalBakeBox());

		Gizmo.Draw.Color = Color.Blue;

		foreach (var waterBody in GetComponentsInChildren<WaterBody>())
		{
			var (center, forward, up, half) = waterBody.GetWorldOBB();

			Gizmo.Draw.LineBBox(BBox.FromPositionAndSize(center, half * 2));
		}
	}



	private void CacheSceneGeometry()
	{
		_terrains.Clear();
		_solidColliders.Clear();

		foreach (var terrain in Scene.GetAllComponents<Terrain>())
		{
			if (!terrain.IsValid() || !terrain.Enabled || !terrain.Active || !terrain.EnableCollision || terrain.Storage is null)
				continue;

			_terrains.Add(terrain);
			_solidColliders.Add(terrain);
		}

		foreach (var collider in Scene.GetAllComponents<Collider>())
		{
			if (!collider.IsValid() || !collider.Enabled || !collider.Active || collider.IsTrigger)
				continue;

			if (collider.GameObject.Tags.Has(BakedTag))
				continue;
			
			if (ExcludeMeshGeometry && collider is not Terrain)
				continue;

			_solidColliders.Add(collider);
		}

		_insideTraceDistance = Math.Max(BakeSizeXY.Length * 2.0f, 10000.0f);
	}



	private void CollectPhysicsNodes(BBox _LocalBox, int _Depth, List<BBox> _Pending)
	{
		var sample = ClassifyNode(_LocalBox);

		bool terrainRejected = sample.TerrainAllInside || (sample.TerrainMixed && !sample.MeshHasAny);
		bool meshRejected = sample.MeshAllInside;
		bool overlapsNonTerrainSolid = BoxOverlapsNonTerrainSolid(_LocalBox);

		if (meshRejected)
		{
			_skippedInsideCount++;

			return;
		}

		if (terrainRejected)
		{
			if (TryHandleCoastalNode(_LocalBox, _Depth, _Pending, sample))
				return;

			_skippedInsideCount++;

			return;
		}

		bool shouldSubdivide = sample.MeshMixed || sample.TerrainMixed || overlapsNonTerrainSolid;

		if (shouldSubdivide && CanSubdivide(_LocalBox, _Depth))
		{
			_subdividedCount++;

			foreach (var child in Subdivide(_LocalBox))
				CollectPhysicsNodes(child, _Depth + 1, _Pending);

			return;
		}

		if (shouldSubdivide)
		{
			_skippedInsideCount++;

			return;
		}

		_Pending.Add(_LocalBox);
	}



	private SampleSummary ClassifyNode(BBox _LocalBox)
	{
		int total = 0;
		int terrainInside = 0;
		int meshInside = 0;

		foreach (var localPoint in EnumerateSamplePoints(_LocalBox))
		{
			total++;

			var worldPoint = WorldTransform.PointToWorld(localPoint);

			if (IsPointInsideTerrainOnly(worldPoint))
				terrainInside++;

			if (IsPointInsideSolidMeshOnly(worldPoint))
				meshInside++;
		}

		return new SampleSummary
		{
			Total = total,
			TerrainInside = terrainInside,
			MeshInside = meshInside
		};
	}



	private bool TryHandleCoastalNode(BBox _LocalBox, int _Depth, List<BBox> _Pending, SampleSummary _Sample)
	{
		if (!EnableCoastalFill || _Sample.MeshHasAny)
			return false;

		float maxSize = Math.Max(_LocalBox.Size.x, _LocalBox.Size.y);

		if (maxSize > CoastalFillMaxCellSize)
		{
			_subdividedCount++;

			foreach (var child in Subdivide(_LocalBox))
				CollectPhysicsNodes(child, _Depth + 1, _Pending);

			return true;
		}

		if (IsCellTooFarInland(_LocalBox))
			return false;

		_Pending.Add(_LocalBox);

		return true;
	}



	private bool IsCellTooFarInland(BBox _LocalBox)
	{
		int inlandCount = 0;
		int total = 0;

		foreach (var localPoint in EnumerateXYSamplePoints(_LocalBox))
		{
			total++;

			var worldPoint = WorldTransform.PointToWorld(localPoint);

			if (IsInlandAtXY(worldPoint))
				inlandCount++;
		}

		return total > 0 && ((float)inlandCount / total) >= CoastalFillInlandThreshold;
	}



	private bool IsInlandAtXY(Vector3 _WorldPoint)
	{
		if (!IsLandAtXY(_WorldPoint))
			return false;

		if (CoastalFillPenetrationDistance <= 0.0f)
			return true;

		Vector3[] offsets =
		[
			Vector3.Right * CoastalFillPenetrationDistance,
			Vector3.Left * CoastalFillPenetrationDistance,
			Vector3.Forward * CoastalFillPenetrationDistance,
			Vector3.Backward * CoastalFillPenetrationDistance
		];

		foreach (var offset in offsets)
		{
			if (!IsLandAtXY(_WorldPoint + offset))
				return false;
		}

		return true;
	}



	private bool IsLandAtXY(Vector3 _WorldPoint)
	{
		foreach (var terrain in _terrains)
		{
			if (TryGetTerrainSurfaceWorldHeight(terrain, _WorldPoint, out var worldHeight) && IsTerrainHeightBlocking(worldHeight))
				return true;
		}

		return false;
	}



	private bool IsPointInsideTerrainOnly(Vector3 _WorldPoint)
	{
		foreach (var terrain in _terrains)
		{
			if (TryGetTerrainSurfaceWorldHeight(terrain, _WorldPoint, out var worldHeight) && IsTerrainHeightBlocking(worldHeight))
			{
				if (_WorldPoint.z <= worldHeight + TerrainPadding)
					return true;
			}
		}

		return false;
	}



	private bool IsTerrainHeightBlocking(float _SampledWorldHeight)
	{
		if (IgnoreTerrainBelowWaterSurface && _SampledWorldHeight <= WaterSurfaceZ - TerrainDepthIgnoreDistance)
			return false;

		return _SampledWorldHeight >= WaterSurfaceZ + TerrainPadding;
	}



	private bool IsPointInsideSolidMeshOnly(Vector3 _WorldPoint)
	{
		if (ExcludeMeshGeometry)
			return false;
		
		var probe = Scene.Trace
			.Sphere(SolidProbeRadius, _WorldPoint, _WorldPoint)
			.WithoutTags(BakedTag)
			.Run();

		if (probe.StartedSolid && probe.Collider is not Terrain)
			return true;

		int oddAxes = 0;

		if (HasOddHitCount(_WorldPoint, Vector3.Right)) oddAxes++;
		if (HasOddHitCount(_WorldPoint, Vector3.Forward)) oddAxes++;
		if (HasOddHitCount(_WorldPoint, Vector3.Up)) oddAxes++;

		return oddAxes >= 2;
	}



	private bool HasOddHitCount(Vector3 _Start, Vector3 _Direction)
	{
		if (ExcludeMeshGeometry)
			return false;
		
		var end = _Start + _Direction.Normal * _insideTraceDistance;

		var hits = Scene.Trace
			.Ray(_Start, end)
			.WithoutTags(BakedTag)
			.RunAll();

		int hitCount = 0;
		Collider lastCollider = null;
		float lastFraction = -10.0f;

		foreach (var hit in hits)
		{
			if (!hit.Hit || hit.Collider is null)
				continue;

			if (!_solidColliders.Contains(hit.Collider) || hit.Collider is Terrain)
				continue;

			if (hit.Collider == lastCollider && Math.Abs(hit.Fraction - lastFraction) < 0.0001f)
				continue;

			lastCollider = hit.Collider;
			lastFraction = hit.Fraction;
			hitCount++;
		}

		return (hitCount & 1) == 1;
	}



	private bool BoxOverlapsNonTerrainSolid(BBox _LocalBox)
	{
		if (ExcludeMeshGeometry)
			return false;
		
		var center = WorldTransform.PointToWorld(_LocalBox.Center);

		var hits = Scene.Trace
			.Box(_LocalBox.Size, center, center)
			.Rotated(WorldRotation)
			.WithoutTags(BakedTag)
			.RunAll();

		foreach (var hit in hits)
		{
			if (hit.Hit && hit.Collider is not null && hit.Collider is not Terrain)
				return true;
		}

		return false;
	}



	private static bool TryGetTerrainSurfaceWorldHeight(Terrain _Terrain, Vector3 _WorldPoint, out float _SampledWorldHeight)
	{
		_SampledWorldHeight = 0.0f;

		var storage = _Terrain.Storage;

		if (storage is null || storage.HeightMap is null || storage.ControlMap is null || storage.Resolution <= 1)
			return false;

		var localPoint = _Terrain.WorldTransform.PointToLocal(_WorldPoint);

		if (localPoint.x < 0.0f || localPoint.y < 0.0f || localPoint.x > storage.TerrainSize || localPoint.y > storage.TerrainSize)
			return false;

		int resolution = storage.Resolution;
		float gridX = (localPoint.x / storage.TerrainSize) * (resolution - 1);
		float gridY = (localPoint.y / storage.TerrainSize) * (resolution - 1);

		int x0 = (int)MathF.Floor(gridX).Clamp(0, resolution - 1);
		int y0 = (int)MathF.Floor(gridY).Clamp(0, resolution - 1);
		int x1 = (x0 + 1).Clamp(0, resolution - 1);
		int y1 = (y0 + 1).Clamp(0, resolution - 1);

		var control = new CompactTerrainMaterial(storage.ControlMap[x0 + y0 * resolution]);

		if (control.IsHole)
			return false;

		float tx = gridX - x0;
		float ty = gridY - y0;
		float h00 = storage.HeightMap[x0 + y0 * resolution];
		float h10 = storage.HeightMap[x1 + y0 * resolution];
		float h01 = storage.HeightMap[x0 + y1 * resolution];
		float h11 = storage.HeightMap[x1 + y1 * resolution];
		float hx0 = MathX.Lerp(h00, h10, tx);
		float hx1 = MathX.Lerp(h01, h11, tx);
		float sampledLocalHeight = MathX.Lerp(hx0, hx1, ty) * (storage.TerrainHeight / ushort.MaxValue);

		_SampledWorldHeight = _Terrain.WorldTransform.PointToWorld(new Vector3(localPoint.x, localPoint.y, sampledLocalHeight)).z;

		return true;
	}



	private static IEnumerable<Vector3> EnumerateSamplePoints(BBox _LocalBox)
	{
		for (int ix = 0; ix < 3; ix++)
			for (int iy = 0; iy < 3; iy++)
				for (int iz = 0; iz < 3; iz++)
				{
					yield return new Vector3(
						MathX.Lerp(_LocalBox.Mins.x, _LocalBox.Maxs.x, ix * 0.5f),
						MathX.Lerp(_LocalBox.Mins.y, _LocalBox.Maxs.y, iy * 0.5f),
						MathX.Lerp(_LocalBox.Mins.z, _LocalBox.Maxs.z, iz * 0.5f)
					);
				}
	}



	private static IEnumerable<Vector3> EnumerateXYSamplePoints(BBox _LocalBox)
	{
		float z = _LocalBox.Center.z;

		for (int ix = 0; ix < 5; ix++)
			for (int iy = 0; iy < 5; iy++)
			{
				yield return new Vector3(
					MathX.Lerp(_LocalBox.Mins.x, _LocalBox.Maxs.x, ix / 4.0f),
					MathX.Lerp(_LocalBox.Mins.y, _LocalBox.Maxs.y, iy / 4.0f),
					z
				);
			}
	}



	private bool CanSubdivide(BBox _LocalBox, int _Depth)
	{
		if (_Depth >= MaxDepth)
			return false;

		var size = _LocalBox.Size;

		return size.x > MinCellSize || size.y > MinCellSize;
	}



	private static IEnumerable<BBox> Subdivide(BBox _LocalBox)
	{
		var center = _LocalBox.Center;
		var mins = _LocalBox.Mins;
		var maxs = _LocalBox.Maxs;

		for (int ix = 0; ix < 2; ix++)
			for (int iy = 0; iy < 2; iy++)
			{
				yield return new BBox(
					new Vector3(ix == 0 ? mins.x : center.x, iy == 0 ? mins.y : center.y, mins.z),
					new Vector3(ix == 0 ? center.x : maxs.x, iy == 0 ? center.y : maxs.y, maxs.z)
				);
			}
	}



	private bool CreateWaterBody(GameObject _Container, BBox _LocalBox)
	{
		float width = _LocalBox.Size.x - QuadInset * 2.0f;
		float length = _LocalBox.Size.y - QuadInset * 2.0f;

		if (width <= 1.0f || length <= 1.0f)
			return false;

		var go = new GameObject(_Container, true, "Water Volume");
		go.Tags.Add(BakedTag);

		var worldPoint = WorldTransform.PointToWorld(_LocalBox.Center);

		go.WorldPosition = new Vector3(worldPoint.x, worldPoint.y, WaterSurfaceZ - WaterDepth * 0.5f);
		go.WorldRotation = WorldRotation;
		go.WorldScale = 1.0f;

		var bounds = new BBox
		(
			new Vector3(-width * 0.5f, -length * 0.5f, -WaterDepth * 0.5f),
			new Vector3(width * 0.5f, length * 0.5f, WaterDepth * 0.5f)
		);

		var body = go.GetOrAddComponent<WaterBody>();
		body.SetBounds(bounds);
		body.WaterType = WaterType;

		if (Soundscape)
			CreateSoundscapeTrigger(go, width, length);

		return true;
	}



	private void CreateSoundscapeTrigger(GameObject _Parent, float _Width, float _Length)
	{
		var finalExtents = new Vector3(_Width * 0.5f, _Length * 0.5f, WaterDepth * 2.0f);

		if (finalExtents.x <= 1.0f || finalExtents.y <= 1.0f || finalExtents.z <= 1.0f)
			return;

		var go = new GameObject(_Parent, true, "Water Soundscape");
		go.Tags.Add(BakedTag);
		go.LocalPosition = Vector3.Zero;
		go.LocalRotation = Rotation.Identity;
		go.LocalScale = 1.0f;

		var trigger = go.GetOrAddComponent<SoundscapeTrigger>();
		trigger.Type = SoundscapeTrigger.TriggerType.Box;
		trigger.Soundscape = SoundscapeAsset;
		trigger.TargetMixer = SoundscapeTargetMixer;
		trigger.StayActiveOnExit = SoundscapeStayActiveOnExit;
		trigger.Volume = SoundscapeVolume;
		trigger.BoxSize = finalExtents;
	}



	private BBox GetLocalBakeBox()
	{
		float minZ = WaterSurfaceZ - WaterDepth;
		float maxZ = WaterSurfaceZ;

		var mins = new Vector3(-BakeSizeXY.x * 0.5f, -BakeSizeXY.y * 0.5f, minZ);
		var maxs = new Vector3(BakeSizeXY.x * 0.5f, BakeSizeXY.y * 0.5f, maxZ);

		return new BBox(mins, maxs);
	}



	private GameObject GetOrCreateBakedContainer()
	{
		var existing = FindBakedContainer();

		if (existing.IsValid())
			return existing;

		var container = new GameObject(GameObject, true, BakedContainerName);
		container.Tags.Add("container");
		container.Tags.Add(BakedTag);
		container.LocalPosition = Vector3.Zero;
		container.LocalRotation = Rotation.Identity;
		container.LocalScale = 1.0f;

		return container;
	}



	private GameObject FindBakedContainer()
	{
		return GameObject.Children.FirstOrDefault(child => child.IsValid() && child.Tags.Has("container"));
	}



	private struct SampleSummary
	{
		public int Total;
		public int TerrainInside;
		public int MeshInside;

		public bool TerrainAllInside => Total > 0 && TerrainInside == Total;
		public bool TerrainMixed => TerrainInside > 0 && TerrainInside < Total;
		public bool MeshAllInside => Total > 0 && MeshInside == Total;
		public bool MeshHasAny => MeshInside > 0;
		public bool TerrainHasAny => TerrainInside > 0;
		public bool MeshMixed => MeshInside > 0 && MeshInside < Total;
	}
}
