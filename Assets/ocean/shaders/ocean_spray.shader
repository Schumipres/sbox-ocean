// Spray thrown up where a hull cuts into the sea, drawn by OceanSpray: camera-facing quads built on the CPU, one per
// particle. Three kinds, told apart by the whole part of the second texture coordinate's y (its fraction is a seed):
//   0 - a sheet: a broad, torn fan of white water, soft-edged and patchy, thinning as it flies.
//   1 - a droplet: a small hard bead of water, stretched along its flight.
//   2 - mist: a big, faint puff the wind carries off.
// Lit by the scene's sun and sky as OceanSpray hands them over, with fog on top. Blended, never writing depth.
HEADER
{
	Description = "Ocean Spray";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
}

COMMON
{
	#include "common/shared.hlsl"
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
	float4 vSpray : TEXCOORD14;     // corner u, v (-1 to 1), opacity, kind
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput v )
	{
		PixelInput i = ProcessVertex( v );
		i.vSpray = float4( v.vTexCoord.xy, v.vTexCoord2.xy );
		return FinalizeVertex( i );
	}
}

PS
{
	#include "common/pixel.hlsl"

	RenderState( CullMode, NONE );
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, SRC_ALPHA );
	RenderState( DstBlend, INV_SRC_ALPHA );
	RenderState( DepthWriteEnable, false );

	float3 g_vSprayLight < Attribute( "SprayLight" ); Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vSprayShadow < Attribute( "SprayShadow" ); Default3( 0.55, 0.62, 0.68 ); >;

	float Hash( float2 p )
	{
		return frac( sin( dot( p, float2( 127.1, 311.7 ) ) ) * 43758.5453 );
	}

	float Noise( float2 p )
	{
		float2 i = floor( p );
		float2 f = frac( p );
		f = f * f * ( 3.0 - 2.0 * f );
		return lerp( lerp( Hash( i ), Hash( i + float2( 1, 0 ) ), f.x ), lerp( Hash( i + float2( 0, 1 ) ), Hash( i + float2( 1, 1 ) ), f.x ), f.y );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float3 worldPos = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		float2 uv = i.vSpray.xy;
		float opacity = i.vSpray.z;

		// The kind in the whole part, the particle's own seed in the fraction.
		float kind = floor( i.vSpray.w );
		float seed = frac( i.vSpray.w ) * 10.0;
		float r = length( uv );

		float alpha;
		float light;

		if ( kind < 0.5 )
		{
			// A sheet: a soft disc torn up by noise that drifts with the particle, so each sheet breaks up differently.
			float n = Noise( uv * 2.5 + seed * 3.7 ) * 0.6 + Noise( uv * 6.0 - seed * 1.3 ) * 0.4;
			alpha = saturate( ( 1.0 - r ) * 1.6 - ( 1.0 - n ) * 0.9 );
			light = 0.75 + 0.25 * ( 1.0 - r );
		}
		else if ( kind < 1.5 )
		{
			// A droplet: a bright bead with a soft rim.
			alpha = saturate( ( 1.0 - r ) * 3.0 );
			light = 0.9 + 0.1 * ( 1.0 - r );
		}
		else
		{
			// Mist: faint, wide, smooth.
			alpha = saturate( 1.0 - r ) * saturate( 1.0 - r ) * 0.45;
			light = 0.85;
		}

		alpha *= saturate( opacity );
		clip( alpha - 0.003 );

		// The top of each puff catches the sun, the bottom is in its own shade.
		float3 color = lerp( g_vSprayShadow, g_vSprayLight, saturate( light * ( 0.6 + 0.4 * uv.y ) ) );
		return DoAtmospherics( worldPos, i.vPositionSs.xy, float4( color, alpha ) );
	}
}
