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

		return false;
	}

	public static Vector3 GetWaveDisplacementAt(Vector3 position)
	{
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

		// Interactive ripples add purely vertical displacement on top of the base waves
		displacement.z += Current?.ComputeRippleHeight((Vector2)position) ?? 0.0f;

		return displacement;
	}

	public static Vector3 GetWaveVelocityAt(Vector3 position)
	{
		WaterQuad quad = FindQuadAtPosition(position);
		WaterBody body = FindBodyAtPosition(position);

		if (quad.IsValid() && body.IsValid())
			return quad.GetWaveHeightAt(position) >= body.GetWaveHeightAt(position)
				? quad.GetWaveVelocityAt(position)
				: body.GetWaveVelocityAt(position);

		if (quad.IsValid()) return quad.GetWaveVelocityAt(position);
		if (body.IsValid()) return body.GetWaveVelocityAt(position);
		return Vector3.Zero;
	}

	public static float GetFlatWaterHeightAt(Vector3 position)
	{
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
		WaterQuad quad = FindQuadAtPosition(position);
		WaterBody body = FindBodyAtPosition(position);

		float height;

		if (quad.IsValid() && body.IsValid())
			height = MathF.Max(quad.GetWaveHeightAt(position), body.GetWaveHeightAt(position));
		else if (quad.IsValid()) height = quad.GetWaveHeightAt(position);
		else if (body.IsValid()) height = body.GetWaveHeightAt(position);
		else return float.MinValue;

		// Interactive ripples raise/lower the effective surface so physics tracks the visual
		return height + (Current?.ComputeRippleHeight((Vector2)position) ?? 0.0f);
	}
}
