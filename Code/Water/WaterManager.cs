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

	private CommandList m_CommandList = new("Water Rendering");

	private CameraComponent m_LastCamera;
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
		
		UpdateCommandListRegistration();

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

		// Unregister from the camera we actually registered with. Scene.Camera can have changed
		// (or gone) since then, so asking for it again would leave the list attached to a camera
		// we never clean up.
		if (m_LastCamera.IsValid())
			m_LastCamera.RemoveCommandList(m_CommandList);

		m_LastCamera = null;
	}



	/// <summary>
	/// Keeps the compute command list attached to a camera that will actually replay it. This has
	/// to run every frame, not just on enable: a scene starting without a camera would never
	/// register at all, and leaving play mode destroys the play camera without the reference here
	/// turning null, so comparing references alone would leave us bound to a dead camera forever.
	/// </summary>
	private void UpdateCommandListRegistration()
	{
		var renderCamera = GetRenderCamera();

		if (renderCamera == m_LastCamera && m_LastCamera.IsValid())
			return;

		if (m_LastCamera.IsValid())
			m_LastCamera.RemoveCommandList(m_CommandList);

		m_LastCamera = null;

		if (renderCamera.IsValid())
		{
			renderCamera.AddCommandList(m_CommandList, RenderStage.AfterTransparent);
			m_LastCamera = renderCamera;
		}
	}



	/// <summary>
	/// The camera whose command list actually replays. A scene camera does so in the editor
	/// viewport as well as in game, so it wins when one exists; with no camera in the scene the
	/// editor camera is the only thing left that will replay ours.
	/// </summary>
	private CameraComponent GetRenderCamera()
	{
		if (Scene.Camera.IsValid())
			return Scene.Camera;

		if (Scene.IsEditor)
			return Application.Editor?.Camera;

		return null;
	}



	/// <summary>
	/// World position the water should treat as the viewer, for anything that culls or picks
	/// volumes by distance. While editing that has to be the viewport camera rather than the scene
	/// camera, or volumes are gathered around wherever the game camera happens to be parked and the
	/// water you are actually looking at gets the wrong set. Falls back when no camera exists at
	/// all, which is a real case - Scene.Camera excludes the editor camera and can be null.
	/// </summary>
	public static Vector3 GetViewPosition(Scene scene, Vector3 fallback = default)
	{
		if (!scene.IsValid())
			return fallback;

		if (scene.IsEditor)
		{
			var editorCamera = Application.Editor?.Camera;
			if (editorCamera.IsValid())
				return editorCamera.WorldPosition;
		}

		return scene.Camera.IsValid() ? scene.Camera.WorldPosition : fallback;
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

		UpdateCommandListRegistration();

		if (Game.IsPlaying)
		{
			m_CameraPosition = Scene.Camera?.WorldPosition ?? Vector3.Zero;
		}
		else
		{
			// Guarded: with no camera in the scene at all this used to dereference straight through
			// Application.Editor.Camera and throw.
			var editorCamera = Application.Editor?.Camera;
			m_CameraPosition = editorCamera.IsValid() ? editorCamera.WorldPosition : Vector3.Zero;
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
