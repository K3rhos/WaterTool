using Sandbox;

namespace RedSnail.WaterTool;

public sealed class Buoyancy : Component
{
	private Collider m_Collider;

	private const float WATER_DENSITY = 1000.0f; // kg/m3

	[Property, Group("Buoyancy")] private float SpringStiffness { get; set; } = 500.0f;
	[Property, Group("Buoyancy")] private float Damping { get; set; } = 5.0f;
	[Property, Group("Buoyancy"), Range(0.1f, 1.0f)] private float HullSpread { get; set; } = 0.6f;
	[Property, Group("Buoyancy")] private float SurfaceOffset { get; set; } = 0.0f;

	[Property, Group("Drag")] private float DragCoefficient { get; set; } = 1.0f;
	[Property, Group("Drag")] private float AngularDragCoefficient { get; set; } = 2.0f;

	// Set to 0 for docked/anchored boats that should only bob vertically
	[Property, Group("Wave Transport"), Range(0f, 1f)] private float HorizontalDisplacementStrength { get; set; } = 1.0f;
	[Property, Group("Wave Transport"), Range(0.1f, 10.0f)] private float Drag { get; set; } = 1.5f;

	[Property] private float AirLeakRate { get; set; } = 0.0f;

	[Property, Group("Ripples")] private bool EmitEntryRipple { get; set; } = true;
	// Ring spacing for the entry splash — smaller = tighter, more concentric rings.
	[Property, Group("Ripples"), Range(20.0f, 400.0f)] private float EntryRippleWavelength { get; set; } = 120.0f;
	// Ring size for the entry splash — larger = a bigger, broader ripple.
	[Property, Group("Ripples"), Range(10.0f, 500.0f)] private float EntryRippleWidth { get; set; } = 50.0f;

	// Continuous wake ripples while the hull moves across the surface (great for boats).
	[Property, Group("Ripples")] private bool EmitWakeRipple { get; set; } = true;
	[Property, Group("Ripples")] private float WakeRippleStrength { get; set; } = 0.1f;
	// Minimum horizontal speed (units/s) before a wake ripple is emitted.
	[Property, Group("Ripples")] private float WakeRippleMinSpeed { get; set; } = 20.0f;
	// Seconds between wake ripples — lower = denser trail (uses more of the global ripple budget).
	[Property, Group("Ripples")] private float WakeRippleInterval { get; set; } = 0.0333f;
	// Ring spacing for wake ripples — smaller = tighter, more concentric rings.
	[Property, Group("Ripples"), Range(20.0f, 400.0f)] private float WakeRippleWavelength { get; set; } = 120.0f;
	// Ring size for wake ripples — larger = a bigger, broader ripple.
	[Property, Group("Ripples"), Range(10.0f, 500.0f)] private float WakeRippleWidth { get; set; } = 50.0f;

	[Sync] public float AirVolume { get; private set; } = 1.0f;
	[Sync] public float WaterHeight { get; private set; } = float.MinValue;
	[Sync] public bool IsTouchingWater { get; private set; }

	private bool m_WasBelowSurface;
	private float m_WakeTimer;

	public bool IsUnderwater => IsTouchingWater && WorldPosition.z <= WaterHeight;



	protected override void OnAwake()
	{
		m_Collider = GetComponent<Collider>();
	}



	protected override void OnFixedUpdate()
	{
		if (IsProxy)
			return;

		if (m_Collider.IsTrigger)
			return;

		if (!m_Collider.Rigidbody.IsValid())
			return;

		float waveHeight = WaterManager.GetWaterHeightAt(WorldPosition);
		bool insideWater = waveHeight > float.MinValue;

		if (insideWater)
		{
			WaterHeight = waveHeight;
			IsTouchingWater = true;

			HandleEntryRipple();

			float colliderHeight = m_Collider.LocalBounds.Size.z;
			bool isNearWater = WorldPosition.z <= WaterHeight + colliderHeight;

			if (isNearWater)
			{
				ApplyWaterResistance();
				ApplyAngularDrag();
				ApplyBuoyancy();
				ApplyWaveTransport();
				ApplyCurrentForce();

				HandleWakeRipple();
			}
		}
		else
		{
			IsTouchingWater = false;
			WaterHeight = float.MinValue;
			m_WasBelowSurface = false;
		}

		// Always run, drains while submerged, recovers while above water or fully out
		UpdateAirVolume();
	}



