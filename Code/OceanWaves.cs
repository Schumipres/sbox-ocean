using System;
using System.Threading.Tasks;

namespace OceanLib;

/// <summary>
/// The sea an <see cref="Ocean"/> draws, replayed on the CPU for physics. It takes the swell cascade - every wave
/// longer than a few metres, the ones that lift and roll a hull - and rebuilds it exactly with an FFT of its own,
/// small enough for the CPU because the swell's waves all sit near the middle of the spectrum. The chop of the finer
/// cascades isn't replayed: a hull rides over it. No GPU readback and no latency, and it runs the same on a server
/// with no GPU, so every machine floats its boats on the same water.
///
/// It matches the GPU wave for wave: same wave vectors, frequencies snapped to the loop period in the same float
/// arithmetic, and the same half-texel shift the GPU's sampler applies to the cascade's map. The grid is rebuilt
/// <see cref="Rate"/> times a second of sea time and blended in between - the swell's shortest waves take over two
/// seconds to pass - and each one is built ahead of time on a worker thread (<see cref="Prefetch"/>), so the physics
/// never waits for one. The sea is linear in its amplitudes, so each grid holds both sea states apart and the blend
/// between them is applied as it's read: exact, whatever the blend has moved to since the grid was built.
/// </summary>
public sealed class OceanWaves
{
	/// <summary>Grid rebuilds per second of sea time.</summary>
	public const float Rate = 20.0f;

	/// <summary>Points per side of the CPU's grid.</summary>
	public int GridSize { get; }

	/// <summary>How many waves it replays.</summary>
	public int Count { get; }

	/// <summary>Metres the grid covers: the swell cascade's tile.</summary>
	public float TileLength { get; }

	/// <summary>How far from the first sea state to the second, as on the GPU.</summary>
	public float Blend { get; set; }

	/// <summary>Grids built on the main thread because none was ready - ideally none after the first.</summary>
	public int SynchronousBuilds { get; private set; }

	/// <summary>Grids built on worker threads.</summary>
	public int BackgroundBuilds { get; private set; }

	/// <summary>The sea at one moment, on the grid, for each sea state: height, x-shift and y-shift in metres.</summary>
	private sealed class Grid
	{
		// The moment held, as a count of steps: whole numbers, so a moment found and a moment asked for always match.
		public int Step = int.MinValue;

		// Built on the main thread (Pending null and Filled), or by a worker whose task has finished - which is also
		// what makes its writes visible here.
		public bool Filled;
		public Task Pending;
		public bool Ready => Filled && (Pending is null || Pending.IsCompleted);

		public float[][] Height, ShiftX, ShiftY;
		public float[] WorkRe, WorkIm, PairRe, PairIm, LineRe, LineIm;
	}

	// The two in use, and the next three: a velocity reads the sea a step ahead, and the physics can run a little ahead
	// of the frame that asked for the prefetch.
	private const int GridCount = 5;
	private const int Lookahead = 3;

	private readonly int m_Log;
	private readonly float m_Shift;
	private readonly float m_Loop;
	private readonly bool m_Same;
	private readonly int[] m_Index;
	private readonly float[] m_Kx, m_Ky, m_Omega;

	// Each wave's h0 and conj(h0(-k)), per sea state.
	private readonly float[][] m_Are, m_Aim, m_Cre, m_Cim;

	private readonly float[] m_TwiddleRe, m_TwiddleIm;
	private readonly int[] m_Reverse;
	private readonly List<Grid> m_Grids = new();

