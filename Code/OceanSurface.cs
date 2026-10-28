using System;
using System.Collections.Generic;

namespace OceanLib;

/// <summary>
/// Draws the ocean an <see cref="Ocean"/> simulates, as a clipmap: rings of grid around the camera, each twice as
/// coarse and twice as wide as the one inside, out to the horizon. Every ring sits on a grid fixed in the world - its
/// centre snapped to two of its cells - so the waves stay put under a moving camera instead of swimming. Two meshes do
/// all the rings: a full square for the innermost, and a square with a hole for the rest; the shader scales them,
/// pushes them into waves, folds each ring's edge onto the next ring's grid and clips the overlap between them.
/// </summary>
[Title( "Ocean Surface" ), Category( "Ocean" ), Icon( "grid_on" )]
public sealed class OceanSurface : Component
{
	/// <summary>The sea to draw. Empty takes the one on this object, else the scene's.</summary>
	[Property] public Ocean Ocean { get; set; }

	/// <summary>A material with the ocean surface shader. Empty uses the library's.</summary>
	[Property] public Material Material { get; set; }

	/// <summary>The persistent foam to draw. Empty takes the scene's first.</summary>
	[Property] public OceanFoam Foam { get; set; }

	/// <summary>Metres across one bubble of the foam's pattern.</summary>
	[Property, Group( "Look" ), Range( 0.1f, 5 )] public float FoamBubbleSize { get; set; } = 1.0f;

	[Property, Group( "Look" )] public Color FoamColor { get; set; } = new( 0.82f, 0.88f, 0.88f );

	/// <summary>Metres between vertices in the innermost ring.</summary>
	[Property, Group( "Rings" ), Range( 0.1f, 4 )] public float CellSize { get; set; } = 0.5f;

	/// <summary>Cells across each ring. A multiple of four, so every ring's edge falls on the next one's grid.</summary>
	[Property, Group( "Rings" ), Range( 32, 256 )] public int GridCells { get; set; } = 192;

	/// <summary>How many rings: each doubles the distance the sea reaches.</summary>
	[Property, Group( "Rings" ), Range( 1, 12 )] public int Levels { get; set; } = 8;

	/// <summary>The outer share of each ring over which its vertices fold onto the coarser grid outside.</summary>
	[Property, Group( "Rings" ), Range( 0.05f, 0.5f )] public float MorphBand { get; set; } = 0.2f;

	/// <summary>Metres from the camera by which each cascade's waves have faded out of the geometry, longest first.</summary>
	[Property, Group( "Rings" )] public Vector3 CascadeFade { get; set; } = new( 100000.0f, 1200.0f, 180.0f );

	/// <summary>Where the crests count as folding for the foam tint, as a Jacobian.</summary>
	[Property, Range( -1, 1 )] public float FoamThreshold { get; set; } = 0.4f;

	/// <summary>The sun the water scatters and glints. Empty takes the scene's first directional light.</summary>
	[Property, Group( "Look" )] public DirectionalLight Sun { get; set; }

	/// <summary>The colour of deep water, where no light comes back out of it.</summary>
	[Property, Group( "Look" )] public Color DeepColor { get; set; } = new( 0.006f, 0.04f, 0.075f );

	/// <summary>The turquoise of light scattering out of the crests.</summary>
	[Property, Group( "Look" )] public Color ScatterColor { get; set; } = new( 0.03f, 0.42f, 0.38f );

	/// <summary>How high a crest stands, in metres, before it glows its fullest.</summary>
	[Property, Group( "Look" ), Range( 0.2f, 10 )] public float ScatterHeight { get; set; } = 3.0f;

	/// <summary>How wide the sun's glare spreads over the waves: 0 a tight glint, 1 a broad band of light.</summary>
	[Property, Group( "Look" ), Range( 0, 1 )] public float SunSpread { get; set; } = 0.75f;

	/// <summary>The soft halo of light round the glare.</summary>
	[Property, Group( "Look" ), Range( 0, 2 )] public float SunGlow { get; set; } = 0.25f;

	/// <summary>Drops of rain landing on the water and the rings they spread: 0 dry, 1 a downpour. For weather to drive.</summary>
	[Property, Group( "Look" ), Range( 0, 2 )] public float RainRipples { get; set; } = 0.0f;

	/// <summary>How rough the surface is to the sky's reflection.</summary>
	[Property, Group( "Look" ), Range( 0, 0.5f )] public float Roughness { get; set; } = 0.06f;

	// The hull masks for the shader, one buffer: a fixed block of six rows per shape - three rows of its world-to-model
	// transform, (first plane, plane count, first triangle, triangle count), its box's low and high corners - then the
	// planes and the triangles' points they index.
	private const int MaxHullShapes = 16;
	private const int HullShapeRows = 6;
	private const int MaxHullData = 8192;
	private GpuBuffer<Vector4> m_HullMasks;
	private readonly Vector4[] m_HullMaskData = new Vector4[MaxHullShapes * HullShapeRows + MaxHullData];

