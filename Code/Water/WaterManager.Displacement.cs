using System;
using Sandbox;

namespace RedSnail.WaterTool;

public partial class WaterManager
{
	// --- Hull test ---

	private static bool IsInsideHull(HullCollider hull, Vector3 position)
	{
		if (!hull.IsValid())
			return false;

		var local = hull.WorldTransform.PointToLocal(position);

		if (hull.Type == HullCollider.PrimitiveType.Box)
		{
			var half = hull.BoxSize * 0.5f;
			return float.Abs(local.x) <= half.x &&
				   float.Abs(local.y) <= half.y &&
				   float.Abs(local.z - hull.Center.z) <= half.z;
		}

		if (hull.Type == HullCollider.PrimitiveType.Cylinder)
		{
			Vector2 flat = new(local.x, local.y);
			return flat.LengthSquared <= hull.Radius * hull.Radius &&
				   float.Abs(local.z - hull.Center.z) <= hull.Height * 0.5f;
		}

		return false;
	}

	// --- Nearest-source lookup ---

	private static WaterQuad FindQuadAtPosition(Vector3 position)
	{
		WaterQuad best = null;
		float bestDist = float.MaxValue;

		foreach (WaterQuad quad in Current?.Quads ?? [])
		{
			if (!quad.HullCollider.IsValid())
				continue;
			
			var local = quad.HullCollider.WorldTransform.PointToLocal(position);
			bool insideXY;
			float floorLocalZ;

			if (quad.HullCollider.Type == HullCollider.PrimitiveType.Cylinder)
			{
				Vector2 flat = new(local.x, local.y);
				insideXY = flat.LengthSquared <= quad.HullCollider.Radius * quad.HullCollider.Radius;
				floorLocalZ = quad.HullCollider.Center.z - quad.HullCollider.Height * 0.5f;
			}
			else
			{
				Vector3 half = quad.HullCollider.BoxSize * 0.5f;
				insideXY = MathF.Abs(local.x) <= half.x && MathF.Abs(local.y) <= half.y;
				floorLocalZ = quad.HullCollider.Center.z - half.z;
			}

			if (!insideXY)
				continue;

			// Water has a finite depth: ignore the quad once we're beneath its floor so the
			// column isn't treated as bottomless (otherwise swim/buoyancy stay "submerged" forever).
			if (local.z < floorLocalZ)
				continue;

			float dist = MathF.Abs(position.z - quad.WorldPosition.z);
			if (dist < bestDist) { bestDist = dist; best = quad; }
		}

		return best;
	}

	private static WaterBody FindBodyAtPosition(Vector3 position)
	{
		WaterBody best = null;
		float bestDist = float.MaxValue;

		foreach (WaterBody body in Current?.Bodies ?? [])
		{
			if (!body.IsValid() || !body.Active || !body.ContainsPointXY(position))
				continue;

			// Finite depth: skip bodies whose floor is above us (we're below the volume).
			if (position.z < body.GetBottomHeight())
				continue;

			float dist = body.GetVerticalDistanceToSurface(position);
			if (dist < bestDist) { bestDist = dist; best = body; }
		}

		return best;
	}

	// Rivers are the most specific water volume, so when a position falls inside a
	// flow's footprint the flow provides the surface — quads/bodies are the fallback.
	private static WaterFlow FindFlowAtPosition(Vector3 position, out float surfaceHeight, out Vector2 flowDirection, out float flowSpeed)
	{
		surfaceHeight = float.MinValue;
		flowDirection = Vector2.Zero;
		flowSpeed = 0.0f;

		foreach (WaterFlow flow in Current?.Flows ?? [])
		{
			if (!flow.IsValid() || !flow.Active)
				continue;

			if (!flow.TryGetSurfaceAt(position, out float height, out Vector2 dir, out float speed))
				continue;

			// Finite depth: ignore the river once we're beneath its bed.
			if (position.z < height - flow.Depth)
				continue;

			surfaceHeight = height;
			flowDirection = dir;
			flowSpeed = speed;

			return flow;
		}

		return null;
	}

	// --- Public static wave queries ---

	public static bool IsPositionInsideAny(Vector3 position)
	{
		if (Current is null)
			return false;

		foreach (WaterQuad quad in Current.Quads)
		{
			if (!quad.IsValid() || !quad.Active)
				continue;

			if (!IsInsideHull(quad.HullCollider, position))
				continue;

			if (position.z <= quad.GetWaveHeightAt(position))
				return true;
		}

		foreach (WaterBody body in Current.Bodies)
		{
			if (!body.IsValid() || !body.Active)
				continue;

			if (!body.ContainsPointInVolume(position))
				continue;

			if (position.z <= body.GetWaveHeightAt(position))
				return true;
		}

		foreach (WaterFlow flow in Current.Flows)
		{
			if (!flow.IsValid() || !flow.Active)
				continue;

			if (flow.ContainsPoint(position))
				return true;
		}

		return false;
	}

	/// <summary>
	/// World-space river current at a position (direction * speed, horizontal).
	/// Zero when the position is not inside any WaterFlow. Buoyancy uses this to
	/// drag floating bodies downstream.
	/// </summary>
	public static Vector3 GetFlowVelocityAt(Vector3 position)
	{
		WaterFlow flowSource = FindFlowAtPosition(position, out _, out Vector2 flowDir, out float flowSpeed);

		return flowSource.IsValid()
			? new Vector3(flowDir.x, flowDir.y, 0.0f) * flowSpeed
			: Vector3.Zero;
	}

