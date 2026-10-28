using System;
using System.Text;
using Sandbox.Rendering;

namespace OceanLib;

/// <summary>
/// Shows what the ocean is doing, over the game view: each cascade's tile from above - its heights, and where it folds
/// into breaking crests - the foam window round the camera, and the numbers behind the sea (the state and the blend,
/// the CPU replica's work, the spray). For tuning and checking; leave it off in a game.
/// </summary>
[Title( "Ocean Debug View" ), Category( "Ocean" ), Icon( "bug_report" )]
public sealed class OceanDebugView : Component
{
	/// <summary>The sea to show. Empty takes the one on this object, else the scene's.</summary>
	[Property] public Ocean Ocean { get; set; }

	/// <summary>Each cascade's heights, longest waves first.</summary>
	[Property] public bool ShowHeights { get; set; } = true;

	/// <summary>Each cascade's folding: white where the surface bunches up, red where it folds over and breaks.</summary>
	[Property] public bool ShowFolding { get; set; } = true;

	/// <summary>The persistent foam window, centred on the camera.</summary>
	[Property] public bool ShowFoam { get; set; } = true;

	[Property] public bool ShowStats { get; set; } = true;

	/// <summary>Pixels across each picture.</summary>
	[Property, Range( 64, 512 )] public int PanelSize { get; set; } = 180;

	/// <summary>Where the top left of the view sits on screen, in pixels.</summary>
	[Property] public Vector2 Position { get; set; } = new( 16, 16 );

	private const int Size = 256;
	private const int Panels = Ocean.CascadeCount * 2 + 1;

	private ComputeShader m_Shader;
	private CameraCommands m_Commands;
	private readonly Texture[] m_Panels = new Texture[Panels];
	private readonly float[] m_Ranges = new float[Ocean.CascadeCount];
	private readonly Dictionary<Ocean.SpectrumSet, float[]> m_Deviations = new();

	private static readonly ProfilingSampler s_Profile = new( "Ocean Debug View" );

	private Ocean Sea => Ocean.IsValid() ? Ocean : Components.Get<Ocean>() ?? Ocean.Current;

	protected override void OnEnabled()
	{
		m_Shader = new ComputeShader( "ocean/shaders/ocean_debug_cs.shader" );
		// After the FFT and the foam, on the same stage.
		m_Commands = new CameraCommands( "Ocean Debug View", Stage.AfterUI, 2 );

		for ( int i = 0; i < Panels; i++ )
			m_Panels[i] = Texture.Create( Size, Size, ImageFormat.RGBA8888 ).WithUAVBinding().WithName( $"Ocean Debug {i}" ).Finish();
	}

	protected override void OnDisabled()
	{
		m_Commands?.Dispose();
		m_Commands = null;
	}

	protected override void OnUpdate()
	{
		var ocean = Sea;

		if ( m_Commands is null || !ocean.IsValid() || ocean.Cascades.Length == 0 )
			return;

		m_Commands.Follow( Scene );
		Record( ocean );
		Draw( ocean );
	}

	/// <summary>
	/// Two standard deviations of each cascade's height as the sea is now: what its picture shows as full white or full
	/// blue.
	/// </summary>
	private void UpdateRanges( Ocean ocean )
	{
		var from = Deviations( ocean.From );
		var to = Deviations( ocean.To );

		for ( int i = 0; i < m_Ranges.Length; i++ )
			m_Ranges[i] = 2.0f * MathX.Lerp( from[i], to[i], ocean.Blend );
	}

	private float[] Deviations( Ocean.SpectrumSet set )
	{
		if ( m_Deviations.TryGetValue( set, out var deviations ) )
			return deviations;

		deviations = new float[Ocean.CascadeCount];

		for ( int i = 0; i < deviations.Length; i++ )
		{
			double sum = 0.0;
			foreach ( var t in set.Spectra[i].Texels )
				sum += t.x * t.x + t.y * t.y;

			deviations[i] = MathF.Sqrt( (float)(2.0 * sum) );
		}

		return m_Deviations[set] = deviations;
	}

