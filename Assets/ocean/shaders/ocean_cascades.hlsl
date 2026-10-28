// The maps the FFT writes for each of the three cascades (see Ocean.Bind), and what every shader reading the sea works
// out from them. Include it inside a stage block. Sampling is explicit-LOD by default, which any stage can do; a pixel
// shader that wants mip-mapped, filtered slopes defines OCEAN_SAMPLE as tex.Sample( s, uv ) before including.
#ifndef OCEAN_CASCADES_HLSL
#define OCEAN_CASCADES_HLSL

#ifndef OCEAN_SAMPLE
#define OCEAN_SAMPLE( tex, s, uv ) tex.SampleLevel( s, uv, 0 )
#endif

// x, y: choppy horizontal shift; z: height; w: the shear dDx/dy. All in metres.
Texture2D g_tOceanDisplacement0 < Attribute( "OceanDisplacement0" ); >;
Texture2D g_tOceanDisplacement1 < Attribute( "OceanDisplacement1" ); >;
Texture2D g_tOceanDisplacement2 < Attribute( "OceanDisplacement2" ); >;

// x, y: slope of the height; z, w: stretch of the horizontal shift along x and y. Mip-mapped.
Texture2D g_tOceanDerivatives0 < Attribute( "OceanDerivatives0" ); >;
Texture2D g_tOceanDerivatives1 < Attribute( "OceanDerivatives1" ); >;
Texture2D g_tOceanDerivatives2 < Attribute( "OceanDerivatives2" ); >;

float3 g_vOceanTileLengths < Attribute( "OceanTileLengths" ); Default3( 19685, 3819, 760 ); >;   // units
float g_flOceanUnitsPerMetre < Attribute( "OceanUnitsPerMetre" ); Default1( 39.37 ); >;

// The three cascades' displacement at a point of the undisplaced grid (world units), each weighted. A cascade whose
// 'active' is 0 isn't fetched at all: one faded out by distance. With implicit-LOD sampling, keep 'active' the same
// across a draw (a uniform), so neighbouring pixels never disagree about sampling.
float4 OceanDisplacement( SamplerState s, float2 world, float3 weight, float3 active )
{
	float4 sum = 0.0;
	[branch] if ( active.x > 0.0 ) sum += OCEAN_SAMPLE( g_tOceanDisplacement0, s, world / g_vOceanTileLengths.x ) * weight.x;
	[branch] if ( active.y > 0.0 ) sum += OCEAN_SAMPLE( g_tOceanDisplacement1, s, world / g_vOceanTileLengths.y ) * weight.y;
	[branch] if ( active.z > 0.0 ) sum += OCEAN_SAMPLE( g_tOceanDisplacement2, s, world / g_vOceanTileLengths.z ) * weight.z;
	return sum;
}

float4 OceanDerivatives( SamplerState s, float2 world, float3 weight, float3 active )
{
	float4 sum = 0.0;
	[branch] if ( active.x > 0.0 ) sum += OCEAN_SAMPLE( g_tOceanDerivatives0, s, world / g_vOceanTileLengths.x ) * weight.x;
	[branch] if ( active.y > 0.0 ) sum += OCEAN_SAMPLE( g_tOceanDerivatives1, s, world / g_vOceanTileLengths.y ) * weight.y;
	[branch] if ( active.z > 0.0 ) sum += OCEAN_SAMPLE( g_tOceanDerivatives2, s, world / g_vOceanTileLengths.z ) * weight.z;
	return sum;
}

// The shear alone, for the Jacobian. Always read at the top level, without anisotropy: the displacement maps have no
// mips, and an anisotropic read of a map without them scatters its taps across the whole map far off - the single
// most expensive thing the surface did - for a difference nobody can see.
float OceanShear( SamplerState s, float2 world, float3 weight, float3 active )
{
	float sum = 0.0;
	[branch] if ( active.x > 0.0 ) sum += g_tOceanDisplacement0.SampleLevel( s, world / g_vOceanTileLengths.x, 0 ).w * weight.x;
	[branch] if ( active.y > 0.0 ) sum += g_tOceanDisplacement1.SampleLevel( s, world / g_vOceanTileLengths.y, 0 ).w * weight.y;
	[branch] if ( active.z > 0.0 ) sum += g_tOceanDisplacement2.SampleLevel( s, world / g_vOceanTileLengths.z, 0 ).w * weight.z;
	return sum;
}

float4 OceanDisplacement( SamplerState s, float2 world, float3 weight ) { return OceanDisplacement( s, world, weight, 1.0 ); }
float4 OceanDerivatives( SamplerState s, float2 world, float3 weight ) { return OceanDerivatives( s, world, weight, 1.0 ); }

// The Jacobian of the choppy shift: 1 for flat water, below 1 where the surface bunches into a crest, below 0 where it
// has folded over - the crests that break into foam.
float OceanJacobian( float4 derivatives, float shear )
{
	return ( 1.0 + derivatives.z ) * ( 1.0 + derivatives.w ) - shear * shear;
}

// The surface normal: slopes taken against the ground the choppy shift moved them over.
float3 OceanNormal( float4 derivatives )
{
	float2 slope = derivatives.xy / ( 1.0 + derivatives.zw );
	return normalize( float3( -slope.x, -slope.y, 1.0 ) );
}

#endif
