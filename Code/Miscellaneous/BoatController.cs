using System;
using Sandbox;
using Sandbox.Movement;

namespace RedSnail.WaterTool;

/// <summary>
/// Minimal demo boat controller.
/// </summary>
[Title( "Demo Boat Controller" ), Group( "Water" ), Icon( "directions_boat" )]
public sealed class BoatController : Component, Component.IPressable, ISitTarget
{
	private TimeSince m_TimeSinceLastUnderWave;
	private float m_LastHitTimer = 1.0f;
	
	[Property, Group( "Seat" )] public GameObject SeatPosition { get; set; }
	[Property, Group( "Seat" )] public GameObject EyePosition  { get; set; }
	[Property, Group( "Seat" )] public GameObject ExitPoint    { get; set; }

	[Property, Group( "Movement" )] public float ThrustForce   { get; set; } = 200_000f;
	[Property, Group( "Movement" )] public float ReverseForce  { get; set; } = 80_000f;
	[Property, Group( "Movement" )] public float TurnForce     { get; set; } = 60_000f;
	[Property, Group( "Movement" )] public float Stability     { get; set; } = 50_000f;
	[Property, Group( "Movement" )] public float TerminalSpeed { get; set; } = 800f;

	[Property, Group( "Interaction" )] public string TooltipTitle { get; set; } = "Drive";
	[Property, Group( "Interaction" )] public string TooltipIcon  { get; set; } = "directions_boat";

	[Property, Group( "Sounds" )] public SoundEvent BoatUnderWaves  { get; set; }
	[Property, Group( "Sounds" )] public SoundPointComponent BoatOnWaterLoop  { get; set; }

	private Rigidbody m_Rigidbody;
	private Buoyancy m_Buoyancy;

	private float m_TargetThrust;
	private float m_TargetTurn;

	public bool IsOccupied => GetComponentInChildren<PlayerController>( false ) != null;



	protected override void OnStart()
	{
		m_Rigidbody = GetComponent<Rigidbody>();
		m_Buoyancy = GetComponent<Buoyancy>();
	}



	protected override void OnFixedUpdate()
	{
		if ( !m_Rigidbody.IsValid() )
			return;

		HandleSounds();
		Stabilize();

		if ( IsOccupied )
			HandleMovement();
		else
		{
			// Smoothly reset forces when unmanned
			m_TargetThrust = 0f;
			m_TargetTurn   = 0f;
		}
	}
	
	
	
	public bool CanPress( IPressable.Event e )
	{
		return e.Source is PlayerController && !IsOccupied;
	}

	public bool Press( IPressable.Event e )
	{
		if ( e.Source is not PlayerController player ) return false;
		if ( IsOccupied ) return false;

		MountPlayer( player );
		return true;
	}

	public IPressable.Tooltip? GetTooltip( IPressable.Event e )
	{
		if ( IsOccupied ) return null;
		
		var tooltip = new IPressable.Tooltip
		{
			Title = TooltipTitle,
			Icon = TooltipIcon
		};

		return tooltip;
	}
	
	
	
	public void AskToLeave( PlayerController player )
	{
		DismountPlayer( player );
	}

	public void UpdatePlayerAnimator( PlayerController controller, SkinnedModelRenderer renderer )
	{
		controller.LocalTransform = global::Transform.Zero;
		renderer.LocalRotation   = Rotation.Identity;
		renderer.Set( "sit",        (int)BaseChair.AnimatorSitPose.ChairForward );
		renderer.Set( "b_grounded", true );
		renderer.Set( "b_climbing", false );
		renderer.Set( "b_swim",     false );
		renderer.Set( "duck",       false );
	}

	public Transform CalculateEyeTransform( PlayerController controller )
	{
		var anchor = EyePosition ?? SeatPosition ?? GameObject;

		// Position follows the seat anchor so the camera rides with the boat.
		// Rotation uses the player's eye angles in pure world space, the boat's
		// pitch and roll are intentionally NOT applied so the view stays level
		// even when the hull bobs or banks.
		return new Transform
		{
			Position = anchor.WorldPosition,
			Rotation = controller.EyeAngles.ToRotation()
		};
	}
	
	
	
	private void MountPlayer( PlayerController player )
	{
		var seat = SeatPosition ?? GameObject;

		// Disable the player's own physics so they don't fight the boat
		if ( player.Body.IsValid() )          player.Body.Enabled = false;
		if ( player.ColliderObject.IsValid() ) player.ColliderObject.Enabled = false;
		
		player.GameObject.SetParent( seat, false );
		player.GameObject.LocalTransform = global::Transform.Zero;
	}

