using Sandbox;

namespace RedSnail.WaterTool;

/// <summary>
/// Emits water ripples when this object crosses the water surface, and optionally
/// while it moves across it. A generic, dependency-free alternative to the entry
/// ripple built into <see cref="Buoyancy"/> — drop it on anything that doesn't have
/// a Buoyancy component (players, NPCs, projectiles, debris...).
///
/// Velocity is derived from the object's own position delta, so it works with any
/// movement system (CharacterController, custom controllers, animation, etc.) and
/// needs no Rigidbody.
/// </summary>
[Icon("water"), Group("Water"), Title("Water Ripple Emitter")]
public sealed class WaterRippleEmitter : Component
{
	[Property, Group("Entry")] public bool EmitOnEntry { get; set; } = true;
	[Property, Group("Entry")] public float EntryStrength { get; set; } = 0.2f;
	// Ring spacing for the entry splash — smaller = tighter, more concentric rings.
	[Property, Group("Entry"), Range(20.0f, 400.0f)] public float EntryWavelength { get; set; } = 120.0f;
	// Ring size for the entry splash — larger = a bigger, broader ripple.
	[Property, Group("Entry"), Range(10.0f, 500.0f)] public float EntryRingWidth { get; set; } = 50.0f;
	// Minimum downward speed (units/s) needed to splash. Set to 0 to ripple on any crossing.
	[Property, Group("Entry")] public float MinImpactSpeed { get; set; } = 40.0f;

	[Property, Group("Wake")] public bool EmitWake { get; set; } = false;
	[Property, Group("Wake")] public float WakeStrength { get; set; } = 0.1f;
	// Ring spacing for wake ripples — smaller = tighter, more concentric rings.
	[Property, Group("Wake"), Range(20.0f, 400.0f)] public float WakeWavelength { get; set; } = 120.0f;
	// Ring size for wake ripples — larger = a bigger, broader ripple.
	[Property, Group("Wake"), Range(10.0f, 500.0f)] public float WakeRingWidth { get; set; } = 50.0f;
	// Minimum horizontal speed (units/s) before a moving object leaves a wake.
	[Property, Group("Wake")] public float WakeMinSpeed { get; set; } = 1.0f;
	[Property, Group("Wake")] public float WakeInterval { get; set; } = 0.0333f; // 30 fps

	// Local-space offset of the point tested against the surface (e.g. the feet).
	[Property, Group("General")] public Vector3 SampleOffset { get; set; } = Vector3.Zero;

	private bool m_Initialized;
	private bool m_WasBelowSurface;
	private Vector3 m_LastPosition;
	private float m_WakeTimer;

	private Vector3 SamplePosition => WorldPosition + WorldRotation * SampleOffset;



	protected override void OnEnabled()
	{
		m_LastPosition = SamplePosition;
		m_WasBelowSurface = false;
		m_Initialized = false;
	}



	protected override void OnUpdate()
	{
		// If this gameobject is parented to anything, we don't want to play water ripple effects
		// (e.g. A player inside a boat)
		if (GameObject.Parent != Scene)
			return;
		
		Vector3 samplePos = SamplePosition;

		// Velocity from position delta — no Rigidbody required
		Vector3 velocity = Time.Delta > 0.0f ? (samplePos - m_LastPosition) / Time.Delta : Vector3.Zero;
		m_LastPosition = samplePos;

		float waterHeight = WaterManager.GetWaterHeightAt(samplePos);

		// Not over any water surface
		if (waterHeight <= float.MinValue)
		{
			m_WasBelowSurface = false;
			return;
		}

		bool belowSurface = samplePos.z <= waterHeight;

		// Skip the first valid frame so an object spawned already in water doesn't splash
		if (!m_Initialized)
		{
			m_WasBelowSurface = belowSurface;
			m_Initialized = true;
			return;
		}

		// Entry splash on the above -> below surface crossing
		if (EmitOnEntry && belowSurface && !m_WasBelowSurface)
		{
			float impactSpeed = float.Max(0.0f, -velocity.z);

			if (impactSpeed >= MinImpactSpeed)
			{
				float strength = (impactSpeed / 150.0f).Clamp(0.3f, 2.5f) * EntryStrength;
				
				WaterManager.AddRipple(samplePos.WithZ(waterHeight), strength, EntryWavelength, EntryRingWidth);
			}
		}

		m_WasBelowSurface = belowSurface;

		float horizontalSpeed = velocity.WithZ(0.0f).Length;
		
		// Continuous wake while skimming/swimming through the surface
		if (EmitWake && belowSurface)
		{
			if (horizontalSpeed >= WakeMinSpeed)
			{
				m_WakeTimer -= Time.Delta;

				if (m_WakeTimer <= 0.0f)
				{
					WaterManager.AddRipple(samplePos.WithZ(waterHeight), WakeStrength, WakeWavelength, WakeRingWidth);
					m_WakeTimer = WakeInterval;
				}
			}
		}
	}
}
