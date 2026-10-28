using System;
using Sandbox.Rendering;

namespace OceanLib;

/// <summary>
/// Persistent foam for the sea an <see cref="Ocean"/> simulates: a window of foam centred on the camera, carried over
/// from frame to frame in a compute shader. Each frame the foam already there fades and blurs into its neighbours -
/// dispersing - while folding crests (at the sea state's <see cref="OceanSea.CrestFoam"/> rate) and every
/// <see cref="OceanFoamSource"/> (hulls, rocks) add new foam, so whitecaps linger after the wave has passed and a
/// moving ship leaves a wake. The window steps with the camera by whole texels, so the foam stays put on the sea.
/// <see cref="OceanSurface"/> draws it; beyond the window, the surface falls back on the crests' own foam.
/// </summary>
[Title( "Ocean Foam" ), Category( "Ocean" ), Icon( "bubble_chart" )]
public sealed class OceanFoam : Component
{
	/// <summary>The sea to foam. Empty takes the one on this object, else the scene's.</summary>
	[Property] public Ocean Ocean { get; set; }

	/// <summary>Texels per side of the window.</summary>
	[Property, Group( "Window" )] public int Resolution { get; set; } = 1024;

	/// <summary>Metres the window covers.</summary>
	[Property, Group( "Window" ), Range( 50, 2000 )] public float Extent { get; set; } = 512.0f;

	/// <summary>Seconds for foam to fade to a third.</summary>
	[Property, Group( "Behaviour" ), Range( 0.2f, 30 )] public float Lifetime { get; set; } = 4.0f;

	/// <summary>How fast foam spreads into its neighbours: soft, dispersing edges.</summary>
	[Property, Group( "Behaviour" ), Range( 0, 1 )] public float Spread { get; set; } = 0.15f;

	/// <summary>The Jacobian below which a crest counts as breaking.</summary>
	[Property, Group( "Behaviour" ), Range( -1, 1 )] public float CrestThreshold { get; set; } = 0.45f;

	/// <summary>
	/// The foam drawn this frame, one channel from 0 to 1: the window the last frame's pass wrote, since this frame's
	/// pass runs after the surface has drawn.
	/// </summary>
	public Texture Current { get; private set; }

	/// <summary>World xy of the corner of the window <see cref="Current"/> holds.</summary>
	public Vector2 Origin { get; private set; }

	/// <summary>World units the window covers.</summary>
	public float WorldExtent => m_Texel * m_Size;

	private const int MaxSources = 32;

	// Ping-pong: each pass reads one and writes the other; m_Flip says which was written last.
	private Texture m_A, m_B;
	private bool m_Flip;
	private int m_Size;
	private float m_Texel;
	private Vector2 m_LastOrigin;
	private bool m_HasLast;
	private ComputeShader m_Shader;
	private CameraCommands m_Commands;
	private GpuBuffer<Vector4> m_Sources;
	private readonly Vector4[] m_SourceData = new Vector4[MaxSources * 3];

	private static readonly ProfilingSampler s_Profile = new( "Ocean Foam" );

	private Ocean Sea => Ocean.IsValid() ? Ocean : Components.Get<Ocean>() ?? Ocean.Current;

	protected override void OnEnabled()
	{
		m_Shader = new ComputeShader( "ocean/shaders/ocean_foam_cs.shader" );
		// After the FFT, which is on the same stage at order 0.
		m_Commands = new CameraCommands( "Ocean Foam", Stage.AfterUI, 1 );
		m_Sources = new GpuBuffer<Vector4>( MaxSources * 3, GpuBuffer.UsageFlags.Structured, "Ocean Foam Sources" );
		Allocate();
	}

	protected override void OnDisabled()
	{
		m_Commands?.Dispose();
		m_Commands = null;
		m_Sources?.Dispose();
		m_Sources = null;
	}

	private void Allocate()
	{
		m_Size = Math.Clamp( Resolution / 8 * 8, 64, 4096 );
		m_A = Texture.Create( m_Size, m_Size, ImageFormat.R32F ).WithUAVBinding().WithName( "Ocean Foam A" ).Finish();
		m_B = Texture.Create( m_Size, m_Size, ImageFormat.R32F ).WithUAVBinding().WithName( "Ocean Foam B" ).Finish();
		m_HasLast = false;
	}

