using System;
using System.Collections.Generic;
using Sandbox.Rendering;

namespace OceanLib;

/// <summary>
/// Simulates the open ocean on the GPU, after Tessendorf's FFT method. One wind spectrum (Phillips, see
/// <see cref="OceanSpectrum"/>) is split by wave length across three cascades of different tile sizes - the swell, the
/// waves on it and the chop on those - so the sea has detail from hundreds of metres down to a hand's width, and the
/// tiles, being of unrelated sizes, never visibly repeat. Each cascade's spectrum is moved to the current time and
/// turned into a surface by an inverse FFT in compute shaders, giving two maps per cascade:
/// <see cref="Cascade.Displacement"/> (choppy x and y shift, height, shear) and <see cref="Cascade.Derivatives"/>
/// (slopes and stretch, mip-mapped for distant shading). The sea depends only on these settings and the time, so every
/// machine sees the same waves.
///
/// The sea state - wind, wave height, look - comes from an <see cref="OceanPreset"/>. The ocean can hold two at once
/// and blend between them (<see cref="BlendTo"/>, <see cref="SetBlend"/>): both are built once and kept, and the blend
/// is a number the GPU and the CPU replica each apply as they go, so a calm can build into a storm without anything
/// being rebuilt.
///
/// For physics, <see cref="HeightAt"/> and <see cref="WaterVelocityAt"/> read a CPU replica of the swell
/// (<see cref="OceanWaves"/>). The GPU work runs from a command list on the main camera at the end of each frame, for
/// the time of the next one, timed as "Ocean FFT" in the GPU profiler.
/// </summary>
[Title( "Ocean" ), Category( "Ocean" ), Icon( "waves" )]
public sealed class Ocean : Component
{
	/// <summary>Waves per side of a cascade's tile. Fixed by the FFT shader's shared memory.</summary>
	public const int Size = 256;

	public const int CascadeCount = 3;

	/// <summary>s&amp;box units (inches) per metre. Every length the ocean exposes is in metres unless it says otherwise.</summary>
	public const float UnitsPerMetre = 39.37f;

	/// <summary>The sea state to start from. Empty is a moderate sea (<see cref="OceanSea.Default"/>).</summary>
	[Property, Group( "Sea" )] public OceanPreset Preset { get; set; }

	/// <summary>Which way the wind blows, in degrees from +x.</summary>
	[Property, Group( "Sea" ), Range( 0, 360 )] public float WindAngle { get; set; } = 0.0f;

	/// <summary>How tightly the waves follow the wind: 2 is Tessendorf's, higher lines them up more.</summary>
	[Property, Group( "Sea" ), Range( 1, 8 )] public float Directionality { get; set; } = 2.0f;

	/// <summary>Waves much shorter than this, in metres, are damped out.</summary>
	[Property, Group( "Sea" ), Range( 0, 2 )] public float SmallWaveCutoff { get; set; } = 0.1f;

	[Property, Group( "Sea" )] public int Seed { get; set; } = 1;

	/// <summary>Metres covered by each cascade's tile, longest first. Unrelated sizes keep the tiles from lining up.</summary>
	[Property, Group( "Cascades" )] public Vector3 CascadeLengths { get; set; } = new( 500.0f, 97.0f, 19.3f );

	/// <summary>
	/// Where one cascade hands over to the next, in waves per tile of the finer one: each cascade keeps the waves too
	/// long for the next to hold this many of.
	/// </summary>
	[Property, Group( "Cascades" ), Range( 2, 16 )] public float BandSplit { get; set; } = 12.0f;

	/// <summary>Seconds after which the sea repeats exactly - long enough that nobody notices.</summary>
	[Property, Group( "Time" )] public float LoopPeriod { get; set; } = 200.0f;

	[Property, Group( "Time" ), Range( 0, 4 )] public float TimeScale { get; set; } = 1.0f;

	/// <summary>One tile size of the sea and the maps the FFT writes for it.</summary>
	public sealed class Cascade
	{
		/// <summary>Metres covered by the tile.</summary>
		public float TileLength { get; init; }

		/// <summary>Choppy x and y shift and height, in metres, and the shear dDx/dy in w.</summary>
		public Texture Displacement { get; init; }

