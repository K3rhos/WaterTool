using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.WaterTool;

public partial class WaterManager
{
	// Calm volumes are few (river/ocean junctions) and apply to every water surface,
	// so — like ripples — they live in one shared buffer the manager updates once a
	// frame, rather than the per-component distance-sorted exclusion-volume pattern.

	private const int MAX_CALM_VOLUMES = 64;
	private const int CALM_VOLUME_ROWS = 4;

	public List<WaterCalmVolume> CalmVolumes { get; } = [];

	private GpuBuffer<Vector4> m_CalmVolumeBuffer;
	private readonly Vector4[] m_CalmVolumeData = new Vector4[MAX_CALM_VOLUMES * CALM_VOLUME_ROWS];
	private int m_ActiveCalmCount;



	internal void Register(WaterCalmVolume volume)
	{
		if (!CalmVolumes.Contains(volume))
			CalmVolumes.Add(volume);
	}

	internal void Unregister(WaterCalmVolume volume)
	{
		CalmVolumes.Remove(volume);
	}



	private void UpdateCalmVolumes()
	{
		int count = 0;

		foreach (var volume in CalmVolumes)
		{
			if (!volume.IsValid() || !volume.Active)
				continue;

			if (count >= MAX_CALM_VOLUMES)
				break;

			var (center, forward, up, half) = volume.GetWorldOBB();

			int row = count * CALM_VOLUME_ROWS;
			m_CalmVolumeData[row + 0] = new Vector4(forward.x, forward.y, forward.z, half.x);
			m_CalmVolumeData[row + 1] = new Vector4(up.x, up.y, up.z, half.y);
			m_CalmVolumeData[row + 2] = new Vector4(center.x, center.y, center.z, half.z);
			m_CalmVolumeData[row + 3] = new Vector4(volume.Falloff, volume.Strength, 0.0f, 0.0f);

			count++;
		}

		m_ActiveCalmCount = count;

		EnsureCalmBuffer();

		m_CalmVolumeBuffer.SetData(m_CalmVolumeData.AsSpan(0, count * CALM_VOLUME_ROWS));
	}

	private void EnsureCalmBuffer()
	{
		if (!m_CalmVolumeBuffer.IsValid())
			m_CalmVolumeBuffer = new GpuBuffer<Vector4>(MAX_CALM_VOLUMES * CALM_VOLUME_ROWS, GpuBuffer.UsageFlags.Structured);
	}

	internal void ApplyCalmAttributes(RenderAttributes _Attributes)
	{
		_Attributes.Set("WaterCalmVolumeCount", m_ActiveCalmCount);

		if (m_CalmVolumeBuffer.IsValid())
			_Attributes.Set("WaterCalmVolumeData", m_CalmVolumeBuffer);
	}



	/// <summary>
	/// CPU evaluation of the calm factor at a world position (0 = full waves, 1 = flat).
	/// MUST mirror ComputeWaterCalm() in water_calm_volume.fxc so physics (buoyancy,
	/// height queries) matches the flattened visual surface.
	/// </summary>
	public float ComputeCalm(Vector3 _WorldPosition)
	{
		if (CalmVolumes.Count == 0)
			return 0.0f;

		float calm = 0.0f;

		foreach (var volume in CalmVolumes)
		{
			if (!volume.IsValid() || !volume.Active)
				continue;

			var (center, forward, up, half) = volume.GetWorldOBB();

			Vector3 right = Vector3.Cross(up, forward);
			Vector3 d = _WorldPosition - center;

			float nx = MathF.Abs(Vector3.Dot(d, forward)) / MathF.Max(half.x, 0.001f);
			float ny = MathF.Abs(Vector3.Dot(d, right))   / MathF.Max(half.y, 0.001f);
			float nz = MathF.Abs(Vector3.Dot(d, up))      / MathF.Max(half.z, 0.001f);

			float nmax = MathF.Max(nx, MathF.Max(ny, nz));

			float falloffStart = Math.Clamp(1.0f - volume.Falloff, 0.0f, 1.0f);
			float volumeCalm = (1.0f - SmoothStep(falloffStart, 1.0f, nmax)) * volume.Strength;

			calm = MathF.Max(calm, volumeCalm);
		}

		return Math.Clamp(calm, 0.0f, 1.0f);
	}

	// Matches HLSL smoothstep().
	private static float SmoothStep(float _Edge0, float _Edge1, float _X)
	{
		float t = Math.Clamp((_X - _Edge0) / MathF.Max(_Edge1 - _Edge0, 1e-6f), 0.0f, 1.0f);
		return t * t * (3.0f - 2.0f * t);
	}

	private void DisposeCalmVolumes()
	{
		m_CalmVolumeBuffer?.Dispose();
		m_CalmVolumeBuffer = null;
	}
}
