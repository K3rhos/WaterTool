using System.Collections.Generic;
using Sandbox;
using Sandbox.Rendering;
using RenderStage = Sandbox.Rendering.Stage;

namespace RedSnail.WaterTool;

[Title("Water Manager")]
public partial class WaterManager : GameObjectSystem<WaterManager>
{
	[Property(Title = "Ocean"), Group("Profile"), Order(0)] public WaterDefinition OceanWaveProfile { get; set; }
	[Property(Title = "Lake"), Group("Profile")] public WaterDefinition LakeWaveProfile { get; set; }
	[Property(Title = "River"), Group("Profile")] public WaterDefinition RiverWaveProfile { get; set; }
	[Property(Title = "Pool"), Group("Profile")] public WaterDefinition PoolWaveProfile { get; set; }
	[Property(Title = "Custom"), Group("Profile")] public WaterDefinition CustomWaveProfile { get; set; }

	[Property(Title = "Underwater Volume"), Group("Post Processing")] public PostProcessVolume UnderwaterPostProcessVolume { get; set; }

	private SceneCustomObject m_SceneObject;
	private readonly ComputeShader m_ComputeShader;
	private readonly CommandList m_CommandList = new("Water Quads");
	private CameraComponent m_LastCamera;
	private Vector3 m_CameraPosition;
	private readonly WaterDefinition m_DefaultProfile;

	private List<WaterQuad> Quads { get; } = [];
	private List<WaterBodyRenderer> QuadRenderers { get; } = [];
	public List<WaterBody> Bodies { get; } = [];
	public List<WaterExclusionVolume> ExclusionVolumes { get; } = [];
	public List<HullWaterExclusionVolume> HullExclusionVolumes { get; } = [];



	public WaterManager(Scene _Scene) : base(_Scene)
	{
		m_ComputeShader = new ComputeShader("water_clipmap_cs");

		m_SceneObject = new SceneCustomObject(_Scene.SceneWorld)
		{
			RenderOverride = RenderAll,
			Transform = new Transform(Vector3.Zero, Rotation.Identity),
			Flags =
			{
				IsOpaque = false,
				IsTranslucent = true,
				WantsFrameBufferCopy = false,
				WantsPrePass = false
			}
		};

		m_DefaultProfile = new WaterDefinition();

		Listen(Stage.StartUpdate, 0, Update, "WaterManagerUpdate");
	}



	public override void Dispose()
	{
		m_LastCamera?.RemoveCommandList(m_CommandList);

		m_SceneObject?.Delete();
		m_SceneObject = null;

		base.Dispose();
	}



	private void Update()
	{
		var camera = Scene.Camera;

		if (camera != m_LastCamera)
		{
			m_LastCamera?.RemoveCommandList(m_CommandList);

			camera?.AddCommandList(m_CommandList, RenderStage.AfterTransparent);

			m_LastCamera = camera;
		}

		if (LoadingScreen.IsVisible || Game.IsPlaying)
		{
			m_CameraPosition = camera?.WorldPosition ?? Vector3.Zero;
		}
		else
		{
			m_CameraPosition = Application.Editor.Camera.WorldPosition;
		}

		if (UnderwaterPostProcessVolume.IsValid())
			UnderwaterPostProcessVolume.Enabled = IsPositionInsideAny(m_CameraPosition);
	}



	internal void Register(WaterQuad quad)
	{
		if (!Quads.Contains(quad))
			Quads.Add(quad);
	}

	internal void Unregister(WaterQuad quad)
	{
		Quads.Remove(quad);
	}



	internal void Register(WaterBodyRenderer renderer)
	{
		if (!QuadRenderers.Contains(renderer))
			QuadRenderers.Add(renderer);
	}

	internal void Unregister(WaterBodyRenderer renderer)
	{
		QuadRenderers.Remove(renderer);
	}



	internal void Register(WaterBody body)
	{
		if (!Bodies.Contains(body))
			Bodies.Add(body);
	}

	internal void Unregister(WaterBody body)
	{
		Bodies.Remove(body);
	}



	internal void Register(WaterExclusionVolume volume)
	{
		if (!ExclusionVolumes.Contains(volume))
			ExclusionVolumes.Add(volume);
	}

	internal void Unregister(WaterExclusionVolume volume)
	{
		ExclusionVolumes.Remove(volume);
	}



	internal void Register(HullWaterExclusionVolume hull)
	{
		if (!HullExclusionVolumes.Contains(hull))
			HullExclusionVolumes.Add(hull);
	}

	internal void Unregister(HullWaterExclusionVolume hull)
	{
		HullExclusionVolumes.Remove(hull);
	}



	private WaterDefinition GetWaveProfileForType(WaterBodyType waterType) => waterType switch
	{
		WaterBodyType.Ocean => OceanWaveProfile,
		WaterBodyType.Lake => LakeWaveProfile,
		WaterBodyType.River => RiverWaveProfile,
		WaterBodyType.Pool => PoolWaveProfile,
		_ => CustomWaveProfile
	};

	public static WaterDefinition GetWaveProfile(WaterBodyType _WaterType)
	{
		if (Current == null)
			return null;

		WaterDefinition profile = Current.GetWaveProfileForType(_WaterType);

		if (profile.IsValid())
			return profile;

		Log.Warning("[WaterTool] No water profile found in the 'Water Manager', please add a water profile for the specified water type ! (Project Settings > Water Manager > 'Assign the profiles')");

		return Current.m_DefaultProfile;
	}



	private void RenderAll(SceneObject _)
	{
		if (Graphics.LayerType != SceneLayerType.Translucent)
			return;

		var fbCopy = Graphics.GrabFrameTexture();

		m_CommandList.Reset();

		bool hasAnythingToRender = false;

		foreach (var renderer in QuadRenderers)
		{
			if (!renderer.IsValid() || !renderer.ParticipatesInRendering)
				continue;

			hasAnythingToRender = true;
			renderer.CacheCommandList(m_CommandList);
			renderer.DispatchCompute(m_ComputeShader, m_CameraPosition);
		}

		foreach (var quad in Quads)
		{
			if (!quad.IsValid() || !quad.ParticipatesInRendering)
				continue;

			hasAnythingToRender = true;
			quad.CacheCommandList(m_CommandList);
			quad.DispatchCompute(m_ComputeShader, m_CameraPosition);
		}

		if (!hasAnythingToRender)
			return;

		foreach (var renderer in QuadRenderers)
		{
			if (!renderer.IsValid() || !renderer.ParticipatesInRendering)
				continue;

			renderer.BarrierTransition();
		}

		foreach (var quad in Quads)
		{
			if (!quad.IsValid() || !quad.ParticipatesInRendering)
				continue;

			quad.BarrierTransition();
		}

		foreach (var renderer in QuadRenderers)
		{
			if (!renderer.IsValid() || !renderer.ParticipatesInRendering)
				continue;

			renderer.Draw(fbCopy.ColorTarget);
		}

		foreach (var quad in Quads)
		{
			if (!quad.IsValid() || !quad.ParticipatesInRendering)
				continue;

			quad.Draw(fbCopy.ColorTarget);
		}
	}
}
