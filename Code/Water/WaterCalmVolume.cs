using Sandbox;
using Sandbox.Volumes;

namespace RedSnail.WaterTool;

/// <summary>
/// Calms the water inside a volume: wave displacement (and the surface normals that
/// come from it) smoothly fade to flat. Affects every water surface — WaterQuad,
/// WaterBodyRenderer and WaterFlow — so it's the clean way to blend two of them
/// together. The classic use is a river mouth meeting an ocean: drop a calm volume
/// over the junction, set both surfaces to the same height there, and the wave
/// mismatch (ocean chop poking above the river, seams) disappears.
///
/// Purely visual — it doesn't touch buoyancy, swimming or the flow current.
/// </summary>
[Title("Water Calm Volume")]
[Category("Volumes")]
[Icon("water")]
public sealed class WaterCalmVolume : VolumeComponent, Component.ExecuteInEditor
{
	// 0 = no effect, 1 = perfectly flat at the core. Lets a volume only partially
	// settle the water if you want some residual motion.
	[Property, Range(0.0f, 1.0f)] public float Strength { get; set; } = 1.0f;

	// Fraction of the volume (from each face inward) over which the calming ramps in.
	// 0 = hard edge (a visible crease), 1 = ramps all the way from the center.
	[Property, Range(0.05f, 1.0f)] public float Falloff { get; set; } = 0.4f;
	
	
	
	protected override void OnEnabled()
	{
		WaterManager.Current?.RefreshWaterCalmVolumesList();
	}
	
	protected override void OnDisabled()
	{
		WaterManager.Current?.RefreshWaterCalmVolumesList();
	}

	protected override void DrawGizmos()
	{
		base.DrawGizmos();

		if (!Gizmo.IsSelected)
			return;

		// Faint fill so calm volumes read differently from exclusion volumes
		BBox box = SceneVolume.GetBounds();

		Gizmo.Draw.Color = Color.Cyan.WithAlpha(0.06f);
		Gizmo.Draw.SolidBox(box);
	}

	public (Vector3 Center, Vector3 Forward, Vector3 Up, Vector3 HalfExtents) GetWorldOBB()
	{
		BBox local = SceneVolume.GetBounds();
		Vector3 center = WorldTransform.PointToWorld(local.Center);
		Vector3 halfExtents = local.Size * 0.5f;

		return (center, WorldRotation.Forward, WorldTransform.Up, halfExtents);
	}
}
