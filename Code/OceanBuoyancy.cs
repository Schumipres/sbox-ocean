using System;

namespace OceanLib;

/// <summary>
/// Floats a rigidbody on the <see cref="Ocean"/> sea, through its CPU replica (<see cref="OceanWaves"/>). A grid of
/// points over the hull, each a spring pushing up by how far under the surface it is, so the hull tilts with the swell
/// under it; then everything the water does to a hull besides holding it up:
///
/// - Lift that knows its limits: it stiffens as the hull sinks towards its deck (the reserve a real hull has in its
///   sides), then stops growing once it is under - it can only push aside the water it fills - so no crest can fling
///   the boat out of the sea.
/// - Inertia: one slider from a cork that sits on the surface to a heavy hull that carries its fall into the water and
///   wallows up out of it, the waterline kept where it was.
/// - The water moving: every point is damped against the water's own motion, not the world's. Vertically that lets the
///   hull ride each wave rather than bounce off it; across the sea it is the wave's orbit carrying the hull - forward
///   on the crests, back in the troughs - and the surface drifting downwind, so a boat left alone in a storm is pushed
///   about and off downwind like the real thing.
/// - Drag shaped like a hull: a boat slips along its length but its keel and sides fight being pushed sideways, both
///   growing with the square of the speed, as water drag does; the turning is damped the same way.
/// - Slamming: a point driving into the water fast throws spray off the hull's side (see <see cref="OceanSpray"/>), and a
///   hard landing throws up a burst of foam round it (see <see cref="OceanFoamSource.Splash"/>).
/// </summary>
[Title( "Ocean Buoyancy" ), Category( "Ocean" ), Icon( "sailing" )]
public sealed class OceanBuoyancy : Component
{
	/// <summary>The sea to float on. Empty takes the scene's.</summary>
	[Property] public Ocean Ocean { get; set; }

	/// <summary>Points along the hull and across it.</summary>
	[Property, Group( "Points" ), Range( 1, 8 )] public int PointsAlong { get; set; } = 4;
	[Property, Group( "Points" ), Range( 1, 6 )] public int PointsAcross { get; set; } = 3;

	/// <summary>How far out to the hull's ends and sides the points reach, as a fraction of its half-size.</summary>
	[Property, Group( "Points" ), Range( 0.1f, 1 )] public Vector2 Spread { get; set; } = new( 0.8f, 0.75f );

	/// <summary>
	/// Raises the waterline the points answer to, in metres: higher floats the boat higher. The points sit at the
	/// middle of the hull's height.
	/// </summary>
	[Property, Group( "Float" ), Range( -3, 3 )] public float SurfaceOffset { get; set; } = 0.0f;

	/// <summary>Upward acceleration per unit of depth with no inertia: stiffer settles shallower and quicker.</summary>
	[Property, Group( "Float" ), Range( 5, 200 )] public float Stiffness { get; set; } = 40.0f;

	/// <summary>Damping of each point's rise and fall against the water's own, with no inertia.</summary>
	[Property, Group( "Float" ), Range( 0, 20 )] public float Damping { get; set; } = 6.0f;

	/// <summary>
	/// 0 floats like a cork on the surface; 1 is a heavy hull that ploughs into the water as it drops off a crest and
	/// rises back slowly. The waterline at rest stays the same.
	/// </summary>
	[Property, Group( "Float" ), Range( 0, 1 )] public float Inertia { get; set; } = 0.4f;

	/// <summary>How much harder the water pushes back as the hull sinks past its resting depth: its sides' reserve.</summary>
	[Property, Group( "Float" ), Range( 0, 4 )] public float Reserve { get; set; } = 1.0f;

	/// <summary>The most the water can lift the hull, as a multiple of its weight.</summary>
	[Property, Group( "Float" ), Range( 1, 10 )] public float MaxLift { get; set; } = 3.0f;

