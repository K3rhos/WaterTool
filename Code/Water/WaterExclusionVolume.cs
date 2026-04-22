using Sandbox;
using Sandbox.Volumes;

namespace RedSnail.WaterTool;

/// <summary>
/// Suppresses water surface rendering inside a volume. Has no effect on the physical water hull
/// so buoyancy and swimming still work within the excluded area.
/// Intended for enclosed spaces that sit in water, such as the interior of a boat or submarine.
/// </summary>
[Title("Water Exclusion Volume")]
[Category("Volumes")]
[Icon("water")]
public sealed class WaterExclusionVolume : VolumeComponent, Component.ExecuteInEditor
{
	private bool m_CalledOnValidate;

	protected override void OnEnabled()
	{
		WaterManager.Get(Scene)?.Register(this);
	}

	protected override void OnValidate()
	{
		m_CalledOnValidate = true;
	}

	protected override void OnDisabled()
	{
		// We don't want to unregister if OnValidate() was called — that means we just saved the scene.
		if (m_CalledOnValidate)
		{
			m_CalledOnValidate = false;
			return;
		}

		WaterManager.Get(Scene)?.Unregister(this);
	}

	protected override void DrawGizmos()
	{
		base.DrawGizmos();

		/*
		SceneVolume sceneVolume = SceneVolume;
		Gizmo.Draw.IgnoreDepth = false;
		Gizmo.Draw.Color = Gizmo.Colors.Blue.WithAlpha(0.8f);
		Gizmo.Draw.SolidBox(sceneVolume.Box);
		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = global::Color.White.WithAlpha(0.05f);
		Gizmo.Draw.SolidBox(sceneVolume.Box);
		
		SceneVolume = sceneVolume;
		*/
	}

	protected override void OnUpdate()
	{
		// DebugOverlay.Box(GetWorldBounds(), Color.Cyan, overlay: true);
	}

	public (Vector3 Center, Vector3 Forward, Vector3 Up, Vector3 HalfExtents) GetWorldOBB()
	{
		BBox local = SceneVolume.GetBounds();
		Vector3 center = WorldTransform.PointToWorld(local.Center);
		Vector3 halfExtents = local.Size * 0.5f;

		return (center, WorldRotation.Forward, WorldTransform.Up, halfExtents);
	}

	public void SetLocalBounds(BBox localBounds)
	{
		var sv = SceneVolume;
		sv.Box = localBounds;
		SceneVolume = sv;
	}
}