	private readonly List<SceneObject> m_Rings = new();

	/// <summary>Packs every hull the sea is kept out of for the shader; returns how many shapes.</summary>
	private int GatherHullMasks()
	{
		int shapes = 0;
		int cursor = MaxHullShapes * HullShapeRows;

		void Add( (Vector4, Vector4, Vector4) rows, BBox box, int planeStart, int planes, int triStart, int tris )
		{
			int meta = shapes * HullShapeRows;
			m_HullMaskData[meta + 0] = rows.Item1;
			m_HullMaskData[meta + 1] = rows.Item2;
			m_HullMaskData[meta + 2] = rows.Item3;
			m_HullMaskData[meta + 3] = new Vector4( planeStart, planes, triStart, tris );
			m_HullMaskData[meta + 4] = new Vector4( box.Mins, 0 );
			m_HullMaskData[meta + 5] = new Vector4( box.Maxs, 0 );
			shapes++;
		}

		foreach ( var mask in Scene.GetAllComponents<OceanHullMask>() )
		{
			if ( mask.IsEmpty )
				continue;

			var rows = mask.WorldToLocalRows();

			// Each convex part is its own shape, tested against its planes.
			foreach ( var (start, count) in mask.ConvexParts )
			{
				if ( shapes >= MaxHullShapes || cursor + count > m_HullMaskData.Length )
					break;

				for ( int i = 0; i < count; i++ )
					m_HullMaskData[cursor + i] = mask.Planes[start + i];

				Add( rows, mask.LocalBounds, cursor, count, 0, 0 );
				cursor += count;
			}

			// The rest, if any, as triangles for the crossing count.
			int points = mask.Triangles.Count;

			if ( points > 0 && shapes < MaxHullShapes && cursor + points <= m_HullMaskData.Length )
			{
				for ( int i = 0; i < points; i++ )
					m_HullMaskData[cursor + i] = new Vector4( mask.Triangles[i], 0 );

				Add( rows, mask.LocalBounds, 0, 0, cursor, points / 3 );
				cursor += points;
			}
		}

		if ( shapes == 0 )
			return 0;

		m_HullMasks ??= new GpuBuffer<Vector4>( m_HullMaskData.Length, GpuBuffer.UsageFlags.Structured, "Ocean Hull Masks" );
		m_HullMasks.SetData( m_HullMaskData.AsSpan( 0, cursor ), 0 );
		return shapes;
	}
	private (float, int, int, Material) m_Built;
	private Material m_Material;

	/// <summary>Half the width of a ring, in world units.</summary>
	public float RingHalfWidth( int level ) => GridCells / 2 * LevelCellSize( level );

	private float LevelCellSize( int level ) => CellSize * Ocean.UnitsPerMetre * (1 << level);

	protected override void OnEnabled()
	{
		Build();
	}

	protected override void OnDisabled()
	{
		Clear();
	}

	private void Clear()
	{
		foreach ( var ring in m_Rings )
			ring.Delete();

		m_Rings.Clear();
	}

	private void Build()
	{
		Clear();

		int cells = Math.Max( 4, GridCells / 4 * 4 );
		m_Built = (CellSize, cells, Levels, Material);
		m_Material = Material ?? Material.Load( "ocean/materials/ocean_surface.vmat" );

		if ( m_Material is null )
			return;

		var full = BuildGrid( cells, 0 );

		// The finer ring inside is half this one's width, but its centre may sit up to a cell and a half off this
		// one's: the hole leaves two cells of margin on every side, and the shader clips what overlaps.
		var ring = BuildGrid( cells, cells / 2 - 4 );

		for ( int level = 0; level < Levels; level++ )
		{
			var so = new SceneObject( Scene.SceneWorld, level == 0 ? full : ring, global::Transform.Zero );
			so.Flags.CastShadows = false;
			m_Rings.Add( so );
		}
	}

	/// <summary>A square of cells about the origin, positions in cells, with a square hole of the given width in the middle.</summary>
	private Model BuildGrid( int cells, int hole )
	{
		var vertices = new List<Vertex>( (cells + 1) * (cells + 1) );
		var indices = new List<int>( cells * cells * 6 );
		int half = cells / 2;

		for ( int y = 0; y <= cells; y++ )
			for ( int x = 0; x <= cells; x++ )
				vertices.Add( new Vertex( new Vector3( x - half, y - half, 0 ), Vector3.Up, Vector3.Forward, Vector4.Zero ) );

		int holeLow = half - hole / 2;
		int holeHigh = half + hole / 2;

		for ( int y = 0; y < cells; y++ )
		{
			for ( int x = 0; x < cells; x++ )
			{
				if ( hole > 0 && x >= holeLow && x < holeHigh && y >= holeLow && y < holeHigh )
					continue;

				int a = y * (cells + 1) + x;
				int b = a + cells + 1;
				indices.AddRange( new[] { a, a + 1, b, a + 1, b + 1, b } );
			}
		}

		var mesh = new Mesh( m_Material );
		mesh.CreateVertexBuffer( vertices.Count, vertices );
		mesh.CreateIndexBuffer( indices.Count, indices );

		// The shader puts the grid wherever the camera is: never cull it.
		mesh.Bounds = new BBox( new Vector3( -1e7f, -1e7f, -1e5f ), new Vector3( 1e7f, 1e7f, 1e5f ) );
		return Model.Builder.AddMesh( mesh ).Create();
	}

