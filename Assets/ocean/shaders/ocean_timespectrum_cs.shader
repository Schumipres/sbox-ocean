// Ocean FFT, step 1 of 3: moves the initial spectrum h0(k) to the current time and lays out, for each wave vector,
// the eight spectra the inverse FFT turns into the surface: height, the two horizontal (choppy) displacements, and
// the five derivatives the normals and the foam (Jacobian) need. Each real field's spectrum is Hermitian, so two of
// them share one complex number (a + i b) and one FFT carries both; two float4 targets hold the four pairs.
HEADER
{
	DevShader = true;
	Description = "Ocean FFT - time-evolved spectrum";
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

	// h0(k) in .xy and conj(h0(-k)) in .zw, generated deterministically on the CPU by OceanSpectrum - for two sea
	// states, blended. Both draw each wave's amplitude from the same Gaussian, so blending them only scales it: the
	// sea moves smoothly from one state to the other without a wave jumping.
	RWTexture2D<float4> g_tInitialSpectrum < Attribute( "InitialSpectrum" ); >;
	RWTexture2D<float4> g_tInitialSpectrumB < Attribute( "InitialSpectrumB" ); >;
	float g_flSpectrumBlend < Attribute( "SpectrumBlend" ); >;

	// (h + i Dx, Dy + i dh/dx) and (dh/dy + i dDx/dx, dDy/dy + i dDx/dy).
	RWTexture2D<float4> g_tSpectrumA < Attribute( "SpectrumA" ); >;
	RWTexture2D<float4> g_tSpectrumB < Attribute( "SpectrumB" ); >;

	int g_nOceanSize < Attribute( "OceanSize" ); >;            // FFT resolution, N
	float g_flTileLength < Attribute( "TileLength" ); >;       // metres covered by one tile
	float g_flOceanTime < Attribute( "OceanTime" ); >;         // seconds, already wrapped to the loop period
	float g_flLoopPeriod < Attribute( "LoopPeriod" ); >;       // seconds after which the sea repeats exactly

	float2 ComplexMul( float2 a, float2 b ) { return float2( a.x * b.x - a.y * b.y, a.x * b.y + a.y * b.x ); }

	// i * c
	float2 TimesI( float2 c ) { return float2( -c.y, c.x ); }

	[numthreads( 8, 8, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		int n = g_nOceanSize;
		if ( id.x >= (uint)n || id.y >= (uint)n )
			return;

		// Wave vector of this texel, centred so index N/2 is k = 0.
		float2 k = ( float2( id.xy ) - n * 0.5 ) * ( 6.28318530718 / g_flTileLength );
		float kLength = length( k );

		if ( kLength < 1e-6 )
		{
			g_tSpectrumA[id.xy] = 0;
			g_tSpectrumB[id.xy] = 0;
			return;
		}

		// Deep-water dispersion, snapped to whole multiples of the loop frequency so the sea repeats exactly after
		// the loop period - and the time can stay small enough for float precision.
		float omega0 = 6.28318530718 / g_flLoopPeriod;
		float omega = floor( sqrt( 9.81 * kLength ) / omega0 ) * omega0;

		float4 h0 = lerp( g_tInitialSpectrum[id.xy], g_tInitialSpectrumB[id.xy], g_flSpectrumBlend );
		float s, c;
		sincos( omega * g_flOceanTime, s, c );

		// h(k, t) = h0(k) e^{i w t} + conj(h0(-k)) e^{-i w t}
		float2 h = ComplexMul( h0.xy, float2( c, s ) ) + ComplexMul( h0.zw, float2( c, -s ) );

		float2 kn = k / kLength;
		float2 dx = TimesI( h ) * kn.x;          // horizontal displacement along x: i kx/|k| h
		float2 dy = TimesI( h ) * kn.y;
		float2 dhdx = TimesI( h ) * k.x;         // slope: i kx h
		float2 dhdy = TimesI( h ) * k.y;
		float2 dxdx = -h * ( k.x * k.x / kLength );
		float2 dydy = -h * ( k.y * k.y / kLength );
		float2 dxdy = -h * ( k.x * k.y / kLength );

		g_tSpectrumA[id.xy] = float4( h + TimesI( dx ), dy + TimesI( dhdx ) );
		g_tSpectrumB[id.xy] = float4( dhdy + TimesI( dxdx ), dydy + TimesI( dxdy ) );
	}
}
