using System;
using Sandbox.Rendering;

namespace OceanLib;

/// <summary>
/// Spray where a hull cuts into the sea: when a bow slams into a wave face, the water it shoves aside is thrown up and
/// out in torn white sheets, a scatter of droplets and a drifting mist, which arc over, fall back and are gone in a
/// second or two - the wave breaking on the boat. The sea itself doesn't deform; the spray
/// and the foam (<see cref="OceanFoamSource.Splash"/>) sell it. <see cref="OceanBuoyancy"/> calls <see cref="Emit"/>.
///
/// The particles live on the CPU - a few thousand at most, cheap to move - and are drawn as one batch of camera-facing
/// quads with the ocean_spray shader, from a camera command list after the scene's transparent things. They are lit by
/// the scene's sun and fog colours, so they go grey with the storm.
/// </summary>
[Title( "Ocean Spray" ), Category( "Ocean" ), Icon( "water_drop" )]
public sealed class OceanSpray : Component
{
	/// <summary>A material on the ocean_spray shader. Empty uses the library's.</summary>
	[Property] public Material Material { get; set; }

	/// <summary>The most particles alive at once; older ones make way when it's full.</summary>
	[Property, Range( 100, 8000 )] public int MaxParticles { get; set; } = 3000;

	/// <summary>Scales every burst: more sheets, more droplets.</summary>
	[Property, Range( 0, 4 )] public float Amount { get; set; } = 1.0f;

	/// <summary>Scales how fast and high the spray is thrown.</summary>
	[Property, Range( 0.2f, 3 )] public float Force { get; set; } = 1.0f;

	/// <summary>The sun lighting the spray. Empty takes the scene's first.</summary>
	[Property] public DirectionalLight Sun { get; set; }

	/// <summary>
	/// The fog whose colour shades the spray's underside, when it has a constant colour. Empty takes the scene's;
	/// without one - or with a fog coloured by the sky - the sun's sky light shades it instead.
	/// </summary>
	[Property] public Fog Fog { get; set; }

	/// <summary>The first active spray in the scene, for anything that wants to throw some.</summary>
	public static OceanSpray Current { get; private set; }

	/// <summary>Particles alive now.</summary>
	public int Count => m_Count;

	private enum Kind { Sheet = 0, Droplet = 1, Mist = 2 }

	private struct Particle
	{
		public Vector3 Position, Velocity;
		public float Age, Life, Size, Grow, Opacity, Seed, Floor, Drag;
		public Kind Kind;
	}

	private const float UnitsPerMetre = Ocean.UnitsPerMetre;
	private const int DrawOrder = 105;

	private Particle[] m_Particles = Array.Empty<Particle>();
	private int m_Count;
	private Vertex[] m_Vertices = Array.Empty<Vertex>();
	private Mesh m_Mesh;
	private Model m_Model;
	private readonly CameraCommands m_Commands = new( "Ocean Spray", Stage.AfterTransparent, DrawOrder );
	private readonly RenderAttributes m_Attributes = new();

	protected override void OnEnabled()
	{
		Current ??= this;
		Allocate();
	}

	protected override void OnDisabled()
	{
		if ( Current == this )
			Current = null;

		m_Commands.Dispose();
		m_Model = null;
	}

	private void Allocate()
	{
		int max = Math.Clamp( MaxParticles, 100, 8000 );
		m_Particles = new Particle[max];
		m_Vertices = new Vertex[max * 4];
		m_Count = 0;

		var material = Material ?? Material.Load( "ocean/materials/ocean_spray.vmat" );

		if ( !material.IsValid() )
			return;

		var indices = new int[max * 6];
		for ( int p = 0; p < max; p++ )
		{
			int v = p * 4, i = p * 6;
			indices[i] = v; indices[i + 1] = v + 1; indices[i + 2] = v + 2;
			indices[i + 3] = v; indices[i + 4] = v + 2; indices[i + 5] = v + 3;
		}

		m_Mesh = new Mesh( material );
		m_Mesh.CreateVertexBuffer<Vertex>( m_Vertices.Length, m_Vertices.AsSpan() );
		m_Mesh.CreateIndexBuffer( indices.Length, indices.AsSpan() );
		m_Mesh.Bounds = new BBox( new Vector3( -1e7f ), new Vector3( 1e7f ) );
		m_Model = Model.Builder.AddMesh( m_Mesh ).Create();
	}

