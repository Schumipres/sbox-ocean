using System;

namespace OceanLib;

/// <summary>
/// Makes foam where this object cuts the sea: a band round its outline at the waterline, stronger the faster it moves,
/// which <see cref="OceanFoam"/> carries on after it - a ship's bow wave and wake, the white water round a rock. The
/// outline is an ellipse in the object's own space; left at zero it is fitted to the model's bounds.
/// </summary>
[Title( "Ocean Foam Source" ), Category( "Ocean" ), Icon( "bubble_chart" )]
public sealed class OceanFoamSource : Component
{
	/// <summary>Half the length and half the width of the outline, in metres. Zero fits it to the model.</summary>
	[Property] public Vector2 HalfSize { get; set; }

	/// <summary>Foam added per second at a standstill, where the band is thickest.</summary>
	[Property, Range( 0, 10 )] public float Strength { get; set; } = 0.6f;

	/// <summary>Extra foam per second for each metre per second the object moves.</summary>
	[Property, Range( 0, 5 )] public float SpeedStrength { get; set; } = 0.6f;

	/// <summary>How far out from the outline the foam reaches, in metres.</summary>
	[Property, Range( 0.1f, 10 )] public float BandWidth { get; set; } = 1.5f;

	/// <summary>The outline in world units: half length and half width.</summary>
	public Vector2 WorldHalfSize { get; private set; }

	/// <summary>The lowest and highest point of the object, in world z: it only foams where the sea lies between them.</summary>
	public Vector2 HeightRange { get; private set; }

	/// <summary>How fast it moves across the sea, in metres per second.</summary>
	public float Speed { get; private set; }

	/// <summary>
	/// Where the object's upright axis crosses the mean sea level: the middle of its waterline. A leaning rock whose
	/// centre lies deep under water cuts the surface metres away from that centre.
	/// </summary>
	public Vector3 WaterlineCenter { get; private set; }

	private Vector3 m_LastPosition;
	private bool m_HasLast;

	protected override void OnUpdate()
	{
		var bounds = Components.Get<ModelRenderer>()?.Model?.Bounds ?? BBox.FromPositionAndSize( Vector3.Zero, 32 );
		var scale = WorldScale;
		var half = HalfSize.Length > 0.0f ? HalfSize * Ocean.UnitsPerMetre : new Vector2( bounds.Extents.x, bounds.Extents.y );
		WorldHalfSize = new Vector2( half.x * scale.x, half.y * scale.y );

		var world = bounds.Transform( WorldTransform );
		HeightRange = new Vector2( world.Mins.z, world.Maxs.z );

		var centre = WorldTransform.PointToWorld( bounds.Center );
		var up = WorldRotation.Up;
		WaterlineCenter = MathF.Abs( up.z ) > 0.2f ? centre - up * (centre.z / up.z) : centre;

		if ( m_HasLast && Time.Delta > 0.0f )
			Speed = MathX.Lerp( Speed, (WorldPosition - m_LastPosition).WithZ( 0 ).Length / Time.Delta / Ocean.UnitsPerMetre, 0.2f );

		m_LastPosition = WorldPosition;
		m_HasLast = true;
	}

	/// <summary>Foam per second this frame: the steady band, more for speed, and any splash still settling.</summary>
	public float Rate => Strength + SpeedStrength * Speed + m_Splash;

	private float m_Splash;

	/// <summary>A burst of white water round the hull - it slammed into a wave - fading over half a second.</summary>
	public void Splash( float amount )
	{
		m_Splash = MathF.Max( m_Splash, amount );
	}

	protected override void OnFixedUpdate()
	{
		m_Splash *= MathF.Exp( -Time.Delta / 0.5f );
	}

	protected override void DrawGizmos()
	{
		// The outline the foam forms round, at the waterline.
		var bounds = Components.Get<ModelRenderer>()?.Model?.Bounds ?? BBox.FromPositionAndSize( Vector3.Zero, 32 );
		var half = HalfSize.Length > 0.0f ? HalfSize * Ocean.UnitsPerMetre : new Vector2( bounds.Extents.x, bounds.Extents.y );
		var centre = bounds.Center.WithZ( 0 );

		Gizmo.Draw.Color = Color.White.WithAlpha( 0.6f );

		for ( int i = 0; i < 48; i++ )
		{
			float a = i / 48.0f * MathF.PI * 2.0f, b = (i + 1) / 48.0f * MathF.PI * 2.0f;
			Gizmo.Draw.Line( centre + new Vector3( MathF.Cos( a ) * half.x, MathF.Sin( a ) * half.y, 0 ),
				centre + new Vector3( MathF.Cos( b ) * half.x, MathF.Sin( b ) * half.y, 0 ) );
		}
	}
}
