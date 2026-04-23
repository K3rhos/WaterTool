using System;
using Sandbox;
using Sandbox.Volumes;

namespace RedSnail.WaterTool;

/// <summary>
/// Defines a discrete body of water that participates in a renderer-driven water system.
/// Provides volume bounds, a physics hull for buoyancy/swimming, and renderer inclusion in one component.
/// Requires a WaterQuadRenderer present in the scene to produce a visible water surface.
/// </summary>
[Title("Water Body")]
[Category("Water")]
[Icon("water_drop")]
public sealed class WaterBody : VolumeComponent, Component.ExecuteInEditor
{
	private HullCollider m_HullCollider;
	private BBox m_LastLocalBounds;

	[Property, Group("General")] public WaterBodyType WaterType { get; set; } = WaterBodyType.Ocean;

	protected override void OnEnabled()
	{
		WaterManager.Current?.Register(this);

		UpdateColliderState();

		m_LastLocalBounds = SceneVolume.GetBounds();
	}

	protected override void OnDisabled()
	{
		WaterManager.Current?.Unregister(this);

		m_HullCollider?.Destroy();
		m_HullCollider = null;
	}

	protected override void OnUpdate()
	{
		BBox localBounds = SceneVolume.GetBounds();

		if (localBounds != m_LastLocalBounds)
		{
			UpdateColliderState();

			m_LastLocalBounds = localBounds;
		}
	}

	protected override void DrawGizmos()
	{
		if (!Gizmo.IsSelected || !m_HullCollider.IsValid())
			return;

		Gizmo.Draw.Color = Color.Cyan;
		Gizmo.Draw.LineBBox(m_HullCollider.LocalBounds);
	}

	// Bounds
	public void SetBounds(BBox bounds)
	{
		SceneVolume = SceneVolume with { Box = bounds };
	}

	public float GetSurfaceHeight()
	{
		BBox local = SceneVolume.GetBounds();

		return WorldTransform.PointToWorld(new Vector3(local.Center.x, local.Center.y, local.Maxs.z)).z;
	}

	public bool ContainsPointXY(Vector3 worldPosition)
	{
		BBox local = SceneVolume.GetBounds();
		Vector3 point = WorldTransform.PointToLocal(worldPosition);
		Vector3 half = local.Size * 0.5f;

		return MathF.Abs(point.x - local.Center.x) <= half.x && MathF.Abs(point.y - local.Center.y) <= half.y;
	}

	public bool ContainsPointInVolume(Vector3 worldPosition)
	{
		BBox local = SceneVolume.GetBounds();

		Vector3 point = WorldTransform.PointToLocal(worldPosition);
		Vector3 half = local.Size * 0.5f;

		return MathF.Abs(point.x - local.Center.x) <= half.x &&
			   MathF.Abs(point.y - local.Center.y) <= half.y &&
			   MathF.Abs(point.z - local.Center.z) <= half.z;
	}

	public (Vector3 Center, Vector3 Forward, Vector3 Up, Vector3 HalfExtents) GetWorldOBB()
	{
		BBox local = SceneVolume.GetBounds();

		return (WorldTransform.PointToWorld(local.Center), WorldRotation.Forward, WorldTransform.Up, local.Size * 0.5f);
	}

	// Wave queries
	public Vector3 GetWaveDisplacementAt(Vector3 _WorldPosition)
	{
		WaterDefinition profile = WaterManager.GetWaveProfile(WaterType);

		return profile.IsValid() ? WaterWaveUtility.ComputeDisplacementAt(_WorldPosition, profile) : Vector3.Zero;
	}

	public Vector3 GetWaveVelocityAt(Vector3 _WorldPosition)
	{
		WaterDefinition profile = WaterManager.GetWaveProfile(WaterType);

		return profile.IsValid() ? WaterWaveUtility.ComputeVelocityAt(_WorldPosition, profile) : Vector3.Zero;
	}

	public float GetWaveHeightAt(Vector3 _WorldPosition) => GetSurfaceHeight() + GetWaveDisplacementAt(_WorldPosition).z;

	internal float GetVerticalDistanceToSurface(Vector3 _WorldPosition) => MathF.Abs(_WorldPosition.z - GetSurfaceHeight());

	private void UpdateColliderState()
	{
		BBox local = SceneVolume.GetBounds();

		m_HullCollider = GetOrAddComponent<HullCollider>();
		m_HullCollider.Flags |= ComponentFlags.Hidden;
		m_HullCollider.Static = true;
		m_HullCollider.Type = HullCollider.PrimitiveType.Box;
		m_HullCollider.Center = local.Center;
		m_HullCollider.BoxSize = local.Size;
		m_HullCollider.IsTrigger = true;

		Tags.Add("water");
	}
}