	public OceanWaves( OceanSpectrum from, OceanSpectrum to, float loopPeriod )
	{
		int n = from.Size;
		TileLength = from.TileLength;
		m_Loop = loopPeriod;
		m_Same = ReferenceEquals( from, to );

		// Every wave either sea state has, and how far from the middle of the spectrum they reach: the grid just has
		// to hold them.
		int reach = 0, count = 0;

		for ( int i = 0; i < from.Texels.Length; i++ )
		{
			if ( from.Texels[i] == Vector4.Zero && to.Texels[i] == Vector4.Zero )
				continue;

			count++;
			reach = Math.Max( reach, Math.Max( Math.Abs( i % n - n / 2 ), Math.Abs( i / n - n / 2 ) ) );
		}

		GridSize = 16;
		while ( GridSize / 2 <= reach )
			GridSize *= 2;

		GridSize = Math.Min( GridSize, n );
		m_Log = (int)Math.Round( Math.Log2( GridSize ) );

		// The GPU samples its map with texel centres half a texel in, so the map it shows at a spot is the sea half a
		// texel back.
		m_Shift = 0.5f * TileLength / n;

		Count = count;
		m_Index = new int[count];
		m_Kx = new float[count]; m_Ky = new float[count]; m_Omega = new float[count];
		m_Are = new[] { new float[count], new float[count] };
		m_Aim = new[] { new float[count], new float[count] };
		m_Cre = new[] { new float[count], new float[count] };
		m_Cim = new[] { new float[count], new float[count] };

		float omega0 = 2.0f * MathF.PI / loopPeriod;
		int w = 0;

		for ( int i = 0; i < from.Texels.Length; i++ )
		{
			var a = from.Texels[i];
			var b = to.Texels[i];

			if ( a == Vector4.Zero && b == Vector4.Zero )
				continue;

			int ix = i % n - n / 2, iy = i / n - n / 2;
			var k = from.WaveVector( i % n, i / n );

			m_Index[w] = ((iy + GridSize) % GridSize) * GridSize + (ix + GridSize) % GridSize;
			m_Kx[w] = k.x;
			m_Ky[w] = k.y;

			// As the GPU works it out, in floats, so a frequency on the edge of a step snaps the same way.
			m_Omega[w] = MathF.Floor( MathF.Sqrt( 9.81f * k.Length ) / omega0 ) * omega0;
			m_Are[0][w] = a.x; m_Aim[0][w] = a.y; m_Cre[0][w] = a.z; m_Cim[0][w] = a.w;
			m_Are[1][w] = b.x; m_Aim[1][w] = b.y; m_Cre[1][w] = b.z; m_Cim[1][w] = b.w;
			w++;
		}

		m_TwiddleRe = new float[GridSize / 2];
		m_TwiddleIm = new float[GridSize / 2];

		// Inverse transform: e^{+i 2 pi j / N}.
		for ( int j = 0; j < GridSize / 2; j++ )
			(m_TwiddleIm[j], m_TwiddleRe[j]) = MathF.SinCos( 2.0f * MathF.PI * j / GridSize );

		m_Reverse = new int[GridSize];
		for ( int j = 0; j < GridSize; j++ )
		{
			int r = 0;
			for ( int bit = 0; bit < m_Log; bit++ )
				r |= ((j >> bit) & 1) << (m_Log - 1 - bit);
			m_Reverse[j] = r;
		}

		for ( int g = 0; g < GridCount; g++ )
			m_Grids.Add( NewGrid() );
	}

	private Grid NewGrid()
	{
		int cells = GridSize * GridSize;
		int states = m_Same ? 1 : 2;

		var grid = new Grid
		{
			Height = new float[states][], ShiftX = new float[states][], ShiftY = new float[states][],
			WorkRe = new float[cells], WorkIm = new float[cells], PairRe = new float[cells], PairIm = new float[cells],
			LineRe = new float[GridSize], LineIm = new float[GridSize],
		};

		for ( int s = 0; s < states; s++ )
		{
			grid.Height[s] = new float[cells];
			grid.ShiftX[s] = new float[cells];
			grid.ShiftY[s] = new float[cells];
		}

		return grid;
	}

	private static int StepOf( float time ) => (int)MathF.Floor( time * Rate );

	private Grid Find( int step )
	{
		foreach ( var g in m_Grids )
			if ( g.Ready && g.Step == step )
				return g;

		return null;
	}

	private bool Pending( int step )
	{
		foreach ( var g in m_Grids )
			if ( g.Pending is { IsCompleted: false } && g.Step == step )
				return true;

		return false;
	}

	/// <summary>A grid not being built and not one of the moments in use: the oldest.</summary>
	private Grid FreeGrid( int keepA, int keepB )
	{
		Grid best = null;

		foreach ( var g in m_Grids )
		{
			if ( g.Pending is { IsCompleted: false } )
				continue;

			if ( g.Ready && (g.Step == keepA || g.Step == keepB) )
				continue;

			if ( best is null || !g.Ready || (best.Ready && g.Step < best.Step) )
				best = g;
		}

		return best;
	}

