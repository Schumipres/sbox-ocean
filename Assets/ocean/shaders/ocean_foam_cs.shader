// Ocean foam: a window of foam that follows the camera, carried over from frame to frame. Each
// frame every texel takes what was under it last frame (the window moved by whole texels, so nothing smears), lets it
// fade and blurs it a little into its neighbours - the foam dispersing - and then adds new foam where the sea makes
// it: where the crests fold over (the Jacobian of all three cascades below the threshold), and in a band round
// anything that cuts the surface - hulls, rocks - so a moving hull leaves a trail behind it.
//
// Texels are laid out on the undisplaced ocean grid, the same space the surface shader looks its waves up in, so the
// foam rides the waves wherever their choppy shift carries them.
HEADER
{
	DevShader = true;
	Description = "Ocean foam - persistent camera-centred foam buffer";
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

	RWTexture2D<float> g_tFoamPrevious < Attribute( "FoamPrevious" ); >;
	RWTexture2D<float> g_tFoamNext < Attribute( "FoamNext" ); >;

	#include "ocean/shaders/ocean_cascades.hlsl"

	SamplerState g_sWrap < Filter( BILINEAR ); AddressU( WRAP ); AddressV( WRAP ); >;

	int g_nFoamSize < Attribute( "FoamSize" ); >;                      // texels per side
	float2 g_vFoamOrigin < Attribute( "FoamOrigin" ); >;               // world xy of texel (0, 0)'s corner
	float g_flFoamTexel < Attribute( "FoamTexel" ); >;                 // units per texel
	float2 g_vFoamShift < Attribute( "FoamShift" ); >;                 // whole texels the window moved since last frame

	float g_flDecay < Attribute( "FoamDecay" ); >;                     // what's left of the foam after this frame
	float g_flBlur < Attribute( "FoamBlur" ); >;                       // how much each texel mixes with its neighbours
	float g_flThreshold < Attribute( "FoamThreshold" ); >;             // Jacobian below which crests make foam
	float g_flCrestRate < Attribute( "FoamCrestRate" ); >;             // foam added per second at a fully folded crest
	float g_flDelta < Attribute( "FoamDelta" ); >;                     // seconds this frame

	// Hulls and rocks, three rows each: (centre xyz, strength), (forward xy, half length, half width),
	// (lowest z, highest z, band width, 0) - all in world units.
	int g_nSourceCount < Attribute( "FoamSourceCount" ); >;
	StructuredBuffer<float4> g_vSources < Attribute( "FoamSources" ); >;

	float Previous( int2 p )
	{
		p += int2( round( g_vFoamShift ) );
		if ( any( p < 0 ) || any( p >= g_nFoamSize ) )
			return 0.0;
		return g_tFoamPrevious[p];
	}

	[numthreads( 8, 8, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		int2 p = int2( id.xy );
		if ( any( p >= g_nFoamSize ) )
			return;

		// Carry over, spread and fade.
		float centre = Previous( p );
		float around = ( Previous( p + int2( 1, 0 ) ) + Previous( p - int2( 1, 0 ) ) + Previous( p + int2( 0, 1 ) ) + Previous( p - int2( 0, 1 ) ) ) * 0.25;
		float foam = lerp( centre, around, g_flBlur ) * g_flDecay;

		float2 world = g_vFoamOrigin + ( float2( p ) + 0.5 ) * g_flFoamTexel;
		// Crests folding over: the same Jacobian the surface shades with.
		float4 s = OceanDisplacement( g_sWrap, world, 1.0 );
		float jacobian = OceanJacobian( OceanDerivatives( g_sWrap, world, 1.0 ), s.w );

		foam += saturate( g_flThreshold - jacobian ) * g_flCrestRate * g_flDelta;

		// Round anything cutting the surface: a band just outside its outline, where it stands in the water. The
		// waves carry this bit of water sideways as well as up - by metres in a storm - so it's where the water
		// actually is that's tested against the outline, not the grid point it started from.
		float3 shift = s.xyz * g_flOceanUnitsPerMetre;
		float sea = shift.z;
		float2 water = world + shift.xy;

		for ( int e = 0; e < g_nSourceCount; e++ )
		{
			float4 a = g_vSources[e * 3 + 0];
			float4 b = g_vSources[e * 3 + 1];
			float4 c = g_vSources[e * 3 + 2];

			if ( sea < c.x || sea > c.y )
				continue;

			float2 rel = water - a.xy;
			float2 local = float2( dot( rel, b.xy ), dot( rel, float2( -b.y, b.x ) ) );

			// Distance outside the ellipse, roughly in world units.
			float outside = ( length( local / b.zw ) - 1.0 ) * min( b.z, b.w );
			float band = saturate( 1.0 - abs( outside ) / c.z );
			foam += band * band * a.w * g_flDelta;
		}

		g_tFoamNext[p] = saturate( foam );
	}
}