	private void HandleEntryRipple()
	{
		// Detect the moment the object crosses below the surface and emit a splash
		// ripple. A minimum impact speed gate keeps a gently bobbing hull from
		// spamming ripples every time it dips through the surface line.
		bool belowSurface = WorldPosition.z <= WaterHeight;

		if (EmitEntryRipple && belowSurface && !m_WasBelowSurface)
		{
			float impactSpeed = float.Max(0.0f, -m_Collider.Rigidbody.Velocity.z);

			if (impactSpeed > 40.0f)
			{
				float strength = (impactSpeed / 150.0f).Clamp(0.3f, 2.5f);
				WaterManager.AddRipple(WorldPosition.WithZ(WaterHeight), strength, EntryRippleWavelength, EntryRippleWidth);
			}
		}

		m_WasBelowSurface = belowSurface;
	}



	private void HandleWakeRipple()
	{
		if (!EmitWakeRipple)
			return;

		// Emit a steady trail of ripples while the hull moves across the surface.
		// The min-speed gate stops a near-stationary hull bobbing in the waves from
		// dribbling out ripples; the timer spaces them along the path of travel.
		float horizontalSpeed = m_Collider.Rigidbody.Velocity.WithZ(0.0f).Length;

		if (horizontalSpeed < WakeRippleMinSpeed)
			return;

		m_WakeTimer -= Time.Delta;

		if (m_WakeTimer > 0.0f)
			return;
		
		float strength = (horizontalSpeed / 1000.0f).Clamp(0.1f, 1.0f);
		
		WaterManager.AddRipple(WorldPosition.WithZ(WaterHeight), strength, WakeRippleWavelength, m_Collider.LocalBounds.Extents.x);
		
		m_WakeTimer = WakeRippleInterval;
	}



	private float GetSubmersionAtPoint(Vector3 _WorldPoint, float _WaterHeight)
	{
		float depth = _WaterHeight - _WorldPoint.z;

		// Get the height of the collider for normalization
		BBox localBounds = m_Collider.LocalBounds;
		float colliderHeight = localBounds.Size.z;

		if (colliderHeight <= 0.0f)
			return 0.0f;

		// Return normalized depth (0 = at surface, 1 = fully submerged)
		return (depth / colliderHeight).Clamp(0.0f, 1.0f);
	}



	private void UpdateAirVolume()
	{
		if (WorldPosition.z < WaterHeight)
			AirVolume -= Time.Delta * AirLeakRate;
		else
			AirVolume += Time.Delta * AirLeakRate;

		AirVolume = AirVolume.Clamp(0.0f, 1.0f);
	}



	private void ApplyWaterResistance()
	{
		Vector3 velocity = m_Collider.Rigidbody.Velocity;
		float speed = velocity.Length * 0.0254f; // Convert inches to meters

		if (speed < 0.01f)
			return;

		float submersion = GetSubmersionAtPoint(WorldPosition, WaterHeight);

		// Approximate frontal area (in m²)
		BBox worldBounds = m_Collider.LocalBounds.Transform(WorldTransform);
		float area = (worldBounds.Size.z * worldBounds.Size.x) * 0.00064516f; // Convert inches² to meters²
		Vector3 velocityDir = velocity.Normal;

		// Drag force = -0.5 * ρ * v^2 * C_d * A * dir
		Vector3 dragForce = -0.5f * WATER_DENSITY * speed * speed * DragCoefficient * area * velocityDir * submersion;

		m_Collider.Rigidbody.ApplyForce(dragForce);
	}