	/// <summary>
	/// Starts building, on worker threads, the grids the physics will need next after this time - call once a frame.
	/// </summary>
	public void Prefetch( float time )
	{
		int before = StepOf( time );

		for ( int ahead = 1; ahead <= Lookahead; ahead++ )
		{
			int step = before + ahead;

			if ( Find( step ) is not null || Pending( step ) )
				continue;

			var grid = FreeGrid( before, before + 1 );

			if ( grid is null )
				return;

			grid.Step = step;
			grid.Filled = true;
			BackgroundBuilds++;
			grid.Pending = GameTask.RunInThreadAsync( () => Build( grid, step / Rate ) );
		}
	}

	/// <summary>The grids either side of a time - built here only if no worker got to them - and how far between.</summary>
	private (Grid, Grid, float) Prepare( float time )
	{
		int before = StepOf( time );

		var a = Find( before ) ?? BuildNow( before, before + 1 );
		var b = Find( before + 1 ) ?? BuildNow( before + 1, before );
		return (a, b, time * Rate - before);
	}

	private Grid BuildNow( int step, int keep )
	{
		// Never waits on a worker: its task completes through the main thread, so waiting for it here never ends. A step
		// a worker is still on is built again here, and a spare grid is added if every one is busy - rare, and kept.
		var grid = FreeGrid( keep, keep );

		if ( grid is null )
		{
			grid = NewGrid();
			m_Grids.Add( grid );
		}

		grid.Pending = null;
		grid.Step = step;
		Build( grid, step / Rate );
		grid.Filled = true;
		SynchronousBuilds++;
		return grid;
	}

	private void Build( Grid grid, float time )
	{
		for ( int state = 0; state < grid.Height.Length; state++ )
			BuildState( grid, time, state );
	}

	private void BuildState( Grid grid, float time, int state )
	{
		Array.Clear( grid.WorkRe ); Array.Clear( grid.WorkIm ); Array.Clear( grid.PairRe ); Array.Clear( grid.PairIm );

		// The waves repeat over the loop period, so any time past it is the same sea as the time it wraps to.
		float t = time % m_Loop;
		var are = m_Are[state]; var aim = m_Aim[state]; var cre = m_Cre[state]; var cim = m_Cim[state];

		for ( int w = 0; w < Count; w++ )
		{
			var (s, c) = MathF.SinCos( m_Omega[w] * t );

			// h(k, t) = h0(k) e^{i w t} + conj(h0(-k)) e^{-i w t}
			float re = are[w] * c - aim[w] * s + cre[w] * c + cim[w] * s;
			float im = are[w] * s + aim[w] * c - cre[w] * s + cim[w] * c;

			float kl = MathF.Sqrt( m_Kx[w] * m_Kx[w] + m_Ky[w] * m_Ky[w] );
			float nx = m_Kx[w] / kl, ny = m_Ky[w] / kl;

			// Two real fields share one complex FFT as a + i b: height plus i times the x-shift, whose spectrum is
			// i kx/|k| h - so i times it is -kx/|k| h. The y-shift goes alone: i ky/|k| h.
			int at = m_Index[w];
			grid.WorkRe[at] += re * (1.0f - nx);
			grid.WorkIm[at] += im * (1.0f - nx);
			grid.PairRe[at] += -im * ny;
			grid.PairIm[at] += re * ny;
		}

		Transform( grid, grid.WorkRe, grid.WorkIm );
		Transform( grid, grid.PairRe, grid.PairIm );

		int cells = GridSize * GridSize;
		Array.Copy( grid.WorkRe, grid.Height[state], cells );
		Array.Copy( grid.WorkIm, grid.ShiftX[state], cells );
		Array.Copy( grid.PairRe, grid.ShiftY[state], cells );
	}

	/// <summary>Inverse 2D FFT in place: every row, then every column, radix 2.</summary>
	private void Transform( Grid grid, float[] re, float[] im )
	{
		int n = GridSize;

		for ( int row = 0; row < n; row++ )
			Line( grid, re, im, row * n, 1 );

		for ( int column = 0; column < n; column++ )
			Line( grid, re, im, column, n );
	}

