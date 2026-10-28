using System;
using System.Collections.Generic;
using System.Linq;

namespace OceanLib;

/// <summary>
/// Keeps the sea out of a hull, to the shape of its collision mesh: <see cref="OceanSurface"/> doesn't draw the ocean
/// anywhere inside it, so an open boat sitting low isn't shown full of sea. Read once from the model, handed to the GPU
/// in the hull's own space; only where the hull is changes from frame to frame.
///
/// After the Water Tool's HullWaterExclusionVolume, with one change: a convex hull - what s&box's collision usually is -
/// is kept as its face planes, and a point is inside when it is behind every one of them, which the shader can give up
/// on at the first face it's in front of. Only meshes that aren't convex fall back to counting how many triangles a
/// ray from the point crosses. Either way a box round the hull turns away everything not near it first.
/// </summary>
[Title( "Ocean Hull Mask" ), Category( "Ocean" ), Icon( "format_color_reset" )]
public sealed class OceanHullMask : Component, Component.ExecuteInEditor
{
	/// <summary>The model whose collision shapes the mask. Empty takes the ModelRenderer's.</summary>
	[Property] public Model CustomModel { get; set; }

	/// <summary>The convex parts' face planes in model space: (normal, offset), inside where dot(normal, p) + offset &lt;= 0.</summary>
	public IReadOnlyList<(int Start, int Count)> ConvexParts => m_ConvexParts;

	public IReadOnlyList<Vector4> Planes => m_Planes;

	/// <summary>The rest of the collision, as triangles in model space, three points each.</summary>
	public IReadOnlyList<Vector3> Triangles => m_Triangles;

	/// <summary>The box round it all, in model space.</summary>
	public BBox LocalBounds { get; private set; }

	public bool IsEmpty => m_Planes.Count == 0 && m_Triangles.Count == 0;

	private readonly List<(int, int)> m_ConvexParts = new();
	private readonly List<Vector4> m_Planes = new();
	private readonly List<Vector3> m_Triangles = new();
	private Model m_Built;

	private Model ResolveModel() => CustomModel ?? Components.Get<ModelRenderer>()?.Model;

	protected override void OnEnabled() => Rebuild();

	protected override void OnUpdate()
	{
		if ( ResolveModel() != m_Built )
			Rebuild();
	}

	private void Rebuild()
	{
		var model = ResolveModel();
		m_Built = model;
		m_ConvexParts.Clear();
		m_Planes.Clear();
		m_Triangles.Clear();
		LocalBounds = default;

		if ( model?.Physics is null )
			return;

		var min = new Vector3( float.MaxValue );
		var max = new Vector3( float.MinValue );

		void Grow( Vector3 p )
		{
			min = Vector3.Min( min, p );
			max = Vector3.Max( max, p );
		}

		foreach ( var part in model.Physics.Parts )
		{
			foreach ( var hull in part.Hulls )
			{
				var points = hull.GetPoints()?.ToArray();

				if ( points is null || points.Length < 4 )
					continue;

				int start = m_Planes.Count;
				m_Planes.AddRange( FacePlanes( points ) );
				m_ConvexParts.Add( (start, m_Planes.Count - start) );

				foreach ( var p in points )
					Grow( p );
			}

			foreach ( var mesh in part.Meshes )
			{
				foreach ( var t in mesh.GetTriangles() )
				{
					m_Triangles.Add( t.A ); m_Triangles.Add( t.B ); m_Triangles.Add( t.C );
					Grow( t.A ); Grow( t.B ); Grow( t.C );
				}
			}
		}

		if ( !IsEmpty )
			LocalBounds = new BBox( min, max );
	}

	/// <summary>
	/// The faces of a convex hull as planes, outward-facing, each once: a plane through three of its points with every
	/// other point on one side of it is a face. Nearly coplanar duplicates are merged.
	/// </summary>
	private static List<Vector4> FacePlanes( Vector3[] points )
	{
		var planes = new List<Vector4>();
		var centroid = points.Aggregate( Vector3.Zero, ( a, b ) => a + b ) / points.Length;
		int n = points.Length;

		for ( int i = 0; i < n; i++ )
		for ( int j = i + 1; j < n; j++ )
		for ( int k = j + 1; k < n; k++ )
		{
			var normal = Vector3.Cross( points[j] - points[i], points[k] - points[i] );

			if ( normal.LengthSquared < 1e-8f )
				continue;

			normal = normal.Normal;

			// Outward: away from the middle.
			if ( Vector3.Dot( normal, centroid - points[i] ) > 0.0f )
				normal = -normal;

			float offset = -Vector3.Dot( normal, points[i] );
			bool face = true;

			for ( int m = 0; m < n && face; m++ )
				face = Vector3.Dot( normal, points[m] ) + offset <= 0.01f;

			if ( !face )
				continue;

			bool known = planes.Any( p => Vector3.Dot( new Vector3( p.x, p.y, p.z ), normal ) > 0.9999f && MathF.Abs( p.w - offset ) < 0.01f );

			if ( !known )
				planes.Add( new Vector4( normal, offset ) );
		}

		return planes;
	}

	/// <summary>
	/// The world-to-model transform's three rows, for dot( row, float4( position, 1 ) ) in the shader. Matches
	/// WorldTransform.PointToLocal: forward is model x, left model y, up model z.
	/// </summary>
	public (Vector4, Vector4, Vector4) WorldToLocalRows()
	{
		var rotation = WorldRotation;
		var position = WorldPosition;
		var scale = WorldScale;
		var ax = rotation.Forward / scale.x;
		var ay = rotation.Left / scale.y;
		var az = rotation.Up / scale.z;

		return (
			new Vector4( ax, -Vector3.Dot( rotation.Forward, position ) / scale.x ),
			new Vector4( ay, -Vector3.Dot( rotation.Left, position ) / scale.y ),
			new Vector4( az, -Vector3.Dot( rotation.Up, position ) / scale.z ) );
	}

	protected override void DrawGizmos()
	{
		if ( !Gizmo.IsSelected || IsEmpty )
			return;

		Gizmo.Draw.Color = Color.Cyan.WithAlpha( 0.5f );
		Gizmo.Draw.LineBBox( LocalBounds );
	}
}