	public static Vector3 GetWaveDisplacementAt(Vector3 position)
	{
		// Rivers: no wave orbital displacement — the current is a velocity, applied
		// through Buoyancy's current drag (GetFlowVelocityAt), not wave transport.
		WaterFlow flowSource = FindFlowAtPosition(position, out _, out _, out _);

		if (flowSource.IsValid())
			return new Vector3(0.0f, 0.0f, Current?.ComputeRippleHeight(position) ?? 0.0f);

		WaterQuad quad = FindQuadAtPosition(position);
		WaterBody body = FindBodyAtPosition(position);

		Vector3 displacement;

		if (quad.IsValid() && body.IsValid())
			displacement = quad.GetWaveHeightAt(position) >= body.GetWaveHeightAt(position)
				? quad.GetWaveDisplacementAt(position)
				: body.GetWaveDisplacementAt(position);
		else if (quad.IsValid()) displacement = quad.GetWaveDisplacementAt(position);
		else if (body.IsValid()) displacement = body.GetWaveDisplacementAt(position);
		else return Vector3.Zero;

		// Calm volumes damp the ambient wave displacement, matching the shader
		displacement *= 1.0f - (Current?.ComputeCalm(position) ?? 0.0f);

		// Interactive ripples add purely vertical displacement on top — NOT calmed
		displacement.z += Current?.ComputeRippleHeight((Vector2)position) ?? 0.0f;

		return displacement;
	}

	public static Vector3 GetWaveVelocityAt(Vector3 position)
	{
		// Rivers: the water itself moves — report the current as the wave velocity.
		WaterFlow flowSource = FindFlowAtPosition(position, out _, out Vector2 flowDir, out float flowSpeed);

		if (flowSource.IsValid())
			return new Vector3(flowDir.x, flowDir.y, 0.0f) * flowSpeed;

		WaterQuad quad = FindQuadAtPosition(position);
		WaterBody body = FindBodyAtPosition(position);

		Vector3 velocity;

		if (quad.IsValid() && body.IsValid())
			velocity = quad.GetWaveHeightAt(position) >= body.GetWaveHeightAt(position)
				? quad.GetWaveVelocityAt(position)
				: body.GetWaveVelocityAt(position);
		else if (quad.IsValid()) velocity = quad.GetWaveVelocityAt(position);
		else if (body.IsValid()) velocity = body.GetWaveVelocityAt(position);
		else return Vector3.Zero;

		// Calm volumes damp the wave motion too
		return velocity * (1.0f - (Current?.ComputeCalm(position) ?? 0.0f));
	}

	public static float GetFlatWaterHeightAt(Vector3 position)
	{
		WaterFlow flowSource = FindFlowAtPosition(position, out float flowHeight, out _, out _);

		if (flowSource.IsValid())
			return flowHeight;

		WaterQuad quad = FindQuadAtPosition(position);
		WaterBody body = FindBodyAtPosition(position);

		if (quad.IsValid() && body.IsValid())
			return MathF.Max(quad.WorldPosition.z, body.GetSurfaceHeight());

		if (quad.IsValid()) return quad.WorldPosition.z;
		if (body.IsValid()) return body.GetSurfaceHeight();
		return float.MinValue;
	}

	public static float GetWaterHeightAt(Vector3 position)
	{
		float height;
		float flat;   // the flat (no-wave) surface for this source — calm target

		WaterFlow flowSource = FindFlowAtPosition(position, out float flowHeight, out _, out _);

		if (flowSource.IsValid())
		{
			// River surface: level across the channel, follows the spline downstream.
			// (The shader's travelling waves are small detail — not mirrored on the CPU.)
			// Already flat on the CPU, so a calm volume is a no-op here.
			height = flowHeight;
			flat = flowHeight;
		}
		else
		{
			WaterQuad quad = FindQuadAtPosition(position);
			WaterBody body = FindBodyAtPosition(position);

			if (quad.IsValid() && body.IsValid())
			{
				float qh = quad.GetWaveHeightAt(position);
				float bh = body.GetWaveHeightAt(position);
				if (qh >= bh) { height = qh; flat = quad.WorldPosition.z; }
				else { height = bh; flat = body.GetSurfaceHeight(); }
			}
			else if (quad.IsValid()) { height = quad.GetWaveHeightAt(position); flat = quad.WorldPosition.z; }
			else if (body.IsValid()) { height = body.GetWaveHeightAt(position); flat = body.GetSurfaceHeight(); }
			else return float.MinValue;
		}

		// Calm volumes settle the ambient waves toward the flat surface, matching the
		// shader, so buoyancy sits on the same level the player sees.
		float calm = Current?.ComputeCalm(position) ?? 0.0f;
		if (calm > 0.0f)
			height = float.Lerp(height, flat, calm);

		// Interactive ripples raise/lower the effective surface on top — NOT calmed
		return height + (Current?.ComputeRippleHeight((Vector2)position) ?? 0.0f);
	}
}
