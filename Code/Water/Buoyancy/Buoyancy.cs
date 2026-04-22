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
	[Property, Group("Wave Transport")] private float WaveTransportForce { get; set; } = 5.0f;

	[Property] private float AirLeakRate { get; set; } = 0.0f;

	[Sync] public float AirVolume { get; private set; } = 1.0f;
	[Sync] public float WaterHeight { get; private set; } = float.MinValue;
	[Sync] public bool IsTouchingWater { get; private set; }

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

			float colliderHeight = m_Collider.LocalBounds.Size.z;
			bool isNearWater = WorldPosition.z <= WaterHeight + colliderHeight;

			if (isNearWater)
			{
				ApplyWaterResistance();
				ApplyAngularDrag();
				ApplyBuoyancy();
				ApplyWaveTransport();
			}
		}
		else
		{
			IsTouchingWater = false;
			WaterHeight = float.MinValue;
		}

		// Always run, drains while submerged, recovers while above water or fully out
		UpdateAirVolume();
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

		m_Collider.Rigidbody.ApplyForce(horizontalDisp * WaveTransportForce);
	}
}