	/// <summary>The fastest the hull is made to follow the surface rising or falling, in metres per second.</summary>
	[Property, Group( "Float" ), Range( 0.5f, 10 )] public float MaxWaterRise { get; set; } = 3.0f;

	/// <summary>How hard the water's own movement carries the hull along: 0 ignores the waves' orbit and the drift.</summary>
	[Property, Group( "Water Movement" ), Range( 0, 1 )] public float WaveTransport { get; set; } = 1.0f;

	/// <summary>How fast the surface drifts downwind in a storm, in metres per second - less in a calmer sea.</summary>
	[Property, Group( "Water Movement" ), Range( 0, 3 )] public float WindDrift { get; set; } = 0.6f;

	/// <summary>Drag along the hull: low, a hull is shaped to slip through the water that way. Per metre.</summary>
	[Property, Group( "Drag" ), Range( 0, 2 )] public float LengthwiseDrag { get; set; } = 0.06f;

	/// <summary>Drag across the hull: high, the keel and sides fight being pushed sideways. Per metre.</summary>
	[Property, Group( "Drag" ), Range( 0, 5 )] public float SidewaysDrag { get; set; } = 0.8f;

	/// <summary>Drag of the water on the hull's turning, per second.</summary>
	[Property, Group( "Drag" ), Range( 0, 10 )] public float AngularDrag { get; set; } = 1.5f;

	/// <summary>How fast a point must hit the water, in metres per second, to throw up foam.</summary>
	[Property, Group( "Splash" ), Range( 0.5f, 10 )] public float SplashSpeed { get; set; } = 2.0f;

	/// <summary>How much foam a hard slam throws up.</summary>
	[Property, Group( "Splash" ), Range( 0, 10 )] public float SplashFoam { get; set; } = 3.0f;

	/// <summary>How fast a point must drive into the water, in metres per second, to throw spray (see <see cref="OceanSpray"/>).</summary>
	[Property, Group( "Splash" ), Range( 0.5f, 10 )] public float SprayImpact { get; set; } = 1.5f;

	/// <summary>Scales the spray this hull throws.</summary>
	[Property, Group( "Splash" ), Range( 0, 4 )] public float SprayAmount { get; set; } = 1.0f;

	/// <summary>How much of the hull's points are under water, 0 to 1.</summary>
	public float Submerged { get; private set; }

	/// <summary>The sea's height under the hull's centre, in world units.</summary>
	public float WaterHeight { get; private set; }

	/// <summary>The water's movement under the hull, in world units per second.</summary>
	public Vector3 WaterVelocity { get; private set; }

	private Rigidbody m_Body;
	private Collider m_Collider;
	private OceanFoamSource m_Foam;
	private float[] m_LastWater = Array.Empty<float>();
	private bool[] m_WasUnder = Array.Empty<bool>();
	private float[] m_SprayCooldown = Array.Empty<float>();
	private Vector3[] m_Points = Array.Empty<Vector3>();

	private const float UnitsPerMetre = Ocean.UnitsPerMetre;

