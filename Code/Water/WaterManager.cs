using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;
using Sandbox.Rendering;
using RenderStage = Sandbox.Rendering.Stage;

namespace RedSnail.WaterTool;

[Title("Water Manager")]
public partial class WaterManager : Component, Component.ExecuteInEditor, Component.DontExecuteOnServer, IHotloadManaged
{
	private SceneCustomObject m_SceneObject;
	
	[SkipHotload] public static WaterManager Current { get; private set; } = null;
	
	[Property(Title = "Ocean"), Group("Profile"), Order(0)] public WaterDefinition OceanWaveProfile { get; set; }
	[Property(Title = "Lake"), Group("Profile")] public WaterDefinition LakeWaveProfile { get; set; }
	[Property(Title = "River"), Group("Profile")] public WaterDefinition RiverWaveProfile { get; set; }
	[Property(Title = "Pool"), Group("Profile")] public WaterDefinition PoolWaveProfile { get; set; }
	[Property(Title = "Custom"), Group("Profile")] public WaterDefinition CustomWaveProfile { get; set; }

	[Property(Title = "Underwater Volume"), Group("Post Processing")] public PostProcessVolume UnderwaterPostProcessVolume { get; set; }

	private ComputeShader m_ComputeShader;

	// Double-buffered command lists. We BUILD into the disabled "back" list on the main
	// thread (FinishUpdate); the camera EXECUTES the enabled "front" list on a render
	// worker thread. Because the recorded list and the executing list are never the same
	// instance in a frame, the engine never iterates a list while we're resetting it -
	// which is the multithreaded "CommandList was null" crash. Both stay attached to the
	// camera for its lifetime; each frame we just flip which one is Enabled.
	private CommandList m_CommandList = new("Water Quads");
	private Vector3 m_CameraPosition;
	private WaterDefinition m_DefaultProfile;

	private List<WaterQuad> Quads { get; } = [];
	private List<WaterBodyRenderer> QuadRenderers { get; } = [];
	public List<WaterBody> Bodies { get; } = [];
	public List<WaterFlow> Flows { get; } = [];
	public List<WaterExclusionVolume> ExclusionVolumes { get; } = [];
	public List<HullWaterExclusionVolume> HullExclusionVolumes { get; } = [];
	
	
	
	protected override void OnAwake()
	{
		Current = Scene.Get<WaterManager>();
		
		m_ComputeShader = new ComputeShader("water_clipmap_cs");

		m_DefaultProfile = new WaterDefinition();
	}
	
	
	
	protected override void OnEnabled()
	{
		m_SceneObject = new SceneCustomObject(Scene.SceneWorld)
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
		
		Scene.Camera?.AddCommandList(m_CommandList, RenderStage.AfterTransparent);
		
		RefreshWaterQuadsList();
		RefreshWaterBodyRenderersList();
		RefreshWaterBodiesList();
		RefreshWaterExclusionVolumesList();
		RefreshWaterHullExclusionVolumesList();
	}
	
	
	
	protected override void OnDisabled()
	{
		m_SceneObject?.Delete();
		m_SceneObject = null;

		m_RippleBuffer?.Dispose();
		m_RippleBuffer = null;
	
		ClearCalmVolumes();
		
		Scene.Camera?.RemoveCommandList(m_CommandList);
	}
	
	
	
	void IHotloadManaged.Destroyed(Dictionary<string, object> _State)
	{
		_State["IsActive"] = Current == this;
	}



	void IHotloadManaged.Created(IReadOnlyDictionary<string, object> _State)
	{
		if (_State.GetValueOrDefault("IsActive") is true)
			Current = this;
	}
	
	
	
