// Ocean FFT, step 2 of 3: an inverse FFT of the two spectrum targets, in place. One thread group takes one whole line
// of 256 texels into shared memory and runs the eight radix-2 Stockham stages there, so a pass over the texture is a
// single dispatch. Run once along x (Direction 0) and once along y (Direction 1) for the 2D transform.
HEADER
{
	DevShader = true;
	Description = "Ocean FFT - inverse FFT pass";
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

	#define FFT_SIZE 256
	#define FFT_LOG_SIZE 8

	RWTexture2D<float4> g_tSpectrumA < Attribute( "SpectrumA" ); >;
	RWTexture2D<float4> g_tSpectrumB < Attribute( "SpectrumB" ); >;

	int g_nDirection < Attribute( "Direction" ); >;     // 0: along x, 1: along y

	// Each float4 holds two complex numbers; both targets ride through the same stages together.
	groupshared float4 s_A[2][FFT_SIZE];
	groupshared float4 s_B[2][FFT_SIZE];

	float2 ComplexMul( float2 a, float2 b ) { return float2( a.x * b.x - a.y * b.y, a.x * b.y + a.y * b.x ); }

	float4 Twiddle4( float2 t, float4 v ) { return float4( ComplexMul( t, v.xy ), ComplexMul( t, v.zw ) ); }

	[numthreads( FFT_SIZE, 1, 1 )]
	void MainCs( uint3 thread : SV_GroupThreadID, uint3 group : SV_GroupID )
	{
		uint index = thread.x;
		uint2 texel = g_nDirection == 0 ? uint2( index, group.y ) : uint2( group.y, index );

		s_A[0][index] = g_tSpectrumA[texel];
		s_B[0][index] = g_tSpectrumB[texel];
		GroupMemoryBarrierWithGroupSync();

		uint from = 0;

		[unroll]
		for ( uint stage = 0; stage < FFT_LOG_SIZE; stage++ )
		{
			// Stockham, self-sorting: each output reads a pair half a span apart and needs no bit reversal.
			uint span = FFT_SIZE >> ( stage + 1 );
			uint w = span * ( index / span );
			uint i = ( w + index ) % FFT_SIZE;

			// Inverse transform: e^{+i 2 pi w / N}.
			float2 twiddle;
			sincos( 6.28318530718 / FFT_SIZE * w, twiddle.y, twiddle.x );

			uint to = 1 - from;
			s_A[to][index] = s_A[from][i] + Twiddle4( twiddle, s_A[from][i + span] );
			s_B[to][index] = s_B[from][i] + Twiddle4( twiddle, s_B[from][i + span] );
			from = to;
			GroupMemoryBarrierWithGroupSync();
		}

		g_tSpectrumA[texel] = s_A[from][index];
		g_tSpectrumB[texel] = s_B[from][index];
	}
}
