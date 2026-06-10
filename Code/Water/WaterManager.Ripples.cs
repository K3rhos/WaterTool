using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.WaterTool;

public partial class WaterManager
{
	// Interactive ripples — expanding radial wave packets stamped onto the surface
	// when something enters or moves on the water. Each emitter is uploaded as two
	// float4 rows: row0 = (Center.xy, StartTime, Strength), row1 = (Wavelength, Width, _, _).
	// Amplitude/Speed/Damping are global; Strength, Wavelength and Width are per-ripple.
	// The exact same formula runs in advancedwater.shader (VS) and in ComputeRippleHeight
	// (CPU) so buoyancy bobs over the visual ripples.

	private const int MAX_RIPPLES = 64;
	private const int RIPPLE_ROWS = 2;

	[Property(Title = "Amplitude"), Group("Ripples")] public float RippleAmplitude { get; set; } = 8.0f;
	[Property(Title = "Expansion Speed"), Group("Ripples")] public float RippleSpeed { get; set; } = 100.0f;
	// Default ring spacing used when a ripple is spawned without an explicit wavelength.
	// Smaller = tighter, more concentric rings. Larger = fewer, broader rings.
	[Property(Title = "Default Wavelength"), Group("Ripples")] public float RippleWavelength { get; set; } = 120.0f;
	// Default ring size used when a ripple is spawned without an explicit width.
	// Larger = bigger, broader ripple (the wave packet spans a wider radial band).
	[Property(Title = "Default Ring Width"), Group("Ripples")] public float RippleWidth { get; set; } = 50.0f;
	[Property(Title = "Damping"), Group("Ripples")] public float RippleDamping { get; set; } = 1.0f;
	[Property(Title = "Lifetime"), Group("Ripples")] public float RippleLifetime { get; set; } = 3.0f;

	private struct RippleEmitter
	{
		public Vector2 Center;
		public float StartTime;
		public float Strength;
		public float Wavelength;
		public float Width;
	}

	private readonly List<RippleEmitter> m_Ripples = [];
	private GpuBuffer<Vector4> m_RippleBuffer;
	private readonly Vector4[] m_RippleData = new Vector4[MAX_RIPPLES * RIPPLE_ROWS];
	private int m_ActiveRippleCount;



	/// <summary>
	/// Spawn an expanding ripple on the water surface at the given world position.
	/// </summary>
	/// <param name="_WorldPosition">Where the ripple originates (only XY is used).</param>
	/// <param name="_Strength">Scales the height of the ripple (1 = a normal splash).</param>
	/// <param name="_Wavelength">Ring spacing — smaller = more rings. Pass &lt;= 0 to use the manager's Default Wavelength.</param>
	/// <param name="_Width">Ring size — larger = a bigger, broader ripple. Pass &lt;= 0 to use the manager's Default Ring Width.</param>
	public static void AddRipple(Vector3 _WorldPosition, float _Strength = 1.0f, float _Wavelength = -1.0f, float _Width = -1.0f)
	{
		Current?.AddRippleInternal(_WorldPosition, _Strength, _Wavelength, _Width);
	}

	private void AddRippleInternal(Vector3 _WorldPosition, float _Strength, float _Wavelength, float _Width)
	{
		if (_Strength <= 0.0f)
			return;

		// Fall back to the global defaults when no per-ripple value is given
		if (_Wavelength <= 0.0f)
			_Wavelength = RippleWavelength;

		if (_Width <= 0.0f)
			_Width = RippleWidth;

		// Drop the oldest when full so the freshest splashes always survive
		if (m_Ripples.Count >= MAX_RIPPLES)
			m_Ripples.RemoveAt(0);

		m_Ripples.Add(new RippleEmitter
		{
			Center = new Vector2(_WorldPosition.x, _WorldPosition.y),
			StartTime = Time.Now,
			Strength = _Strength,
			Wavelength = _Wavelength,
			Width = _Width
		});
	}



	private void UpdateRipples()
	{
		// Prune expired emitters
		for (int i = m_Ripples.Count - 1; i >= 0; i--)
		{
			if (Time.Now - m_Ripples[i].StartTime > RippleLifetime)
				m_Ripples.RemoveAt(i);
		}

		m_ActiveRippleCount = Math.Min(m_Ripples.Count, MAX_RIPPLES);

		for (int i = 0; i < m_ActiveRippleCount; i++)
		{
			var r = m_Ripples[i];
			int row = i * RIPPLE_ROWS;

			m_RippleData[row + 0] = new Vector4(r.Center.x, r.Center.y, r.StartTime, r.Strength);
			m_RippleData[row + 1] = new Vector4(r.Wavelength, r.Width, 0.0f, 0.0f);
		}

		EnsureRippleBuffer();

		m_RippleBuffer.SetData(m_RippleData.AsSpan(0, m_ActiveRippleCount * RIPPLE_ROWS));
	}

	private void EnsureRippleBuffer()
	{
		if (!m_RippleBuffer.IsValid())
			m_RippleBuffer = new GpuBuffer<Vector4>(MAX_RIPPLES * RIPPLE_ROWS, GpuBuffer.UsageFlags.Structured);
	}



	internal void ApplyRippleAttributes(RenderAttributes _Attributes)
	{
		_Attributes.Set("RippleCount", m_ActiveRippleCount);
		_Attributes.Set("RippleAmplitude", RippleAmplitude);
		_Attributes.Set("RippleSpeed", RippleSpeed);
		_Attributes.Set("RippleDamping", RippleDamping);

		if (m_RippleBuffer.IsValid())
			_Attributes.Set("RippleData", m_RippleBuffer);
	}



	/// <summary>
	/// CPU evaluation of the ripple vertical displacement at a world XY position.
	/// MUST mirror ComputeRipples() in advancedwater.shader so physics matches visuals.
	/// </summary>
	public float ComputeRippleHeight(Vector2 _WorldXY)
	{
		if (m_Ripples.Count == 0)
			return 0.0f;

		float z = 0.0f;

		for (int i = 0; i < m_Ripples.Count; i++)
		{
			var r = m_Ripples[i];

			float age = Time.Now - r.StartTime;
			if (age < 0.0f || age > RippleLifetime)
				continue;

			float freq = r.Wavelength > 0.001f ? (MathF.PI * 2.0f / r.Wavelength) : 0.0f;
			float invWidthSq = r.Width > 0.001f ? 1.0f / (r.Width * r.Width) : 0.0f;

			float d = (_WorldXY - r.Center).Length;
			float ring = age * RippleSpeed;
			float ringDelta = d - ring;

			float spatialEnv = MathF.Exp(-ringDelta * ringDelta * invWidthSq);
			float timeEnv = MathF.Exp(-age * RippleDamping);
			float wave = MathF.Sin(ringDelta * freq);

			z += wave * spatialEnv * timeEnv * RippleAmplitude * r.Strength;
		}

		return z;
	}
}
