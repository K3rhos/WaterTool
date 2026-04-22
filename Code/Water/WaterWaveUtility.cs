using System;
using Sandbox;

namespace RedSnail.WaterTool;

public enum WaterBodyType
{
	Ocean,
	Lake,
	River,
	Pool,
	Custom
}

public static class WaterWaveUtility
{
	public static Vector3 ComputeDisplacementAt(Vector2 worldXY, WaterDefinition profile)
	{
		Vector3 detail = ComputeGerstner(worldXY, profile.WavesScale, profile.WavesSpeed, profile.WavesDirection, profile.WavesOctaves, profile.WavesLacunarity, profile.WavesPersistence, profile.WavesSteepness) * profile.WavesIntensity;
		Vector3 swell = ComputeGerstner(worldXY, profile.SwellScale, profile.SwellSpeed, profile.SwellDirection, profile.SwellOctaves, profile.SwellLacunarity, profile.SwellPersistence, profile.SwellSteepness) * profile.SwellIntensity;
		return detail + swell;
	}

	public static Vector3 ComputeVelocityAt(Vector2 worldXY, WaterDefinition profile)
	{
		Vector3 detail = ComputeGerstnerVelocity(worldXY, profile.WavesScale, profile.WavesSpeed, profile.WavesDirection, profile.WavesOctaves, profile.WavesLacunarity, profile.WavesPersistence, profile.WavesSteepness) * profile.WavesIntensity;
		Vector3 swell = ComputeGerstnerVelocity(worldXY, profile.SwellScale, profile.SwellSpeed, profile.SwellDirection, profile.SwellOctaves, profile.SwellLacunarity, profile.SwellPersistence, profile.SwellSteepness) * profile.SwellIntensity;
		return detail + swell;
	}

	private static Vector3 ComputeGerstner(Vector2 worldXY, float scale, float speed, Vector2 direction, int octaves, float lacunarity, float persistence, float steepness)
	{
		if (scale <= 0.0f || speed <= 0.0f || octaves <= 0)
			return Vector3.Zero;

		Vector2 waveDirection = direction.Normal;
		float t = Time.Now * speed;

		Vector3 displacement = Vector3.Zero;
		float amp = 1.0f;
		float freq = scale;
		float maxAmp = 0f;

		for (int oct = 0; oct < octaves; oct++)
		{
			float angle = oct * 1.2f;
			Vector2 octDir = new(
				waveDirection.x * MathF.Cos(angle) - waveDirection.y * MathF.Sin(angle),
				waveDirection.x * MathF.Sin(angle) + waveDirection.y * MathF.Cos(angle)
			);

			float phase = freq * (octDir.x * worldXY.x + octDir.y * worldXY.y) + t * freq * 0.5f;
			displacement.x += steepness * amp * octDir.x * MathF.Cos(phase);
			displacement.y += steepness * amp * octDir.y * MathF.Cos(phase);
			displacement.z += amp * MathF.Sin(phase);

			maxAmp += amp;
			amp *= persistence;
			freq *= lacunarity;
		}

		return maxAmp > 0.0f ? displacement / maxAmp : Vector3.Zero;
	}

	private static Vector3 ComputeGerstnerVelocity(Vector2 worldXY, float scale, float speed, Vector2 direction, int octaves, float lacunarity, float persistence, float steepness)
	{
		if (scale <= 0.0f || speed <= 0.0f || octaves <= 0)
			return Vector3.Zero;

		Vector2 waveDirection = direction.Normal;
		float t = Time.Now * speed;

		Vector3 velocity = Vector3.Zero;
		float amp = 1.0f;
		float freq = scale;
		float maxAmp = 0f;

		for (int oct = 0; oct < octaves; oct++)
		{
			float angle = oct * 1.2f;
			Vector2 octDir = new(
				waveDirection.x * MathF.Cos(angle) - waveDirection.y * MathF.Sin(angle),
				waveDirection.x * MathF.Sin(angle) + waveDirection.y * MathF.Cos(angle)
			);

			float phase = freq * (octDir.x * worldXY.x + octDir.y * worldXY.y) + t * freq * 0.5f;
			float angularVelocity = freq * speed * 0.5f;

			velocity.x -= steepness * amp * octDir.x * angularVelocity * MathF.Sin(phase);
			velocity.y -= steepness * amp * octDir.y * angularVelocity * MathF.Sin(phase);
			velocity.z += amp * angularVelocity * MathF.Cos(phase);

			maxAmp += amp;
			amp *= persistence;
			freq *= lacunarity;
		}

		return maxAmp > 0.0f ? velocity / maxAmp : Vector3.Zero;
	}
}
