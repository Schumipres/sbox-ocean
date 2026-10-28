// Ocean FFT, step 3 of 3: turns the transformed spectra into the maps the surface reads. Because the wave vectors were
// centred on N/2, every other texel comes out of the FFT negated; this flips it back. Then it unpacks the real fields
// and writes the displacement (choppy x, y and height, in metres) and the derivatives of all three. The Jacobian of
// the horizontal displacement - below 1 where the surface bunches up, below 0 where it folds over, the crests that
// should foam - needs every cascade's derivatives summed first, so the surface works it out from these.
HEADER
{
	DevShader = true;
	Description = "Ocean FFT - assemble displacement and derivatives";
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

	RWTexture2D<float4> g_tSpectrumA < Attribute( "SpectrumA" ); >;
	RWTexture2D<float4> g_tSpectrumB < Attribute( "SpectrumB" ); >;

	// x, y: horizontal displacement; z: height; w: the shear of the horizontal displacement, dDx/dy.
	RWTexture2D<float4> g_tDisplacement < Attribute( "Displacement" ); >;

	// x, y: slope of the height; z, w: stretch of the horizontal displacement along x and y.
	RWTexture2D<float4> g_tDerivatives < Attribute( "Derivatives" ); >;

	int g_nOceanSize < Attribute( "OceanSize" ); >;
	float g_flChoppiness < Attribute( "Choppiness" ); >;

	[numthreads( 8, 8, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		if ( id.x >= (uint)g_nOceanSize || id.y >= (uint)g_nOceanSize )
			return;

		float sign = ( ( id.x + id.y ) & 1 ) ? -1.0 : 1.0;
		float4 a = g_tSpectrumA[id.xy] * sign;
		float4 b = g_tSpectrumB[id.xy] * sign;

		// a = (h, Dx, Dy, dh/dx), b = (dh/dy, dDx/dx, dDy/dy, dDx/dy), each the real part then the imaginary part.
		float height = a.x;
		float2 shift = float2( a.y, a.z ) * g_flChoppiness;
		float2 slope = float2( a.w, b.x );
		float dxdx = b.y * g_flChoppiness;
		float dydy = b.z * g_flChoppiness;
		float dxdy = b.w * g_flChoppiness;

		g_tDisplacement[id.xy] = float4( shift, height, dxdy );
		g_tDerivatives[id.xy] = float4( slope, dxdx, dydy );
	}
}