	private void ApplyAngularDrag()
	{
		Vector3 angularVelocity = m_Collider.Rigidbody.AngularVelocity;

		if (angularVelocity.LengthSquared < 0.0001f)
			return;

		float submersion = GetSubmersionAtPoint(WorldPosition, WaterHeight);

		Vector3 angularDrag = -angularVelocity * AngularDragCoefficient * submersion;
		m_Collider.Rigidbody.AngularVelocity += angularDrag * Time.Delta;
	}



	private void ApplyBuoyancy()
	{
		BBox localBounds = m_Collider.LocalBounds;
		Vector3 center = localBounds.Center;
		Vector3 extents = localBounds.Extents;

		float sx = extents.x * HullSpread;
		float sy = extents.y * HullSpread;

		Vector3 p0 = center;                                        // Center
		Vector3 p1 = center + new Vector3(sx, 0, 0);               // Starboard
		Vector3 p2 = center + new Vector3(-sx, 0, 0);              // Port
		Vector3 p3 = center + new Vector3(0, sy, 0);               // Bow
		Vector3 p4 = center + new Vector3(0, -sy, 0);              // Stern
		Vector3 p5 = center + new Vector3(sx, sy, 0);              // Bow-Starboard
		Vector3 p6 = center + new Vector3(-sx, sy, 0);             // Bow-Port
		Vector3 p7 = center + new Vector3(sx, -sy, 0);             // Stern-Starboard
		Vector3 p8 = center + new Vector3(-sx, -sy, 0);            // Stern-Port

		const int pointCount = 9;
		float mass = m_Collider.Rigidbody.Mass;
		Vector3 angularVel = m_Collider.Rigidbody.AngularVelocity;

		foreach (Vector3 localPoint in new[] { p0, p1, p2, p3, p4, p5, p6, p7, p8 })
		{
			Vector3 worldPoint = WorldTransform.PointToWorld(localPoint);

			float pointWaterHeight = WaterManager.GetWaterHeightAt(worldPoint);
			if (pointWaterHeight == float.MinValue)
				pointWaterHeight = WaterHeight;

			/*
			Vector3 test = worldPoint;
			test.z = pointWaterHeight;
			
			DebugOverlay.Box(test, Vector3.One, Color.Red, overlay: true);
			*/

			// How far below the wave surface this point is (positive = submerged)
			// SurfaceOffset raises the effective water level so the boat sits higher
			float depth = (pointWaterHeight + SurfaceOffset) - worldPoint.z;

			if (depth <= 0f)
				continue;

			// Spring: force proportional to depth below surface, scaled by remaining air
			float springForce = depth * SpringStiffness * mass * AirVolume / pointCount;

			// Damper: opposes vertical velocity at this point to prevent oscillation
			Vector3 pointVelocity = m_Collider.Rigidbody.Velocity + Vector3.Cross(angularVel, worldPoint - WorldPosition);
			float damperForce = -pointVelocity.z * Damping * mass / pointCount;

			m_Collider.Rigidbody.ApplyForceAt(worldPoint, Vector3.Up * (springForce + damperForce));
		}
	}



	private void ApplyWaveTransport()
	{
		if (HorizontalDisplacementStrength <= 0f)
			return;

		Vector3 displacement = WaterManager.GetWaveDisplacementAt(WorldPosition);
		Vector3 horizontalDisp = new Vector3(displacement.x, displacement.y, 0) * HorizontalDisplacementStrength;

		Vector3 relative = horizontalDisp - m_Collider.Rigidbody.Velocity.WithZ(0.0f);
		Vector3 force = relative * (m_Collider.Rigidbody.Mass * Drag);
		
		m_Collider.Rigidbody.ApplyForce(force);
	}



	private void ApplyCurrentForce()
	{
		if (HorizontalDisplacementStrength <= 0f)
			return;
		
		Vector3 flowVelocity = WaterManager.GetFlowVelocityAt(WorldPosition);

		if (flowVelocity.LengthSquared < 0.01f)
			return;
		
		Vector3 relative = flowVelocity - m_Collider.Rigidbody.Velocity.WithZ(0.0f);
		Vector3 force = relative * HorizontalDisplacementStrength * (m_Collider.Rigidbody.Mass * Drag);
		
		m_Collider.Rigidbody.ApplyForce(force);
	}
}