	private void RenderAll(SceneObject _)
	{
		if (Graphics.LayerType != SceneLayerType.Translucent)
			return;
		
		m_CommandList.Reset();

		bool hasAnythingToRender = false;

		foreach (var renderer in QuadRenderers)
		{
			if (!renderer.IsValid() || !renderer.ParticipatesInRendering)
				continue;

			hasAnythingToRender = true;
			renderer.RecordCompute(m_CommandList, m_ComputeShader, m_CameraPosition);
		}
		
		foreach (var quad in Quads)
		{
			if (!quad.IsValid() || !quad.ParticipatesInRendering)
				continue;

			hasAnythingToRender = true;
			quad.RecordCompute(m_CommandList, m_ComputeShader, m_CameraPosition);
		}
		
		// Flows build their mesh on the CPU (no compute pass or barrier needed)
		foreach (var flow in Flows)
		{
			if (!flow.IsValid() || !flow.ParticipatesInRendering)
				continue;

			hasAnythingToRender = true;
		}

		if (hasAnythingToRender)
		{
			foreach (var renderer in QuadRenderers)
			{
				if (!renderer.IsValid() || !renderer.ParticipatesInRendering)
					continue;

				renderer.BarrierTransition(m_CommandList);
			}

			foreach (var quad in Quads)
			{
				if (!quad.IsValid() || !quad.ParticipatesInRendering)
					continue;

				quad.BarrierTransition(m_CommandList);
			}

			m_CommandList.Attributes.GrabFrameTexture("FrameBufferCopyTexture");

			foreach (var renderer in QuadRenderers)
			{
				if (!renderer.IsValid() || !renderer.ParticipatesInRendering)
					continue;

				renderer.Draw(m_CommandList);
			}
			
			foreach (var quad in Quads)
			{
				if (!quad.IsValid() || !quad.ParticipatesInRendering)
					continue;

				quad.Draw(m_CommandList);
			}
			
			foreach (var flow in Flows)
			{
				if (!flow.IsValid() || !flow.ParticipatesInRendering)
					continue;

				flow.Draw(m_CommandList);
			}
		}
	}
	
	
	
	protected override void OnUpdate()
	{
		// We've to make sure it's always correct while in the editor
		// (S&box is a complete mess when it comes to managing a singleton properly on a component that execute in the editor, bcs its reference get constantly swapped between
		// gameplay and editor, we've to do this non sense !)
		if (Scene.IsEditor)
			Current = Scene.Get<WaterManager>();
		
		if (Game.IsPlaying)
		{
			m_CameraPosition = Scene.Camera?.WorldPosition ?? Vector3.Zero;
		}
		else
		{
			m_CameraPosition = Application.Editor.Camera.WorldPosition;
		}

		if (UnderwaterPostProcessVolume.IsValid())
			UnderwaterPostProcessVolume.Enabled = IsPositionInsideAny(m_CameraPosition);

		UpdateRipples();
		UpdateCalmVolumes();
	}

	/// <summary>
	/// We have to do all this non sense bcs using a Register/Unregister logic with OnEnabled/OnDisabled is a complete
	/// mess to manage when we enter play mode/stop play mode in the editor, the references get duplicated etc... Otherwise we've to check by gameobject id...
	/// It's just way too annoying, refreshing the whole list is safer and we're always sure to have the proper count of components
	/// </summary>
	public void RefreshWaterQuadsList()
	{
		if (!Scene.IsValid()) // S&box make this null while stopping play mode and entering back the editor mode (We need to guard this)
			return;
		
		Quads.Clear();
		Quads.AddRange(Scene.GetAll<WaterQuad>());
	}

	public void RefreshWaterBodyRenderersList()
	{
		if (!Scene.IsValid()) // S&box make this null while stopping play mode and entering back the editor mode (We need to guard this)
			return;
		
		QuadRenderers.Clear();
		QuadRenderers.AddRange(Scene.GetAll<WaterBodyRenderer>());
	}

	public void RefreshWaterBodiesList()
	{
		if (!Scene.IsValid()) // S&box make this null while stopping play mode and entering back the editor mode (We need to guard this)
			return;
		
		Bodies.Clear();
		Bodies.AddRange(Scene.GetAll<WaterBody>());
	}
	
	public void RefreshWaterFlowsList()
	{
		if (!Scene.IsValid()) // S&box make this null while stopping play mode and entering back the editor mode (We need to guard this)
			return;
		
		Flows.Clear();
		Flows.AddRange(Scene.GetAll<WaterFlow>());
	}

	public void RefreshWaterExclusionVolumesList()
	{
		if (!Scene.IsValid()) // S&box make this null while stopping play mode and entering back the editor mode (We need to guard this)
			return;
		
		ExclusionVolumes.Clear();
		ExclusionVolumes.AddRange(Scene.GetAll<WaterExclusionVolume>());
	}

	public void RefreshWaterHullExclusionVolumesList()
	{
		if (!Scene.IsValid()) // S&box make this null while stopping play mode and entering back the editor mode (We need to guard this)
			return;
		
		HullExclusionVolumes.Clear();
		HullExclusionVolumes.AddRange(Scene.GetAll<HullWaterExclusionVolume>());
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
}