	private void Record( Ocean ocean )
	{
		UpdateRanges( ocean );

		var cl = m_Commands.List;
		cl.Reset();

		using ( cl.ProfileScope( s_Profile ) )
		{
			cl.Attributes.Set( "DebugSize", Size );

			for ( int i = 0; i < Ocean.CascadeCount; i++ )
			{
				cl.Attributes.Set( "DebugDisplacement", ocean.Cascades[i].Displacement );
				cl.Attributes.Set( "DebugDerivatives", ocean.Cascades[i].Derivatives );
				cl.Attributes.Set( "DebugRange", m_Ranges[i] );

				Panel( cl, i, 0.0f );
				Panel( cl, Ocean.CascadeCount + i, 1.0f );
			}

			var foam = Scene.GetAllComponents<OceanFoam>().FirstOrDefault();

			if ( foam.IsValid() && foam.Current is not null )
			{
				cl.Attributes.Set( "DebugFoam", foam.Current );
				Panel( cl, Panels - 1, 2.0f );
			}
		}
	}

	private void Panel( CommandList cl, int index, float mode )
	{
		cl.Attributes.Set( "DebugMode", mode );
		cl.Attributes.Set( "DebugOut", m_Panels[index] );
		cl.DispatchCompute( m_Shader, Size, Size, 1 );
		cl.UavBarrier( m_Panels[index] );
	}

	private void Draw( Ocean ocean )
	{
		var camera = m_Commands.Camera;

		if ( !camera.IsValid() )
			return;

		var hud = camera.BeginHud();

		try
		{
			DrawPanels( ref hud, ocean );
		}
		finally
		{
			hud.Dispose();
		}
	}

	private void DrawPanels( ref Painter hud, Ocean ocean )
	{
		int row = 0;
		string Tile( int i ) => $"{ocean.LengthOf( i ):0.#} m";

		if ( ShowHeights )
			Row( ref hud, row++, "Heights", 0, Ocean.CascadeCount, i => $"{Tile( i )}  ±{m_Ranges[i]:0.##} m" );

		if ( ShowFolding )
			Row( ref hud, row++, "Folding (Jacobian)", Ocean.CascadeCount, Ocean.CascadeCount, Tile );

		var foam = Scene.GetAllComponents<OceanFoam>().FirstOrDefault();

		if ( ShowFoam && foam.IsValid() )
			Row( ref hud, row++, "Foam window", Panels - 1, 1, _ => $"{foam.Extent:0} m round the camera" );

		if ( ShowStats )
		{
			var sea = ocean.Sea;
			var waves = ocean.Waves;
			var text = new StringBuilder()
				.AppendLine( $"Sea: wind {sea.WindSpeed:0.#} m/s · Hs {sea.SignificantHeight:0.##} m · choppiness {sea.Choppiness:0.##} · scatter {sea.Scatter:0.##} · crest foam {sea.CrestFoam:0.##}" )
				.AppendLine( $"Blend: {ocean.Blend:P0} from Hs {ocean.From.Sea.SignificantHeight:0.##} m to {ocean.To.Sea.SignificantHeight:0.##} m · time {ocean.OceanTime:0.0} / {ocean.LoopPeriod:0} s" )
				.AppendLine( waves is null ? "CPU replica: none" : $"CPU replica: {waves.Count} waves on {waves.GridSize}² · grids built {waves.BackgroundBuilds} in background, {waves.SynchronousBuilds} waiting" )
				.AppendLine( $"Spray: {OceanSpray.Current?.Count ?? 0} particles · foam sources: {Scene.GetAllComponents<OceanFoamSource>().Count()}" )
				.ToString();

			Text( ref hud, text, 13, new Rect( Position.x, RowTop( row ), 1200, 100 ) );
		}
	}

	private const float Gap = 8.0f;
	private const float TitleHeight = 18.0f;

	private float RowTop( int row ) => Position.y + row * (PanelSize + Gap + TitleHeight);

	private void Row( ref Painter hud, int row, string title, int first, int count, Func<int, string> label )
	{
		float y = RowTop( row );
		Text( ref hud, title, 13, new Rect( Position.x, y, 600, TitleHeight ) );

		for ( int i = 0; i < count; i++ )
		{
			var rect = new Rect( Position.x + i * (PanelSize + Gap), y + TitleHeight, PanelSize, PanelSize );
			hud.Texture( m_Panels[first + i], rect, null );
			Text( ref hud, label( i ), 11, rect.Shrink( 4.0f ) );
		}
	}

	private static void Text( ref Painter hud, string text, float size, Rect rect )
	{
		hud.TextStyle = TextStyle.Default with { FontSize = size, Color = Color.White, Alignment = TextFlag.LeftTop };
		hud.Text( text, rect );
	}
}