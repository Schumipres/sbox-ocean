using System;

namespace OceanLib;

/// <summary>
/// The initial wave spectrum h0(k) of one ocean cascade, after Tessendorf: a Phillips spectrum for a wind of a given
/// speed and direction, with each wave's amplitude and phase drawn from a Gaussian. A cascade only keeps the waves in
/// its own band of wave numbers, so several cascades of different tile sizes add up to one sea without counting any
/// wave twice; amplitudes carry the cascade's frequency spacing (dk), so the bands join smoothly whatever their tile.
///
/// Everything comes from integer hashes of the seed and the wave's index - never from a shared random generator - so
/// every machine, the GPU's surface and any CPU-side replica of it for physics build exactly the same sea.
/// </summary>
public sealed class OceanSpectrum
{
	public const float Gravity = 9.81f;

	/// <summary>FFT resolution: waves per side of the tile.</summary>
	public int Size { get; }

	/// <summary>Metres covered by one tile.</summary>
	public float TileLength { get; }

	/// <summary>h0(k) in .xy and conj(h0(-k)) in .zw, row-major, wave vectors centred on index Size / 2.</summary>
	public Vector4[] Texels { get; }

	/// <summary>Sum of |h0|^2 before any scaling: this cascade's share of the sea's height variance, halved.</summary>
	public double RawVariance { get; }

	/// <summary>The settings shared by every cascade of one sea.</summary>
	public readonly record struct Settings( float WindSpeed, float WindAngle, float Directionality, float SmallWaveCutoff, int Seed );

	/// <summary>
	/// Builds the waves with wave numbers in [minK, maxK) - radians per metre - at their raw Phillips amplitude. Call
	/// <see cref="Scale"/> afterwards with the factor that brings all cascades together to the height wanted.
	/// </summary>
	public OceanSpectrum( int size, float tileLength, float minK, float maxK, int cascade, Settings settings )
	{
		Size = size;
		TileLength = tileLength;

		var h0 = new Vector2[size * size];
		// The leading term of h(k, t) - h0(k) e^{i w t} - runs against its wave vector, so the waves that should travel
		// downwind are the ones whose k points upwind: the spectrum is laid out against the wind.
		var wind = -new Vector2( MathF.Cos( settings.WindAngle.DegreeToRadian() ), MathF.Sin( settings.WindAngle.DegreeToRadian() ) );

		// The longest wave a wind of this speed raises; the spectrum falls away below it.
		float longest = settings.WindSpeed * settings.WindSpeed / Gravity;
		float shortest = settings.SmallWaveCutoff;
		float dk = 2.0f * MathF.PI / tileLength;
		double variance = 0.0;

		for ( int y = 0; y < size; y++ )
		{
			for ( int x = 0; x < size; x++ )
			{
				var k = WaveVector( x, y );
				float kLength = k.Length;

				// The first row and column hold the Nyquist frequency, which is its own mirror image: a displacement
				// along it can't be represented by a real field, and would leak into the height packed beside it.
				if ( kLength < 1e-6f || x == 0 || y == 0 || kLength < minK || kLength >= maxK )
					continue;

				var direction = k / kLength;
				float alignment = Vector2.Dot( direction, wind );

				// Phillips: P(k) = exp(-1/(kL)^2) / k^4 * |k.w|^p, small waves damped by exp(-k^2 l^2). Waves running
				// against the wind keep only a little of their energy.
				float kl = kLength * longest;
				float phillips = MathF.Exp( -1.0f / (kl * kl) ) / (kLength * kLength * kLength * kLength);
				phillips *= MathF.Pow( MathF.Abs( alignment ), settings.Directionality );
				phillips *= MathF.Exp( -kLength * kLength * shortest * shortest );

				if ( alignment < 0.0f )
					phillips *= 0.07f;

				var gauss = Gaussian( settings.Seed, cascade, x, y );
				h0[y * size + x] = gauss * (MathF.Sqrt( phillips * 0.5f ) * dk);
				variance += h0[y * size + x].LengthSquared;
			}
		}

		RawVariance = variance;
		Texels = new Vector4[size * size];

		for ( int y = 0; y < size; y++ )
		{
			for ( int x = 0; x < size; x++ )
			{
				var a = h0[y * size + x];
				var b = h0[((size - y) % size) * size + (size - x) % size];
				Texels[y * size + x] = new Vector4( a.x, a.y, b.x, -b.y );
			}
		}
	}

	private OceanSpectrum( int size, float tileLength, Vector4[] texels )
	{
		Size = size;
		TileLength = tileLength;
		Texels = texels;
	}

	/// <summary>
	/// The spectrum part way between two of the same tile: the sea is linear in its amplitudes, so this is exactly the
	/// sea a blend between them shows.
	/// </summary>
	public static OceanSpectrum Lerp( OceanSpectrum a, OceanSpectrum b, float t )
	{
		var texels = new Vector4[a.Texels.Length];

		for ( int i = 0; i < texels.Length; i++ )
			texels[i] = a.Texels[i] + (b.Texels[i] - a.Texels[i]) * t;

		return new OceanSpectrum( a.Size, a.TileLength, texels );
	}

	/// <summary>Multiplies every amplitude.</summary>
	public void Scale( float factor )
	{
		for ( int i = 0; i < Texels.Length; i++ )
			Texels[i] *= factor;
	}

	/// <summary>
	/// The factor that brings a set of cascades to a significant wave height (four standard deviations of the height,
	/// in metres). h(x) sums h0(k) and conj(h0(-k)) for every k, so its variance is twice the sum of |h0|^2.
	/// </summary>
	public static float ScaleFor( float significantHeight, double totalRawVariance )
	{
		return totalRawVariance > 0.0 ? significantHeight / (4.0f * MathF.Sqrt( (float)(2.0 * totalRawVariance) )) : 0.0f;
	}

	/// <summary>The wave vector at a texel, in radians per metre.</summary>
	public Vector2 WaveVector( int x, int y ) => new Vector2( x - Size / 2, y - Size / 2 ) * (2.0f * MathF.PI / TileLength);

	/// <summary>A pair of independent standard Gaussians for one wave, by Box-Muller over two hashed uniforms.</summary>
	private static Vector2 Gaussian( int seed, int cascade, int x, int y )
	{
		float u1 = MathF.Max( Uniform( seed, cascade, x, y, 0 ), 1e-7f );
		float u2 = Uniform( seed, cascade, x, y, 1 );
		float r = MathF.Sqrt( -2.0f * MathF.Log( u1 ) );
		return new Vector2( r * MathF.Cos( 2.0f * MathF.PI * u2 ), r * MathF.Sin( 2.0f * MathF.PI * u2 ) );
	}

	/// <summary>A uniform in [0, 1) from a PCG-style integer hash: the same on every machine.</summary>
	private static float Uniform( int seed, int cascade, int x, int y, int channel )
	{
		uint h = (uint)seed * 747796405u + 2891336453u + (uint)cascade * 0x27D4EB2Fu;
		h ^= (uint)x * 0x9E3779B1u;
		h = Pcg( h ) ^ ((uint)y * 0x85EBCA77u);
		h = Pcg( h ) ^ ((uint)channel * 0xC2B2AE3Du);
		h = Pcg( h );
		return (h >> 8) * (1.0f / 16777216.0f);
	}

	private static uint Pcg( uint v )
	{
		uint state = v * 747796405u + 2891336453u;
		uint word = ((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u;
		return (word >> 22) ^ word;
	}
}