	private void Line( Grid grid, float[] re, float[] im, int start, int stride )
	{
		int n = GridSize;
		var lr = grid.LineRe;
		var li = grid.LineIm;

		for ( int j = 0; j < n; j++ )
		{
			int from = start + m_Reverse[j] * stride;
			lr[j] = re[from];
			li[j] = im[from];
		}

		for ( int size = 2; size <= n; size <<= 1 )
		{
			int half = size >> 1;
			int step = n / size;

			for ( int block = 0; block < n; block += size )
			{
				for ( int j = 0; j < half; j++ )
				{
					float wr = m_TwiddleRe[j * step], wi = m_TwiddleIm[j * step];
					int a = block + j, b = a + half;
					float tr = lr[b] * wr - li[b] * wi;
					float ti = lr[b] * wi + li[b] * wr;
					lr[b] = lr[a] - tr; li[b] = li[a] - ti;
					lr[a] += tr; li[a] += ti;
				}
			}
		}

		for ( int j = 0; j < n; j++ )
		{
			re[start + j * stride] = lr[j];
			im[start + j * stride] = li[j];
		}
	}

	/// <summary>
	/// The swell's displacement at a point of the undisplaced grid, in metres: x and y the choppy shift, z the height.
	/// Read off the grids with a Catmull-Rom curve, smooth between their points, blended between the two sea states
	/// and between the two moments either side.
	/// </summary>
	public Vector3 Displacement( Vector2 metres, float time, float choppiness )
	{
		var (a, b, moment) = Prepare( time );

		int n = GridSize;
		float u = (metres.x - m_Shift) / TileLength * n;
		float v = (metres.y - m_Shift) / TileLength * n;
		int x0 = (int)MathF.Floor( u ), y0 = (int)MathF.Floor( v );

		Span<float> wx = stackalloc float[4];
		Span<float> wy = stackalloc float[4];
		CatmullRom( u - x0, wx );
		CatmullRom( v - y0, wy );

		// The two moments, each blended between the sea states, then between each other.
		float state = m_Same ? 0.0f : Blend;
		int last = m_Same ? 0 : 1;
		float wa0 = (1.0f - moment) * (1.0f - state), wa1 = (1.0f - moment) * state;
		float wb0 = moment * (1.0f - state), wb1 = moment * state;
		var ah0 = a.Height[0]; var ah1 = a.Height[last]; var bh0 = b.Height[0]; var bh1 = b.Height[last];
		var ax0 = a.ShiftX[0]; var ax1 = a.ShiftX[last]; var bx0 = b.ShiftX[0]; var bx1 = b.ShiftX[last];
		var ay0 = a.ShiftY[0]; var ay1 = a.ShiftY[last]; var by0 = b.ShiftY[0]; var by1 = b.ShiftY[last];

		float h = 0.0f, dx = 0.0f, dy = 0.0f;

		for ( int j = 0; j < 4; j++ )
		{
			int row = ((y0 - 1 + j) % n + n) % n * n;

			for ( int i = 0; i < 4; i++ )
			{
				int at = row + ((x0 - 1 + i) % n + n) % n;
				float weight = wx[i] * wy[j];
				h += (ah0[at] * wa0 + ah1[at] * wa1 + bh0[at] * wb0 + bh1[at] * wb1) * weight;
				dx += (ax0[at] * wa0 + ax1[at] * wa1 + bx0[at] * wb0 + bx1[at] * wb1) * weight;
				dy += (ay0[at] * wa0 + ay1[at] * wa1 + by0[at] * wb0 + by1[at] * wb1) * weight;
			}
		}

		return new Vector3( dx * choppiness, dy * choppiness, h );
	}

	private static void CatmullRom( float t, Span<float> weights )
	{
		float t2 = t * t, t3 = t2 * t;
		weights[0] = -0.5f * t3 + t2 - 0.5f * t;
		weights[1] = 1.5f * t3 - 2.5f * t2 + 1.0f;
		weights[2] = -1.5f * t3 + 2.0f * t2 + 0.5f * t;
		weights[3] = 0.5f * t3 - 0.5f * t2;
	}

	/// <summary>
	/// The height of the swell's surface above a point in the world, in metres. The waves carry the water sideways as
	/// well as up, so the water over a spot started out elsewhere: a few rounds of "where would it have come from"
	/// find it.
	/// </summary>
	public float HeightAt( Vector2 metres, float time, float choppiness, int iterations = 3 )
	{
		var start = metres;

		for ( int i = 0; i < iterations; i++ )
		{
			var d = Displacement( start, time, choppiness );
			start = metres - new Vector2( d.x, d.y );
		}

		return Displacement( start, time, choppiness ).z;
	}
}