	protected override void OnFixedUpdate()
	{
		if ( IsProxy )
			return;

		var ocean = Ocean.IsValid() ? Ocean : Ocean.Current;
		m_Body ??= Components.Get<Rigidbody>();
		m_Collider ??= Components.Get<Collider>();
		m_Foam ??= Components.Get<OceanFoamSource>();

		if ( !ocean.IsValid() || ocean.Waves is null || !m_Body.IsValid() || !m_Body.MotionEnabled || !m_Collider.IsValid() )
			return;

		LayOutPoints();

		float gravity = Scene.PhysicsWorld.Gravity.Length;
		float mass = m_Body.Mass;
		float share = mass / m_Points.Length;

		// Inertia softens the spring and the damper together, geometrically - halfway is halfway in feel - and lifts the
		// waterline by however much further the softer spring sags, so only the plunge changes, not where it floats.
		float stiffness = Stiffness * MathF.Pow( 0.2f, Inertia );
		float damping = Damping * MathF.Pow( 0.2f, Inertia );
		float rest = gravity / stiffness;
		float offset = SurfaceOffset * UnitsPerMetre + rest - gravity / Stiffness;
		float maxLift = MaxLift * gravity * share;

		WaterHeight = ocean.HeightAt( WorldPosition );

		// The water's own motion under the hull: the waves' orbit, plus the surface drifting downwind in a big sea.
		var orbit = ocean.WaterVelocityAt( WorldPosition ).WithZ( 0 );
		// Full drift in a storm of eight-metre waves.
		float drift = WindDrift * UnitsPerMetre * (ocean.Sea.SignificantHeight / 8.0f).Clamp( 0.0f, 1.5f );
		WaterVelocity = (orbit + ocean.WindDirection * drift) * WaveTransport;

		int under = 0;
		float slam = 0.0f;

		for ( int i = 0; i < m_Points.Length; i++ )
		{
			var point = WorldTransform.PointToWorld( m_Points[i] );
			float water = ocean.HeightAt( point );

			// How fast the sea is rising or falling right here - held to what a real sea's surface does. A sharp crest
			// sweeping under a point makes the height there jump faster than any water moves, and following that
			// would throw the hull clear of the crest.
			float maxRise = MaxWaterRise * UnitsPerMetre;
			float waterSpeed = float.IsNaN( m_LastWater[i] ) ? 0.0f : ((water - m_LastWater[i]) / Time.Delta).Clamp( -maxRise, maxRise );
			m_LastWater[i] = water;

			float depth = water + offset - point.z;
			float pointSpeed = m_Body.GetVelocityAtPoint( point ).z;

			if ( depth <= 0.0f )
			{
				m_WasUnder[i] = false;
				continue;
			}

			under++;

			// Hitting the water: how fast this point is going down into it, relative to the water's own rise.
			float impact = (waterSpeed - pointSpeed) / UnitsPerMetre;

			if ( !m_WasUnder[i] )
				slam = MathF.Max( slam, impact );

			m_WasUnder[i] = true;

			// Cutting into a wave hard enough throws spray off the hull's side here.
			m_SprayCooldown[i] -= Time.Delta;
			if ( impact > SprayImpact && m_SprayCooldown[i] <= 0.0f )
			{
				m_SprayCooldown[i] = 0.15f;
				ThrowSpray( m_Points[i], water, impact / SprayImpact - 1.0f );
			}

			// Linear to the resting depth, then stiffening the further past it - the hull's sides. The water can't push
			// up harder than the cap, lift and damping together, whatever the wave is doing.
			float past = MathF.Max( 0.0f, depth - rest );
			float lift = (depth + Reserve * past * past / rest) * stiffness * share;
			float damper = (waterSpeed - pointSpeed) * damping * share;

			m_Body.ApplyForceAt( point, Vector3.Up * MathF.Min( lift + damper, maxLift ) );
		}

		Submerged = (float)under / m_Points.Length;

		if ( Submerged <= 0.0f )
			return;

		ApplyDrag( mass );

		if ( slam > SplashSpeed && m_Foam.IsValid() )
			m_Foam.Splash( SplashFoam * MathF.Min( slam / SplashSpeed - 1.0f, 2.0f ) );
	}

	protected override void DrawGizmos()
	{
		// The points the sea pushes on: blue under water, white out of it, and the water's movement under the hull.
		for ( int i = 0; i < m_Points.Length; i++ )
		{
			Gizmo.Draw.Color = Game.IsPlaying && m_WasUnder[i] ? Color.Cyan : Color.White;
			Gizmo.Draw.SolidSphere( m_Points[i], 3.0f );
		}

		if ( Game.IsPlaying && Submerged > 0.0f )
		{
			Gizmo.Draw.Color = Color.Cyan;
			Gizmo.Draw.Arrow( Vector3.Zero, WorldTransform.NormalToLocal( WaterVelocity ), 6.0f, 3.0f );
		}
	}

