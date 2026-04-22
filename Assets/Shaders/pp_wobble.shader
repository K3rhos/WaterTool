
HEADER
{
	Description = "A simple sin wave wobble post processing shader (Can be used to give an Underwater/Drunk effect)";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth();
}

COMMON
{
	#ifndef S_ALPHA_TEST
	#define S_ALPHA_TEST 0
	#endif
	#ifndef S_TRANSLUCENT
	#define S_TRANSLUCENT 0
	#endif
	
	#include "common/shared.hlsl"
	#include "procedural.hlsl"

	#define S_UV2 1
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
	float4 vColor : COLOR0 < Semantic( Color ); >;
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
	float3 vPositionOs : TEXCOORD14;
	float3 vNormalOs : TEXCOORD15;
	float4 vTangentUOs_flTangentVSign : TANGENT	< Semantic( TangentU_SignV ); >;
	float4 vColor : COLOR0;
	float4 vTintColor : COLOR1;
	#if ( PROGRAM == VFX_PROGRAM_PS )
		bool vFrontFacing : SV_IsFrontFace;
	#endif
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs(VertexInput v)
	{
		PixelInput i;
		i.vPositionPs = float4(v.vPositionOs.xy, 0.0f, 1.0f);
		i.vPositionWs = float3(v.vTexCoord, 0.0f);
		
		return i;
	}
}

PS
{
	#include "common/pixel.hlsl"
	#include "postprocess/functions.hlsl"
	#include "postprocess/common.hlsl"
		
	Texture2D g_tColorBuffer < Attribute( "ColorBuffer" ); SrgbRead ( true ); >;
	float g_flFrequency < Attribute( "Frequency" ); Default1( 20 ); >;
	float g_flSpeed < Attribute( "Speed" ); Default1( 1 ); >;
	float g_flAmplitude < Attribute( "Amplitude" ); Default1( 1 ); >;
		
	float2 MapSceneColorCoords( float2 vInput, float2 modes )
	{
		float2 result;
	
		// X
		if ( modes.x == 1 ) // Mirror
		{
			float xx = abs( vInput.x );
			result.x = (fmod( floor( xx ), 2.0 ) == 0.0) ? frac( xx ) : 1.0 - frac( xx );
		}
		else if ( modes.x == 2 ) // Clamp
		{
			result.x = clamp( vInput.x, 0.0, 1.0 );
		}
		else if ( modes.x == 3 ) // Border
		{
			result.x = (vInput.x < 0.0 || vInput.x > 1.0) ? 0.5 : vInput.x;
		}
		else if ( modes.x == 4 ) // MirrorOnce
		{
	        float xx = abs( vInput.x );
			float floorX = floor( xx );
			if ( floorX < 1.0 )
			{
				result.x = frac( xx );
			}
			else if ( floorX < 2.0 )
			{
				result.x = 1.0 - frac( xx );
			}
			else
			{
				result.x = vInput.x;
			}
		}
		else // Wrap by default
		{
			result.x = vInput.x;
		}
	
		// Y
		if ( modes.y == 1 ) // Mirror
		{
			float yy = abs( vInput.y );
			result.y = (fmod( floor( yy ), 2.0 ) == 0.0) ? frac( yy ) : 1.0 - frac( yy );
		}
		else if ( modes.y == 2 ) // Clamp
		{
			result.y = clamp( vInput.y, 0.0, 1.0 );
		}
		else if ( modes.y == 3 ) // Border
		{
			result.y = (vInput.y < 0.0 || vInput.y > 1.0) ? 0.5 : vInput.y;
		}
		else if ( modes.y == 4 ) // MirrorOnce
		{
			float yy = abs( vInput.y );
			float floorY = floor( yy );
			if ( floorY < 1.0 )
			{
				result.y = frac( yy );
			}
			else if ( floorY < 2.0 )
			{
				result.y = 1.0 - frac( yy );
			}
			else
			{
				result.y = vInput.y;
			}
		}
		else // Wrap by default
		{
			result.y = vInput.y;
		}
	
		return result;
	}
	
	float4 MainPs(PixelInput i) : SV_Target0
	{
		float2 uv = CalculateViewportUv(i.vPositionSs.xy);
		
		float phaseY = uv.y * g_flFrequency;
		
		float timeOffset = g_flTime * g_flSpeed;
		
		float wave = sin(phaseY + timeOffset);
		
		float scale = g_flAmplitude * 0.01;
		
		float offsetX = uv.x + wave * scale;

		float2 wobbledUv = float2(offsetX, uv.y);

		float3 finalColor = g_tColorBuffer.Sample(g_sAniso, MapSceneColorCoords(wobbledUv, float2(1, 1))).rgb;

		return float4(finalColor, 1);
	}
}
