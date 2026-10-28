// Turns one of the ocean's maps into a picture for OceanDebugView: a cascade's height (blue troughs, white crests), a
// cascade's folding (its own Jacobian: white where the surface bunches, red where it folds over) or the foam window.
HEADER
{
	DevShader = true;
	Description = "Ocean debug view";
}

MODES
{
	Default();
}

FEATURES
{
}

COMMON
{
	#include "system.fxc"
}

CS
{
	#include "common.fxc"
	#include "ocean/shaders/ocean_cascades.hlsl"

	Texture2D g_tDisplacement < Attribute( "DebugDisplacement" ); >;
	Texture2D g_tDerivatives < Attribute( "DebugDerivatives" ); >;
	Texture2D g_tFoam < Attribute( "DebugFoam" ); >;
	SamplerState g_sClamp < Filter( BILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	RWTexture2D<float4> g_tOut < Attribute( "DebugOut" ); >;
	int g_nSize < Attribute( "DebugSize" ); >;
	float g_flMode < Attribute( "DebugMode" ); >;      // 0 height, 1 folding, 2 foam
	float g_flRange < Attribute( "DebugRange" ); >;    // metres of height shown as full white or full blue

	[numthreads( 8, 8, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		if ( any( id.xy >= (uint)g_nSize ) )
			return;

		float2 uv = ( float2( id.xy ) + 0.5 ) / g_nSize;
		float3 color;

		if ( g_flMode < 0.5 )
		{
			float h = g_tDisplacement.SampleLevel( g_sClamp, uv, 0 ).z / max( g_flRange, 1e-3 );
			color = h < 0.0 ? lerp( float3( 0.1, 0.25, 0.5 ), float3( 0.0, 0.02, 0.1 ), saturate( -h ) )
			                : lerp( float3( 0.1, 0.25, 0.5 ), float3( 1.0, 1.0, 1.0 ), saturate( h ) );
		}
		else if ( g_flMode < 1.5 )
		{
			float j = OceanJacobian( g_tDerivatives.SampleLevel( g_sClamp, uv, 0 ), g_tDisplacement.SampleLevel( g_sClamp, uv, 0 ).w );
			color = j < 0.0 ? float3( 1.0, 0.15, 0.1 ) : saturate( 1.0 - j ) * float3( 1.0, 1.0, 1.0 );
		}
		else
		{
			color = saturate( g_tFoam.SampleLevel( g_sClamp, uv, 0 ).x ) * float3( 1.0, 1.0, 1.0 );
		}

		g_tOut[id.xy] = float4( color, 1.0 );
	}
}