	/// <summary>
	/// Spray off the hull's side nearest a point, at the waterline: thrown out from the side, forward too at the bow, and
	/// carried along with the hull.
	/// </summary>
	private void ThrowSpray( Vector3 localPoint, float waterZ, float strength )
	{
		var spray = OceanSpray.Current;

		if ( !spray.IsValid() || strength <= 0.0f )
			return;

		var bounds = m_Collider.LocalBounds;
		float side = MathF.Abs( localPoint.y - bounds.Center.y ) > 1.0f ? MathF.Sign( localPoint.y - bounds.Center.y ) : (Game.Random.Float() < 0.5f ? -1.0f : 1.0f);
		var edge = new Vector3( localPoint.x, bounds.Center.y + side * bounds.Extents.y * 0.95f, bounds.Center.z );
		var at = WorldTransform.PointToWorld( edge ).WithZ( waterZ );

		// Out from the side, and from the bow forward as well: the bow throws its spray ahead and to both sides.
		float bow = ((localPoint.x - bounds.Center.x) / bounds.Extents.x).Clamp( 0.0f, 1.0f );
		var outward = WorldRotation.Left * side + WorldRotation.Forward * bow * 0.8f;

		spray.Emit( at, m_Body.GetVelocityAtPoint( at ), outward, strength * SprayAmount );
	}

	/// <summary>
	/// The water's drag on the hull moving through it: slight along the hull, strong across it, both with the square of
	/// the speed against the water - so the water's own movement pulls the hull along with it.
	/// </summary>
	private void ApplyDrag( float mass )
	{
		var relative = (m_Body.Velocity - WaterVelocity).WithZ( 0 ) / UnitsPerMetre;
		var forward = WorldRotation.Forward.WithZ( 0 );
		forward = forward.Length > 1e-3f ? forward.Normal : Vector3.Forward;
		var side = new Vector3( -forward.y, forward.x, 0 );

		float along = Vector3.Dot( relative, forward );
		float across = Vector3.Dot( relative, side );

		// Quadratic, as water drag is, with a small linear part so a hull nearly at rest still settles.
		float fAlong = -(LengthwiseDrag * MathF.Abs( along ) + 0.05f) * along;
		float fAcross = -(SidewaysDrag * MathF.Abs( across ) + 0.3f) * across;

		var force = (forward * fAlong + side * fAcross) * mass * UnitsPerMetre * Submerged;
		m_Body.ApplyForce( force );

		var angular = m_Body.AngularVelocity;
		m_Body.AngularVelocity = angular * MathF.Max( 0.0f, 1.0f - AngularDrag * Submerged * Time.Delta * (1.0f + angular.Length * 0.5f) );
	}

	private void LayOutPoints()
	{
		int count = PointsAlong * PointsAcross;

		if ( m_Points.Length == count )
			return;

		var bounds = m_Collider.LocalBounds;
		m_Points = new Vector3[count];
		m_LastWater = new float[count];
		m_WasUnder = new bool[count];
		m_SprayCooldown = new float[count];
		Array.Fill( m_LastWater, float.NaN );
		Array.Fill( m_WasUnder, true );

		for ( int a = 0; a < PointsAlong; a++ )
		{
			for ( int b = 0; b < PointsAcross; b++ )
			{
				float x = PointsAlong == 1 ? 0.0f : (a / (PointsAlong - 1.0f) * 2.0f - 1.0f) * Spread.x;
				float y = PointsAcross == 1 ? 0.0f : (b / (PointsAcross - 1.0f) * 2.0f - 1.0f) * Spread.y;
				m_Points[a * PointsAcross + b] = bounds.Center + new Vector3( x * bounds.Extents.x, y * bounds.Extents.y, 0.0f );
			}
		}
	}
}