	/// <summary>
	/// Throws a burst of spray from a point on the waterline: up, out along <paramref name="outward"/> (away from the
	/// hull) and carried on with the hull's own velocity. Strength 1 is a solid slam into a wave; 3 is the biggest.
	/// </summary>
	public void Emit( Vector3 at, Vector3 hullVelocity, Vector3 outward, float strength )
	{
		if ( m_Particles.Length == 0 || strength <= 0.0f )
			return;

		strength = MathF.Min( strength, 3.0f );
		outward = outward.WithZ( 0 );
		outward = outward.Length > 1e-3f ? outward.Normal : Vector3.Forward;
		var along = new Vector3( -outward.y, outward.x, 0 );
		float force = Force * UnitsPerMetre * MathF.Sqrt( strength );
		var carried = hullVelocity * 0.7f;

		int sheets = Spread( 3.0f * strength * Amount );
		int droplets = Spread( 14.0f * strength * Amount );
		int mists = Spread( 1.5f * strength * Amount );

		for ( int i = 0; i < sheets; i++ )
		{
			var dir = outward * Game.Random.Float( 1.5f, 4.0f ) + along * Game.Random.Float( -1.5f, 1.5f ) + Vector3.Up * Game.Random.Float( 3.0f, 6.5f );
			Add( Kind.Sheet, at + along * Game.Random.Float( -40, 40 ), carried + dir * force, Game.Random.Float( 0.7f, 1.2f ),
				Game.Random.Float( 1.2f, 2.2f ) * UnitsPerMetre * MathF.Sqrt( strength ), 3.0f * UnitsPerMetre, 1.0f, 0.6f );
		}

		for ( int i = 0; i < droplets; i++ )
		{
			var dir = outward * Game.Random.Float( 1.0f, 5.0f ) + along * Game.Random.Float( -2.0f, 2.0f ) + Vector3.Up * Game.Random.Float( 3.0f, 8.5f );
			Add( Kind.Droplet, at + along * Game.Random.Float( -50, 50 ), carried + dir * force, Game.Random.Float( 0.9f, 1.8f ),
				Game.Random.Float( 0.08f, 0.16f ) * UnitsPerMetre, 0.0f, 1.0f, 0.05f );
		}

		for ( int i = 0; i < mists; i++ )
		{
			var dir = outward * Game.Random.Float( 0.5f, 1.5f ) + Vector3.Up * Game.Random.Float( 1.0f, 2.5f );
			Add( Kind.Mist, at + Vector3.Up * 40, carried * 0.5f + dir * force * 0.6f, Game.Random.Float( 1.8f, 3.0f ),
				Game.Random.Float( 2.0f, 3.5f ) * UnitsPerMetre, 2.5f * UnitsPerMetre, 0.5f, 1.2f );
		}
	}

	/// <summary>A count from an average: the fraction becomes the chance of one more.</summary>
	private static int Spread( float average ) => (int)average + (Game.Random.Float() < average - MathF.Floor( average ) ? 1 : 0);

	private void Add( Kind kind, Vector3 at, Vector3 velocity, float life, float size, float grow, float opacity, float drag )
	{
		// Full: the oldest particle makes way.
		int slot = m_Count;

		if ( m_Count >= m_Particles.Length )
		{
			slot = 0;
			for ( int i = 1; i < m_Count; i++ )
				if ( m_Particles[i].Age / m_Particles[i].Life > m_Particles[slot].Age / m_Particles[slot].Life )
					slot = i;
		}
		else
		{
			m_Count++;
		}

		m_Particles[slot] = new Particle
		{
			Kind = kind, Position = at, Velocity = velocity, Life = life, Size = size, Grow = grow, Opacity = opacity,
			Drag = drag, Seed = Game.Random.Float(), Floor = at.z - 40.0f, Age = 0.0f
		};
	}

	protected override void OnUpdate()
	{
		if ( m_Particles.Length != Math.Clamp( MaxParticles, 100, 8000 ) || (m_Model is null && Material.IsValid()) )
			Allocate();

		float dt = Time.Delta;
		var gravity = Scene.PhysicsWorld.Gravity;

		for ( int i = 0; i < m_Count; )
		{
			ref var p = ref m_Particles[i];
			p.Age += dt;
			p.Velocity += gravity * (p.Kind == Kind.Mist ? 0.05f : 1.0f) * dt;
			p.Velocity *= MathF.Max( 0.0f, 1.0f - p.Drag * dt );
			p.Position += p.Velocity * dt;
			p.Size += p.Grow * dt;

			// Gone once its time is up, or once it has fallen back into the sea.
			if ( p.Age >= p.Life || (p.Velocity.z < 0 && p.Position.z < p.Floor) )
			{
				m_Particles[i] = m_Particles[--m_Count];
				continue;
			}

			i++;
		}
	}