	private void DismountPlayer( PlayerController player )
	{
		player.GameObject.SetParent( null, true );
		
		if ( player.Body.IsValid() )          player.Body.Enabled = true;
		if ( player.ColliderObject.IsValid() ) player.ColliderObject.Enabled = true;
		
		// Move to exit point, or eject to the side if none is set
		player.WorldPosition = ExitPoint != null
			? ExitPoint.WorldPosition
			: WorldPosition + WorldRotation.Right * 100f + Vector3.Up * 30f;

		m_TargetThrust = 0f;
		m_TargetTurn   = 0f;
	}
	
	
	
	private void HandleMovement()
	{
		// Only push when the hull is actually in the water
		if ( m_Buoyancy is { IsTouchingWater: false } )
			return;

		float fwd  = Input.AnalogMove.x; // W = +1  S = -1
		float side = Input.AnalogMove.y; // D = +1  A = -1
		
		// Thrust
		float wantedThrust = fwd > 0.02f  ?  ThrustForce * fwd
		                   : fwd < -0.02f ? ReverseForce * fwd
		                   : 0f;

		m_TargetThrust = float.Lerp( m_TargetThrust, wantedThrust, Time.Delta * 3f );

		float speed   = m_Rigidbody.Velocity.WithZ( 0 ).Length;
		float limiter = MathF.Min( 1f, TerminalSpeed / ( speed + 0.001f ) );

		m_Rigidbody.ApplyForce( WorldRotation.Right * m_TargetThrust * limiter );

		// Turning
		float speedFactor = float.Clamp( speed / 200f, 0.2f, 1f );
		float wantedTurn  = side * TurnForce * speedFactor;
		m_TargetTurn       = float.Lerp( m_TargetTurn, wantedTurn, Time.Delta * 5f );

		Vector3 bow = WorldPosition + WorldRotation.Forward * 60f;
		m_Rigidbody.ApplyForceAt( bow, WorldRotation.Left * m_TargetTurn );

		// Speed dependent damping so the boat decelerates naturally
		float damping = ( TerminalSpeed / ( speed + 0.001f ) ) * 0.5f;
		m_Rigidbody.LinearDamping = float.Clamp( damping, 0.5f, 5f );
	}
	
	
	
	private void HandleSounds()
	{
		if (Scene.Camera is not CameraComponent camera)
			return;

		HandleWavesSound(camera);
		HandleMovementSound(camera);
	}
	
	
	
	private void HandleWavesSound(CameraComponent _Camera)
	{
		if (!BoatUnderWaves.IsValid())
			return;
		
		float distance = _Camera.WorldPosition.DistanceSquared(WorldPosition);
		float MaxDistanceSq = BoatUnderWaves.Distance * BoatUnderWaves.Distance;

		float speed = m_Rigidbody.Velocity.WithZ(0).Length;
		
		if (speed < 10.0f && distance < MaxDistanceSq && m_Buoyancy.IsTouchingWater && m_TimeSinceLastUnderWave > m_LastHitTimer)
		{
			Sound.Play(BoatUnderWaves, WorldPosition);

			m_TimeSinceLastUnderWave = 0;
			m_LastHitTimer = Game.Random.Float(2.0f, 10.0f);
		}
	}
	
	
	
	private void HandleMovementSound(CameraComponent _Camera)
	{
		if (!BoatOnWaterLoop.IsValid())
			return;
		
		float distance = _Camera.WorldPosition.DistanceSquared(WorldPosition);
		float MaxDistanceSq = BoatOnWaterLoop.Distance * BoatOnWaterLoop.Distance;
		
		if (distance > MaxDistanceSq)
		{
			// Disable the sound point if too far away from the camera (Avoid wasting resources)
			BoatOnWaterLoop.Enabled = false;
		}
		else
		{
			BoatOnWaterLoop.SoundOverride = true;
			BoatOnWaterLoop.Volume = m_Rigidbody.Velocity.WithZ(0).Length.Remap(0.0f, 200.0f);
			BoatOnWaterLoop.Enabled = true;
		}
	}
	
	
	
	private void Stabilize()
	{
		Vector3 torque = Vector3.Cross( WorldRotation.Up, Vector3.Up ) * Stability;
		m_Rigidbody.ApplyTorque( torque );
	}
}