	protected override void OnUpdate()
	{
		var ocean = Sea;

		if ( m_Commands is null || !ocean.IsValid() || ocean.Cascades.Length == 0 )
			return;

		if ( m_Size != Math.Clamp( Resolution / 8 * 8, 64, 4096 ) )
			Allocate();

		m_Commands.Follow( Scene );

		// What the last pass wrote is what this frame shows.
		Current = m_Flip ? m_B : m_A;
		Origin = m_LastOrigin;

		// The next window, centred on the camera and snapped to whole texels.
		m_Texel = Extent * Ocean.UnitsPerMetre / m_Size;
		var camera = m_Commands.Camera;
		var eye = camera.IsValid() ? camera.WorldPosition : WorldPosition;
		float half = m_Size * 0.5f * m_Texel;
		var origin = new Vector2( MathF.Round( (eye.x - half) / m_Texel ) * m_Texel, MathF.Round( (eye.y - half) / m_Texel ) * m_Texel );

		var shift = m_HasLast
			? new Vector2( MathF.Round( (origin.x - m_LastOrigin.x) / m_Texel ), MathF.Round( (origin.y - m_LastOrigin.y) / m_Texel ) )
			: new Vector2( m_Size, m_Size );

		m_LastOrigin = origin;
		m_HasLast = true;

		int count = GatherSources();
		Record( ocean, origin, shift, count );
	}

	private int GatherSources()
	{
		int count = 0;

		foreach ( var source in Scene.GetAllComponents<OceanFoamSource>() )
		{
			if ( count >= MaxSources )
				break;

			var forward = source.WorldRotation.Forward.WithZ( 0 );
			forward = forward.Length > 1e-3f ? forward.Normal : Vector3.Forward;

			m_SourceData[count * 3 + 0] = new Vector4( source.WaterlineCenter, source.Rate );
			m_SourceData[count * 3 + 1] = new Vector4( forward.x, forward.y, source.WorldHalfSize.x, source.WorldHalfSize.y );
			m_SourceData[count * 3 + 2] = new Vector4( source.HeightRange.x, source.HeightRange.y, source.BandWidth * Ocean.UnitsPerMetre, 0 );
			count++;
		}

		if ( count > 0 )
			m_Sources.SetData( m_SourceData.AsSpan( 0, count * 3 ), 0 );

		return count;
	}

	private void Record( Ocean ocean, Vector2 origin, Vector2 shift, int sources )
	{
		var cl = m_Commands.List;
		cl.Reset();

		var previous = m_Flip ? m_B : m_A;
		m_Flip = !m_Flip;
		var next = m_Flip ? m_B : m_A;

		using ( cl.ProfileScope( s_Profile ) )
		{
			ocean.Bind( cl );

			cl.Attributes.Set( "FoamPrevious", previous );
			cl.Attributes.Set( "FoamNext", next );
			cl.Attributes.Set( "FoamSize", m_Size );
			cl.Attributes.Set( "FoamOrigin", origin );
			cl.Attributes.Set( "FoamTexel", m_Texel );
			cl.Attributes.Set( "FoamShift", shift );

			float delta = Time.Delta * ocean.TimeScale;
			cl.Attributes.Set( "FoamDecay", MathF.Exp( -delta / MathF.Max( Lifetime, 0.01f ) ) );
			cl.Attributes.Set( "FoamBlur", MathF.Min( Spread * delta * 30.0f, 0.9f ) );
			cl.Attributes.Set( "FoamThreshold", CrestThreshold );
			cl.Attributes.Set( "FoamCrestRate", ocean.Sea.CrestFoam );
			cl.Attributes.Set( "FoamDelta", delta );
			cl.Attributes.Set( "FoamSourceCount", sources );
			cl.Attributes.Set( "FoamSources", m_Sources );

			cl.DispatchCompute( m_Shader, m_Size, m_Size, 1 );
			cl.UavBarrier( next );
		}
	}

	/// <summary>Hands the foam window to anything drawn with the ocean surface shader.</summary>
	public void Bind( RenderAttributes attributes )
	{
		if ( Current is null )
			return;

		attributes.Set( "OceanFoamMap", Current );
		attributes.Set( "OceanFoamOrigin", Origin );
		attributes.Set( "OceanFoamExtent", WorldExtent );
	}
}
