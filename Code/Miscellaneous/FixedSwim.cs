using Sandbox;
using Sandbox.Movement;

namespace RedSnail.WaterTool;

/// <summary>
/// The character is swimming
/// </summary>
[Icon( "scuba_diving" ), Group( "Movement" ), Title( "MoveMode - Swim (Fixed)" )]
public sealed class MoveModeSwimFixed : MoveMode
{
	private float m_TargetVolume;
	private bool m_WasInWater;
	private bool m_HasTouchedWater;
	
	[Property]
	public int Priority { get; set; } = 10;

	[Property, Range( 0, 1 )]
	public float SwimLevel { get; set; } = 0.7f;

	/// <summary>
	/// We will update this based on how much you're in a "water" tagged trigger.
	/// </summary>
	public float WaterLevel { get; private set; }
	
	[Property, Group("Sounds")] public SoundEvent WaterSplashEnter { get; set; }
	[Property, Group("Sounds")] public SoundEvent WaterSplashExit { get; set; }
	[Property, Group("Sounds")] public SoundPointComponent WaterSwimmingLoop { get; set; }

	protected override void OnStart()
	{
		if (WaterSwimmingLoop.IsValid())
			WaterSwimmingLoop.Enabled = false;
	}
	
	public override void UpdateRigidBody( Rigidbody body )
	{
		body.Gravity = false;
		body.LinearDamping = 3.3f;
		body.AngularDamping = 1f;
	}

	public override int Score( PlayerController controller )
	{
		if ( WaterLevel > SwimLevel ) return Priority;
		return -100;
	}

	public override void OnModeBegin()
	{
		Controller.IsSwimming = true;
		
		if (WaterSwimmingLoop.IsValid())
			WaterSwimmingLoop.Enabled = true;
	}

	public override void OnModeEnd( MoveMode next )
	{
		Controller.IsSwimming = false;
		
		if (WaterSwimmingLoop.IsValid())
			WaterSwimmingLoop.Enabled = false;
		
		// jump when leaving the water
		if ( Input.Down( "Jump" ) )
		{
			Controller.Jump( Vector3.Up * 300 );
		}
	}

	protected override void OnFixedUpdate()
	{
		UpdateWaterLevel();

		if (m_HasTouchedWater != m_WasInWater)
		{
			if (m_HasTouchedWater)
			{
				if (WaterSplashEnter.IsValid())
					Sound.Play(WaterSplashEnter, WorldPosition);
			}
			else
			{
				if (WaterSplashExit.IsValid())
					Sound.Play(WaterSplashExit, WorldPosition);
			}
			
			m_WasInWater = m_HasTouchedWater;
		}
	}

	void UpdateWaterLevel()
	{
		if ( Controller?.Body == null )
			return;
		
		if (GameObject.Parent != Scene)
			return;

		var wt = WorldTransform;
		Vector3 head = wt.PointToWorld( new Vector3( 0, 0, Controller.CurrentHeight ) );
		Vector3 foot = wt.Position;

		float waterLevel = 0;
		
		var waterSurface = WaterManager.GetWaterHeightAt( head );
		var level = Vector3.InverseLerp( waterSurface, foot, head );
		level = (level * 100).CeilToInt() / 100.0f;

		if (level > waterLevel)
		{
			m_HasTouchedWater = true;
			waterLevel = level;
		}
		else
		{
			m_HasTouchedWater = false;
		}

		if ( WaterLevel != waterLevel )
		{
			WaterLevel = waterLevel;
		}
	}

	public override Vector3 UpdateMove( Rotation eyes, Vector3 input )
	{
		if ( Input.Down( "jump" ) )
		{
			input += Vector3.Up;
		}

		return base.UpdateMove( eyes, input );
	}
}