		/// <summary>Slope of the height in x and y, and the stretch of the shift along x and y. Mip-mapped.</summary>
		public Texture Derivatives { get; init; }

		internal Texture SpectrumA { get; init; }
		internal Texture SpectrumB { get; init; }
	}

	/// <summary>One sea state, built: each cascade's initial spectrum, on the CPU and on the GPU.</summary>
	public sealed class SpectrumSet
	{
		public OceanSpectrum[] Spectra { get; init; }
		public OceanSea Sea { get; init; }
		internal Texture[] Textures { get; init; }

		internal static SpectrumSet Create( OceanSpectrum[] spectra, OceanSea sea )
		{
			var textures = new Texture[spectra.Length];

			for ( int i = 0; i < spectra.Length; i++ )
			{
				textures[i] = Texture.Create( Size, Size, ImageFormat.RGBA32323232F )
					.WithUAVBinding()
					.WithData<Vector4>( spectra[i].Texels.AsSpan() )
					.WithName( $"Ocean Initial Spectrum {i}" )
					.Finish();
			}

			return new SpectrumSet { Spectra = spectra, Sea = sea, Textures = textures };
		}

		/// <summary>The sea part way between two states, exactly as the blend shows it.</summary>
		internal static SpectrumSet Lerp( SpectrumSet a, SpectrumSet b, float t )
		{
			var spectra = new OceanSpectrum[a.Spectra.Length];

			for ( int i = 0; i < spectra.Length; i++ )
				spectra[i] = OceanSpectrum.Lerp( a.Spectra[i], b.Spectra[i], t );

			return Create( spectra, OceanSea.Lerp( a.Sea, b.Sea, t ) );
		}
	}

	/// <summary>The cascades, longest waves first.</summary>
	public Cascade[] Cascades { get; private set; } = Array.Empty<Cascade>();

	/// <summary>The two sea states being blended, and how far from the first to the second.</summary>
	public SpectrumSet From { get; private set; }
	public SpectrumSet To { get; private set; }
	public float Blend { get; private set; }

	/// <summary>The sea as it is now, between the two states being blended.</summary>
	public OceanSea Sea => From is null ? StartSea : OceanSea.Lerp( From.Sea, To.Sea, Blend );

	/// <summary>The time, wrapped to the loop period, the maps currently show.</summary>
	public float OceanTime { get; private set; }

	/// <summary>The sea replayed on the CPU, for physics: see <see cref="OceanWaves"/>.</summary>
	public OceanWaves Waves { get; private set; }

	/// <summary>The first active ocean in the scene: what floating things float on.</summary>
	public static Ocean Current { get; private set; }

	/// <summary>
	/// The sea's time at this very moment, wrapped to the loop period: unlike <see cref="OceanTime"/>, which steps
	/// once a frame, it moves on between the physics steps of a frame too.
	/// </summary>
	public float PhysicsTime => (float)((m_Time + (Time.Now - m_TimeTakenAt) * TimeScale) % LoopPeriod + LoopPeriod) % LoopPeriod;

	/// <summary>Which way the wind blows, as a unit vector across the sea.</summary>
	public Vector3 WindDirection => Rotation.FromYaw( WindAngle ).Forward;

	/// <summary>The height of the sea's surface over a point in the world, in world units. The mean level is z = 0.</summary>
	public float HeightAt( Vector3 world )
	{
		if ( Waves is null )
			return 0.0f;

		return Waves.HeightAt( ToMetres( world ), PhysicsTime, Sea.Choppiness ) * UnitsPerMetre;
	}

	/// <summary>
	/// Where the water that lies over a point at rest has been carried by the waves, in world units: sideways along
	/// the orbit and up or down.
	/// </summary>
	public Vector3 DisplacementAt( Vector3 world )
	{
		if ( Waves is null )
			return Vector3.Zero;

		return Waves.Displacement( ToMetres( world ), PhysicsTime, Sea.Choppiness ) * UnitsPerMetre;
	}

