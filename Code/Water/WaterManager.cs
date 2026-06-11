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

	private readonly ComputeShader m_ComputeShader;

	// Double-buffered command lists. We BUILD into the disabled "back" list on the main
	// thread (FinishUpdate); the camera EXECUTES the enabled "front" list on a render
	// worker thread. Because the recorded list and the executing list are never the same
	// instance in a frame, the engine never iterates a list while we're resetting it -
	// which is the multithreaded "CommandList was null" crash. Both stay attached to the
	// camera for its lifetime; each frame we just flip which one is Enabled.
	private CommandList m_FrontCommandList = new("Water Quads (A)") { Enabled = true };
	private CommandList m_BackCommandList = new("Water Quads (B)") { Enabled = false };
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

		m_DefaultProfile = new WaterDefinition();

		Listen(Stage.StartUpdate, 0, Update, "WaterManagerUpdate");

		// Build the render command list at the very end of the main-thread update, after
		// every water component has refreshed its buffers and attributes. Recording here -
		// single-threaded, never inside a render-thread RenderOverride - is what stops
		// Reset()/record from racing the engine's command-list execution on the GPU thread.
		Listen(Stage.FinishUpdate, 0, BuildCommandList, "WaterManagerBuild");
	}



	public override void Dispose()
	{
		if (m_LastCamera.IsValid())
		{
			m_LastCamera.RemoveCommandList(m_FrontCommandList);
			m_LastCamera.RemoveCommandList(m_BackCommandList);
		}

		m_RippleBuffer?.Dispose();
		m_RippleBuffer = null;

		base.Dispose();
	}



	private void Update()
	{
		var camera = Scene.Camera;
		
		if (camera != m_LastCamera)
		{
			// Move both command lists to the new camera. We only add/remove on a camera
			// change (rare) - never per frame - so we don't fight the worker thread that
			// reads the camera's command-list collection while rendering.
			if (m_LastCamera.IsValid())
			{
				m_LastCamera.RemoveCommandList(m_FrontCommandList);
				m_LastCamera.RemoveCommandList(m_BackCommandList);
			}

			if (camera.IsValid())
			{
				camera.AddCommandList(m_FrontCommandList, RenderStage.AfterTransparent);
				camera.AddCommandList(m_BackCommandList, RenderStage.AfterTransparent);
			}

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

		UpdateRipples();
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



	private void BuildCommandList()
	{
		// Record into the back (disabled) list. The engine only executes the enabled
		// front list, so this instance is guaranteed idle - safe to reset and record
		// from the main thread with no lock and no race against the render thread.
		var cl = m_BackCommandList;

		cl.Reset();

		bool hasAnythingToRender = false;

		foreach (var renderer in QuadRenderers)
		{
			if (!renderer.IsValid() || !renderer.ParticipatesInRendering)
				continue;

			hasAnythingToRender = true;
			renderer.RecordCompute(cl, m_ComputeShader, m_CameraPosition);
		}

		foreach (var quad in Quads)
		{
			if (!quad.IsValid() || !quad.ParticipatesInRendering)
				continue;

			hasAnythingToRender = true;
			quad.RecordCompute(cl, m_ComputeShader, m_CameraPosition);
		}

		if (hasAnythingToRender)
		{
			foreach (var renderer in QuadRenderers)
			{
				if (!renderer.IsValid() || !renderer.ParticipatesInRendering)
					continue;

				renderer.BarrierTransition(cl);
			}

			foreach (var quad in Quads)
			{
				if (!quad.IsValid() || !quad.ParticipatesInRendering)
					continue;

				quad.BarrierTransition(cl);
			}

			cl.Attributes.GrabFrameTexture("FrameBufferCopyTexture");

			foreach (var renderer in QuadRenderers)
			{
				if (!renderer.IsValid() || !renderer.ParticipatesInRendering)
					continue;

				renderer.Draw(cl);
			}

			foreach (var quad in Quads)
			{
				if (!quad.IsValid() || !quad.ParticipatesInRendering)
					continue;

				quad.Draw(cl);
			}
		}

		// Publish the freshly-built list and retire the previous one, then swap roles so
		// next frame we record into the one the GPU is no longer touching. Enable the new
		// list before disabling the old, so a worker reading mid-swap sees both (a
		// harmless double draw) rather than neither (a one-frame gap).
		m_BackCommandList.Enabled = true;
		m_FrontCommandList.Enabled = false;

		(m_FrontCommandList, m_BackCommandList) = (m_BackCommandList, m_FrontCommandList);
	}
}