	protected override void OnPreRender()
	{
		m_Commands.List.Reset();
		m_Commands.Follow( Scene );
		var camera = m_Commands.Camera;

		if ( m_Model is null || m_Mesh is null || m_Count == 0 || !camera.IsValid() )
			return;

		var right = camera.WorldRotation.Right;
		var up = camera.WorldRotation.Up;
		var forward = camera.WorldRotation.Forward;

		for ( int i = 0; i < m_Count; i++ )
		{
			ref var p = ref m_Particles[i];
			float t = p.Age / p.Life;
			float half = p.Size * 0.5f;

			// Fades in at once and out over its last part; sheets thin out as they spread.
			float fade = MathF.Min( t * 12.0f, 1.0f ) * MathF.Min( (1.0f - t) * 3.0f, 1.0f );
			float opacity = p.Opacity * fade * (p.Kind == Kind.Sheet ? 1.0f - t * 0.5f : 1.0f);

			var axisX = right * half;
			var axisY = up * half;

			// Droplets stretch along their flight as seen from the camera.
			if ( p.Kind == Kind.Droplet )
			{
				var flight = p.Velocity - forward * Vector3.Dot( p.Velocity, forward );
				float speed = flight.Length;

				if ( speed > 1.0f )
				{
					var dir = flight / speed;
					axisY = dir * half * (1.0f + speed / UnitsPerMetre * 0.25f);
					axisX = Vector3.Cross( dir, forward ).Normal * half;
				}
			}

			var packed = new Vector4( 0, 0, opacity, (int)p.Kind + p.Seed * 0.099f );
			int v = i * 4;
			m_Vertices[v + 0] = Corner( p.Position - axisX - axisY, -1, -1, opacity, packed.w );
			m_Vertices[v + 1] = Corner( p.Position + axisX - axisY, 1, -1, opacity, packed.w );
			m_Vertices[v + 2] = Corner( p.Position + axisX + axisY, 1, 1, opacity, packed.w );
			m_Vertices[v + 3] = Corner( p.Position - axisX + axisY, -1, 1, opacity, packed.w );
		}

		m_Mesh.SetVertexBufferData<Vertex>( m_Vertices.AsSpan( 0, m_Count * 4 ), 0 );
		m_Mesh.SetIndexRange( 0, m_Count * 6 );

		// Lit by the sun on top, the fog's or the sky's grey underneath: white in the sunshine, slate in a storm.
		var sun = Sun.IsValid() ? Sun : Scene.GetAllComponents<DirectionalLight>().FirstOrDefault();
		var fog = Fog.IsValid() ? Fog : Scene.GetAllComponents<Fog>().FirstOrDefault();
		var light = sun.IsValid() ? sun.LightColor : Color.White;
		var shade = fog.IsValid() && fog.ColorSource == FogColorSource.Constant ? fog.Color
			: sun.IsValid() ? sun.SkyColor
			: new Color( 0.55f, 0.62f, 0.68f );
		// White water stays the brightest thing on a grey sea: a floor of light keeps it from sinking into the gloom.
		var sky = new Vector3( shade.r, shade.g, shade.b );
		var lit = new Vector3( light.r, light.g, light.b ) * 0.7f + sky * 0.6f + new Vector3( 0.12f );

		m_Attributes.Set( "SprayLight", lit );
		m_Attributes.Set( "SprayShadow", sky * 0.9f + new Vector3( 0.05f ) );
		m_Commands.List.DrawModel( m_Model, global::Transform.Zero, m_Attributes );
	}

	private static Vertex Corner( Vector3 position, float u, float v, float opacity, float kind )
	{
		return new Vertex
		{
			Position = position,
			Normal = Vector3.Up,
			Tangent = new Vector4( 1, 0, 0, 1 ),
			TexCoord0 = new Vector4( u, v, 0, 0 ),
			TexCoord1 = new Vector4( opacity, kind, 0, 0 ),
			Color = Color.White,
		};
	}
}