	/// <summary>
	/// How fast the water at the surface near a point is moving, in world units per second: the orbit each bit of
	/// water swings round as a wave passes - forward on the crest, back in the trough - which is what carries a
	/// floating thing along. From the swell's displacement a moment apart.
	/// </summary>
	public Vector3 WaterVelocityAt( Vector3 world )
	{
		if ( Waves is null )
			return Vector3.Zero;

		// Without the choppiness: that pinches the crests for the eye, and would carry the water round its orbit nearly
		// twice as fast as a real sea does.
		const float dt = 0.05f;
		var metres = ToMetres( world );
		float t = PhysicsTime;
		var now = Waves.Displacement( metres, t, 1.0f );
		var next = Waves.Displacement( metres, t + dt, 1.0f );
		return (next - now) / dt * UnitsPerMetre;
	}

	/// <summary>
	/// Moves the sea to another state over some seconds, from wherever it is now - even part way through another
	/// blend. Zero seconds switches at once.
	/// </summary>
	public void BlendTo( OceanPreset preset, float seconds )
	{
		if ( !m_Driven )
			m_From = StartSea;

		// Part way through a blend: what shows now becomes the start of the new one, so nothing jumps.
		if ( From is not null && Blend > 0.0f )
		{
			var snapshot = SpectrumSet.Lerp( From, To, Blend );
			DropSnapshot();
			m_Snapshot = snapshot;
			m_From = snapshot.Sea;
		}

		m_To = preset?.Sea ?? OceanSea.Default;
		m_Blend = 0.0f;
		m_Rate = seconds > 0.0f ? 1.0f / seconds : float.PositiveInfinity;
		m_Driven = true;
	}

	/// <summary>
	/// Holds the sea at a fixed point between two states: 0 is all of the first, 1 all of the second. For a slider or
	/// anything else that drives the blend itself every frame.
	/// </summary>
	public void SetBlend( OceanPreset from, OceanPreset to, float blend )
	{
		DropSnapshot();
		m_From = from?.Sea ?? OceanSea.Default;
		m_To = to?.Sea ?? OceanSea.Default;
		m_Blend = blend.Clamp( 0.0f, 1.0f );
		m_Rate = 0.0f;
		m_Driven = true;
	}

	/// <summary>The tile length of a cascade, in metres.</summary>
	public float LengthOf( int cascade ) => cascade switch { 0 => CascadeLengths.x, 1 => CascadeLengths.y, _ => CascadeLengths.z };

	/// <summary>Hands the maps and their tile sizes to anything drawn or dispatched with the ocean's shaders.</summary>
	public void Bind( RenderAttributes attributes )
	{
		for ( int i = 0; i < Cascades.Length; i++ )
		{
			attributes.Set( $"OceanDisplacement{i}", Cascades[i].Displacement );
			attributes.Set( $"OceanDerivatives{i}", Cascades[i].Derivatives );
		}

		attributes.Set( "OceanTileLengths", CascadeLengths * UnitsPerMetre );
		attributes.Set( "OceanUnitsPerMetre", UnitsPerMetre );
	}

	/// <summary>The same, for a command list.</summary>
	public void Bind( CommandList commands )
	{
		for ( int i = 0; i < Cascades.Length; i++ )
		{
			commands.Attributes.Set( $"OceanDisplacement{i}", Cascades[i].Displacement );
			commands.Attributes.Set( $"OceanDerivatives{i}", Cascades[i].Derivatives );
		}

		commands.Attributes.Set( "OceanTileLengths", CascadeLengths * UnitsPerMetre );
		commands.Attributes.Set( "OceanUnitsPerMetre", UnitsPerMetre );
	}

	private ComputeShader m_TimeShader;
	private ComputeShader m_FftShader;
	private ComputeShader m_AssembleShader;
	private CameraCommands m_Commands;
	private double m_Time;
	private double m_TimeTakenAt;

	// Every sea state built so far, and the CPU replica for every pair blended so far.
	private readonly Dictionary<OceanSea, SpectrumSet> m_Sets = new();
	private readonly Dictionary<(SpectrumSet, SpectrumSet), OceanWaves> m_Replicas = new();
	private int m_LayoutHash;

	// What BlendTo or SetBlend asked for; until either is called, the preset is the one sea state.
	private bool m_Driven;
	private OceanSea m_From, m_To;
	private float m_Blend, m_Rate;

	// The sea as it stood when a blend was interrupted: built from the two it was between, not from settings.
	private SpectrumSet m_Snapshot;