	protected override void OnUpdate()
	{
		if ( m_Built != (CellSize, Math.Max( 4, GridCells / 4 * 4 ), Levels, Material) )
			Build();

		var ocean = Ocean.IsValid() ? Ocean : Components.Get<Ocean>() ?? Ocean.Current;

		if ( m_Rings.Count == 0 || !ocean.IsValid() || ocean.Cascades.Length == 0 )
			return;

		var camera = Scene.Camera;
		var eye = camera.IsValid() ? camera.WorldPosition : WorldPosition;
		int cells = m_Built.Item2;
		var fade = CascadeFade * Ocean.UnitsPerMetre;
		Vector2 innerCenter = default;
		float innerHalf = 0.0f;

		var sun = Sun.IsValid() ? Sun : Scene.GetAllComponents<DirectionalLight>().FirstOrDefault();
		var toSun = sun.IsValid() ? -sun.WorldRotation.Forward : new Vector3( 0.9f, 0.0f, 0.42f ).Normal;
		var sunColor = sun.IsValid() ? sun.LightColor : Color.White;
		var foam = Foam.IsValid() ? Foam : Scene.GetAllComponents<OceanFoam>().FirstOrDefault();
		int masks = GatherHullMasks();

		for ( int level = 0; level < m_Rings.Count; level++ )
		{
			var ring = m_Rings[level];
			float cell = LevelCellSize( level );

			// On the grid two of this ring's cells apart, so its even vertices are the next ring's vertices.
			float snap = cell * 2.0f;
			var center = new Vector2( MathF.Round( eye.x / snap ) * snap, MathF.Round( eye.y / snap ) * snap );

			var attributes = ring.Attributes;
			ocean.Bind( attributes );
			attributes.Set( "LevelCellSize", cell );
			attributes.Set( "LevelCenter", center );
			attributes.Set( "LevelGridHalf", cells / 2.0f );
			attributes.Set( "LevelMorphStart", cells / 2.0f * (1.0f - MorphBand) );
			attributes.Set( "LevelInnerCenter", innerCenter );
			attributes.Set( "LevelInnerHalf", innerHalf );

			// The nearest any of this ring's pixels can be is the edge of the ring inside, give or take its snapping;
			// a cascade whose shading has faded out by then isn't fetched for this ring at all.
			float nearest = MathF.Max( innerHalf - cell, 0.0f );
			attributes.Set( "LevelCascades", new Vector3(
				nearest < fade.x * 4.0f ? 1 : 0,
				nearest < fade.y * 4.0f ? 1 : 0,
				nearest < fade.z * 4.0f ? 1 : 0 ) );
			attributes.Set( "OceanFadeDistances", fade );
			attributes.Set( "OceanCameraXY", new Vector2( eye.x, eye.y ) );
			attributes.Set( "OceanFoamThreshold", FoamThreshold );
			attributes.Set( "OceanSunDirection", toSun );
			attributes.Set( "OceanSunColor", new Vector3( sunColor.r, sunColor.g, sunColor.b ) );
			attributes.Set( "OceanDeepColor", new Vector3( DeepColor.r, DeepColor.g, DeepColor.b ) );
			attributes.Set( "OceanScatterColor", new Vector3( ScatterColor.r, ScatterColor.g, ScatterColor.b ) );
			attributes.Set( "OceanScatterStrength", ocean.Sea.Scatter );
			attributes.Set( "OceanScatterHeight", ScatterHeight );
			attributes.Set( "OceanSunSpread", SunSpread );
			attributes.Set( "OceanSunGlow", SunGlow );
			attributes.Set( "OceanRoughness", Roughness );
			attributes.Set( "OceanRainRipples", RainRipples );
			attributes.Set( "OceanFoamBubbleScale", FoamBubbleSize );
			attributes.Set( "OceanFoamColor", new Vector3( FoamColor.r, FoamColor.g, FoamColor.b ) );

			attributes.Set( "OceanHullMaskCount", (float)masks );
			if ( masks > 0 )
				attributes.Set( "OceanHullMasks", m_HullMasks );

			if ( foam.IsValid() && foam.Current is not null )
				foam.Bind( attributes );
			else
				attributes.Set( "OceanFoamExtent", 0.0f );

			innerCenter = center;
			innerHalf = cells / 2.0f * cell;
		}
	}
}