	private static readonly ProfilingSampler s_Profile = new( "Ocean FFT" );

	private OceanSea StartSea => Preset?.Sea ?? OceanSea.Default;

	private static Vector2 ToMetres( Vector3 world ) => new Vector2( world.x, world.y ) / UnitsPerMetre;

	private void DropSnapshot()
	{
		if ( m_Snapshot is null )
			return;

		foreach ( var key in m_Replicas.Keys.Where( k => k.Item1 == m_Snapshot ).ToList() )
			m_Replicas.Remove( key );

		m_Snapshot = null;
	}

	protected override void OnEnabled()
	{
		m_TimeShader = new ComputeShader( "ocean/shaders/ocean_timespectrum_cs.shader" );
		m_FftShader = new ComputeShader( "ocean/shaders/ocean_fft_cs.shader" );
		m_AssembleShader = new ComputeShader( "ocean/shaders/ocean_assemble_cs.shader" );

		m_Commands = new CameraCommands( "Ocean FFT", Stage.AfterUI, 0 );
		m_LayoutHash = 0;
		Refresh();
		Current ??= this;
	}

	protected override void OnDisabled()
	{
		if ( Current == this )
			Current = null;

		m_Commands?.Dispose();
		m_Commands = null;
	}

	private static Texture CreateTarget( string name, ImageFormat format, bool mips = false )
	{
		var builder = Texture.Create( Size, Size, format ).WithUAVBinding().WithName( name );
		return (mips ? builder.WithMips() : builder).Finish();
	}

	/// <summary>Brings the cascades, the two sea states and the CPU replica up to date with the settings.</summary>
	private void Refresh()
	{
		// The tiles and the spectrum's shape set every sea state: when they change, everything built so far is stale.
		int layout = HashCode.Combine( CascadeLengths, BandSplit, Seed, LoopPeriod, WindAngle, Directionality, SmallWaveCutoff );

		if ( layout != m_LayoutHash || Cascades.Length != CascadeCount )
		{
			m_LayoutHash = layout;
			m_Sets.Clear();
			m_Replicas.Clear();

			// An interrupted blend's snapshot was built from the old shape: start again from where it stood.
			if ( m_Snapshot is not null )
			{
				m_From = m_Snapshot.Sea;
				m_Snapshot = null;
			}

			var cascades = new Cascade[CascadeCount];
			for ( int i = 0; i < CascadeCount; i++ )
			{
				var old = i < Cascades.Length ? Cascades[i] : null;
				cascades[i] = new Cascade
				{
					TileLength = LengthOf( i ),
					SpectrumA = old?.SpectrumA ?? CreateTarget( $"Ocean Spectrum A {i}", ImageFormat.RGBA32323232F ),
					SpectrumB = old?.SpectrumB ?? CreateTarget( $"Ocean Spectrum B {i}", ImageFormat.RGBA32323232F ),
					// Half floats: a few millimetres of precision on waves of metres, half the bandwidth to read them.
					Displacement = old?.Displacement ?? CreateTarget( $"Ocean Displacement {i}", ImageFormat.RGBA16161616F ),
					Derivatives = old?.Derivatives ?? CreateTarget( $"Ocean Derivatives {i}", ImageFormat.RGBA16161616F, mips: true ),
				};
			}

			Cascades = cascades;
		}

		if ( !m_Driven )
			m_From = m_To = StartSea;

		From = m_Snapshot ?? SetFor( m_From );
		To = SetFor( m_To );
		Blend = From == To ? 0.0f : m_Blend;

		if ( !m_Replicas.TryGetValue( (From, To), out var replica ) )
			m_Replicas[(From, To)] = replica = new OceanWaves( From.Spectra[0], To.Spectra[0], LoopPeriod );

		Waves = replica;
		Waves.Blend = Blend;
	}

	/// <summary>The built sea state for some settings, building it the first time.</summary>
	private SpectrumSet SetFor( OceanSea sea )
	{
		if ( m_Sets.TryGetValue( sea, out var set ) )
			return set;

		var settings = new OceanSpectrum.Settings( sea.WindSpeed, WindAngle, Directionality, SmallWaveCutoff, Seed );
		var spectra = new OceanSpectrum[CascadeCount];
		double variance = 0.0;

		for ( int i = 0; i < CascadeCount; i++ )
		{
			// Each cascade takes the waves too long for the next one to hold BandSplit of; the last takes the rest.
			float minK = i == 0 ? 0.0f : 2.0f * MathF.PI / LengthOf( i ) * BandSplit;
			float maxK = i == CascadeCount - 1 ? float.MaxValue : 2.0f * MathF.PI / LengthOf( i + 1 ) * BandSplit;

			spectra[i] = new OceanSpectrum( Size, LengthOf( i ), minK, maxK, i, settings );
			variance += spectra[i].RawVariance;
		}

		float scale = OceanSpectrum.ScaleFor( sea.SignificantHeight, variance );

		for ( int i = 0; i < CascadeCount; i++ )
			spectra[i].Scale( scale );

		set = SpectrumSet.Create( spectra, sea );
		m_Sets[sea] = set;
		return set;
	}

	protected override void OnUpdate()
	{
		if ( m_Commands is null )
			return;

		AdvanceBlend();
		Refresh();
		m_Commands.Follow( Scene );

		// The maps are built at the end of this frame, for the moment the next one will show.
		m_Time += Time.Delta * TimeScale;
		m_TimeTakenAt = Time.Now;
		OceanTime = (float)((m_Time + Time.Delta * TimeScale) % LoopPeriod);
		Current ??= this;

		// The CPU replica's next grid gets started on a worker thread now, so it's ready before the physics needs it.
		Waves.Prefetch( PhysicsTime );

		Record();
	}

	private void AdvanceBlend()
	{
		if ( m_Rate <= 0.0f )
			return;

		m_Blend = float.IsInfinity( m_Rate ) ? 1.0f : m_Blend.Approach( 1.0f, Time.Delta * m_Rate );

		if ( m_Blend < 1.0f )
			return;

		// Arrived: the target is now the one sea state.
		m_From = m_To;
		DropSnapshot();
		m_Blend = 0.0f;
		m_Rate = 0.0f;
	}

	private void Record()
	{
		var cl = m_Commands.List;
		cl.Reset();

		using ( cl.ProfileScope( s_Profile ) )
		{
			cl.Attributes.Set( "OceanSize", Size );
			cl.Attributes.Set( "OceanTime", OceanTime );
			cl.Attributes.Set( "LoopPeriod", LoopPeriod );
			cl.Attributes.Set( "Choppiness", Sea.Choppiness );
			cl.Attributes.Set( "SpectrumBlend", Blend );

			for ( int i = 0; i < Cascades.Length; i++ )
			{
				var cascade = Cascades[i];

				// 1. The spectrum at this time, blended between the two sea states.
				cl.Attributes.Set( "InitialSpectrum", From.Textures[i] );
				cl.Attributes.Set( "InitialSpectrumB", To.Textures[i] );
				cl.Attributes.Set( "SpectrumA", cascade.SpectrumA );
				cl.Attributes.Set( "SpectrumB", cascade.SpectrumB );
				cl.Attributes.Set( "TileLength", cascade.TileLength );
				cl.DispatchCompute( m_TimeShader, Size, Size, 1 );
				cl.UavBarrier( cascade.SpectrumA );
				cl.UavBarrier( cascade.SpectrumB );

				// 2. Inverse FFT along x, then along y: one thread group per line.
				for ( int direction = 0; direction < 2; direction++ )
				{
					cl.Attributes.Set( "Direction", direction );
					cl.DispatchCompute( m_FftShader, Size, Size, 1 );
					cl.UavBarrier( cascade.SpectrumA );
					cl.UavBarrier( cascade.SpectrumB );
				}

				// 3. The maps the surface reads, the slopes mip-mapped so distant waves shade without shimmering.
				cl.Attributes.Set( "Displacement", cascade.Displacement );
				cl.Attributes.Set( "Derivatives", cascade.Derivatives );
				cl.DispatchCompute( m_AssembleShader, Size, Size, 1 );
				cl.UavBarrier( cascade.Displacement );
				cl.UavBarrier( cascade.Derivatives );
				cl.GenerateMipMaps( cascade.Derivatives, Graphics.DownsampleMethod.Box );
			}
		}
	}
}
