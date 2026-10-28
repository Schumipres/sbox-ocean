// The ocean surface: camera-centred rings of grid (a clipmap) pushed into waves by the three FFT cascades Ocean
// writes. Each ring is twice as coarse as the one inside it and sits on a world-fixed grid, so the waves don't swim as
// the camera moves. Near its outer edge a ring folds its odd vertices onto the coarser grid of the next one
// (geomorphing), so the two meet without cracks; the part of a ring that the finer one inside already covers is
// clipped away. Small cascades fade out with distance, before the grid gets too coarse to carry them.
//
// Shading: a near-black body, the turquoise of light scattering out of crests that stand high and are seen against
// the sun, the sky's reflection through the engine's lighting and the sun's broad glare. Foam comes from OceanFoam's
// persistent window near the camera - breaking crests and hulls, fading and dispersing - and past it from the crests
// breaking right now (the Jacobian of all three cascades below the threshold), drawn through a pattern of drifting
// bubbles.
HEADER
{
	Description = "Ocean Surface (FFT clipmap)";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth( S_MODE_DEPTH );
	ToolsShadingComplexity( "tools_shading_complexity.shader" );
}

COMMON
{
	#include "common/shared.hlsl"

	// Where each cascade's waves have faded out of the geometry, in units from the camera.
	float3 g_vOceanFadeDistances < Attribute( "OceanFadeDistances" ); Default3( 1e7, 40000, 6000 ); >;
	float2 g_vOceanCameraXY < Attribute( "OceanCameraXY" ); Default2( 0, 0 ); >;

	float3 CascadeFade( float distance, float3 ends )
	{
		return saturate( 2.0 - 2.0 * distance / ends );
	}
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
	float4 vOcean : TEXCOORD14;     // undisplaced world xy, distance from the camera, wave height in metres
};

VS
{
	#include "common/vertex.hlsl"
	#include "ocean/shaders/ocean_cascades.hlsl"

	SamplerState g_sWrapVs < Filter( BILINEAR ); AddressU( WRAP ); AddressV( WRAP ); >;

	float g_flCellSize < Attribute( "LevelCellSize" ); Default1( 20 ); >;      // units between vertices
	float2 g_vLevelCenter < Attribute( "LevelCenter" ); Default2( 0, 0 ); >;    // world xy, on the 2-cell grid
	float g_flGridHalf < Attribute( "LevelGridHalf" ); Default1( 96 ); >;      // cells from centre to edge
	float g_flMorphStart < Attribute( "LevelMorphStart" ); Default1( 80 ); >;  // cells from centre where folding begins

	PixelInput MainVs( VertexInput v )
	{
		PixelInput i = ProcessVertex( v );

		// The mesh is in cells about the ring's centre. Towards the outer edge, odd vertices slide onto their even
		// neighbour, until at the edge the triangles match the next ring's coarser ones exactly.
		float2 cell = v.vPositionOs.xy;
		float edge = max( abs( cell.x ), abs( cell.y ) );
		float morph = saturate( ( edge - g_flMorphStart ) / ( g_flGridHalf - g_flMorphStart ) );
		cell -= frac( cell * 0.5 ) * 2.0 * morph;

		float2 world = g_vLevelCenter + cell * g_flCellSize;
		float distance = length( world - g_vOceanCameraXY );
		float3 fade = CascadeFade( distance, g_vOceanFadeDistances );

		// Explicit LOD here, so each vertex can skip the cascades it has faded out of.
		float3 shift = OceanDisplacement( g_sWrapVs, world, fade, fade ).xyz;

		float3 position = float3( world, 0.0 ) + shift * g_flOceanUnitsPerMetre;

		// The engine hands on positions relative to an offset of its own, for precision far from the origin. The rings
		// are drawn at the world origin, unrotated, so whatever it did to the mesh's own position is that offset: take
		// it away from the wave's position in the same way. (The offset's shader global doesn't hold it here.)
		float3 offset = v.vPositionOs.xyz - i.vPositionWs.xyz;
		i.vPositionWs.xyz = position - offset;
		i.vPositionPs = Position3WsToPs( i.vPositionWs.xyz );
		i.vNormalWs = float3( 0, 0, 1 );
		i.vOcean = float4( world, distance, shift.z );
		return FinalizeVertex( i );
	}
}

PS
{
	#include "common/pixel.hlsl"

	// Filtered and mip-mapped, so distant waves shade without shimmering.
	#define OCEAN_SAMPLE( tex, s, uv ) tex.Sample( s, uv )
	#include "ocean/shaders/ocean_cascades.hlsl"

	SamplerState g_sWrap < Filter( ANISOTROPIC ); MaxAniso( 8 ); AddressU( WRAP ); AddressV( WRAP ); >;

	// The finer ring this one encloses: its centre and half width, in world units. Zero width for the innermost.
	float2 g_vInnerCenter < Attribute( "LevelInnerCenter" ); Default2( 0, 0 ); >;
	float g_flInnerHalf < Attribute( "LevelInnerHalf" ); Default1( 0 ); >;

	// Which cascades any pixel of this ring still shades with: 0 where the whole ring lies past a cascade's fade.
	float3 g_vLevelCascades < Attribute( "LevelCascades" ); Default3( 1, 1, 1 ); >;

	float g_flFoamThreshold < Attribute( "OceanFoamThreshold" ); Default1( 0.4 ); >;

	// ── Foam ──
	Texture2D g_tFoam < Attribute( "OceanFoamMap" ); >;
	SamplerState g_sClamp < Filter( BILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	float2 g_vFoamOrigin < Attribute( "OceanFoamOrigin" ); Default2( 0, 0 ); >;
	float g_flFoamExtent < Attribute( "OceanFoamExtent" ); Default1( 0 ); >;       // 0: no foam window
	float3 g_vFoamColor < Attribute( "OceanFoamColor" ); Default3( 0.82, 0.88, 0.88 ); >;
	float g_flFoamBubbleScale < Attribute( "OceanFoamBubbleScale" ); Default1( 1.0 ); >;  // metres per bubble cell

	// The foam window. Through a sampler: Load() on these engine-bound textures reads back nothing.
	float FoamAt( float2 uv )
	{
		return g_tFoam.SampleLevel( g_sClamp, uv, 0 ).x;
	}

	float2 Hash2( float2 p )
	{
		p = float2( dot( p, float2( 127.1, 311.7 ) ), dot( p, float2( 269.5, 183.3 ) ) );
		return frac( sin( p ) * 43758.5453 );
	}

	// How far a point is from the walls between bubbles: a scatter of points, one per cell, each drifting in a small
	// circle, and the gap between the nearest two of them - 0 on the wall where two bubbles meet, larger inside one.
	float BubbleWalls( float2 p, float time )
	{
		float2 cell = floor( p );
		float2 f = frac( p );
		float first = 8.0, second = 8.0;

		[unroll] for ( int y = -1; y <= 1; y++ )
		[unroll] for ( int x = -1; x <= 1; x++ )
		{
			float2 o = float2( x, y );
			float2 h = Hash2( cell + o );
			float2 point = o + 0.5 + 0.35 * sin( time * 0.6 + 6.2831 * h );
			float d = length( point - f );
			second = d < first ? first : min( second, d );
			first = min( first, d );
		}

		return second - first;
	}

	// How much of the surface the foam covers, from the foam amount: thin foam is a lace of bubble walls, thickening
	// until the bubbles close up into a solid sheet. The pattern blurs to the plain amount with distance, where the
	// lace would only shimmer.
	float FoamCover( float amount, float2 worldMetres, float distanceMetres )
	{
		float far = saturate( amount * 1.1 );
		float blur = saturate( distanceMetres / 150.0 );

		// The bubble pattern is most of this shader's arithmetic: only work it out where it can show - where there is
		// foam at all, and near enough not to be blurred away entirely. Both give exactly what the full sum would.
		[branch]
		if ( amount <= 0.0 || blur >= 1.0 )
			return amount <= 0.0 ? 0.0 : far;

		float2 p = worldMetres / g_flFoamBubbleScale;
		float walls = BubbleWalls( p, g_flTime ) * 0.6 + BubbleWalls( p * 2.3 + 13.1, g_flTime * 1.3 ) * 0.4;
		float near = saturate( ( amount * 1.1 - walls ) * 8.0 ) * saturate( amount * 4.0 );
		return lerp( near, far, blur );
	}

	// ── Rain ──
	float g_flRainRipples < Attribute( "OceanRainRipples" ); Default1( 0 ); >;

	// Rain on the water: drops landing and the rings they spread. The surface is cut into cells, each with one drop at a
	// time landing somewhere in it at its own moment: a bright splash where it hits, then a ring spreading and fading,
	// tilting the normal as it goes. The tilt alone barely shows under a grey storm sky, so the splash and the ring's
	// crest also leave a light mark, returned in z. Three grids, offset from each other, so the drops never line up.
	float3 RainRipples( float2 worldXY, float time )
	{
		float2 tilt = float2( 0.0, 0.0 );
		float mark = 0.0;

		[unroll]
		for ( int layer = 0; layer < 3; layer++ )
		{
			float2 p = worldXY / 40.0 + layer * float2( 0.37, 0.61 );
			float2 cell = floor( p );
			float2 local = frac( p );

			float h = frac( sin( dot( cell + layer * 17.0, float2( 127.1, 311.7 ) ) ) * 43758.5453 );
			float2 center = 0.2 + 0.6 * float2( frac( h * 13.7 ), frac( h * 71.3 ) );
			float age = frac( time * 1.1 + h );

			float2 d = local - center;
			float r = length( d );
			float ring = r - age * 0.45;
			float fade = 1.0 - age;

			tilt += ( d / max( r, 0.001 ) ) * sin( ring * 60.0 ) * exp( -ring * ring * 900.0 ) * fade;

			float splash = ( 1.0 - smoothstep( 0.015, 0.06, r ) ) * saturate( 1.0 - age / 0.12 );
			float crest = exp( -ring * ring * 5000.0 ) * fade * fade;
			mark = max( mark, max( splash, crest * 0.7 ) );
		}

		return float3( tilt, mark );
	}

	// ── Look ──
	float3 g_vSunDirection < Attribute( "OceanSunDirection" ); Default3( 0.9, 0, 0.42 ); >;    // towards the sun
	float3 g_vSunColor < Attribute( "OceanSunColor" ); Default3( 1, 0.92, 0.78 ); >;
	float3 g_vDeepColor < Attribute( "OceanDeepColor" ); Default3( 0.004, 0.03, 0.06 ); >;
	float3 g_vScatterColor < Attribute( "OceanScatterColor" ); Default3( 0.03, 0.42, 0.38 ); >;
	float g_flScatterStrength < Attribute( "OceanScatterStrength" ); Default1( 1.0 ); >;
	float g_flScatterHeight < Attribute( "OceanScatterHeight" ); Default1( 3.0 ); >;   // metres of crest for full scatter
	float g_flSunSpread < Attribute( "OceanSunSpread" ); Default1( 0.6 ); >;
	float g_flSunGlow < Attribute( "OceanSunGlow" ); Default1( 0.25 ); >;
	float g_flRoughness < Attribute( "OceanRoughness" ); Default1( 0.06 ); >;

	// Light that went into the water and came back out: what turns the crests turquoise. Not traced but faked from
	// what makes it show - how high the crest stands (thinner water, the light
	// travels less), whether the eye looks through the crest towards the sun, and how much the wave face turns away
	// from the sun (lit from behind) - with a weaker term for the faces turned towards the eye.
	float3 Scatter( float3 N, float3 V, float3 L, float height )
	{
		// From a little under the mean level up to the crest, so the glow fades in up the face rather than switching on.
		float crest = smoothstep( -0.3, 1.0, height / g_flScatterHeight );
		float lowSun = saturate( 1.0 - L.z * 1.2 ) * 0.6 + 0.4;

		float backlit = pow( saturate( dot( V, -L ) * 0.5 + 0.5 ), 4.0 ) * pow( saturate( 0.5 - 0.5 * dot( L, N ) ), 2.0 );
		float facing = pow( saturate( dot( V, N ) ), 2.0 ) * 0.08;

		float amount = crest * crest * ( backlit * 6.0 * lowSun + facing ) + facing * 0.5;
		return g_vScatterColor * g_vSunColor * amount * g_flScatterStrength;
	}

	// The sun's broad glare on the water: Karis' sphere-light trick widens the sharp specular highlight of a tiny sun
	// into the soft, wide one of a big, low disc - a broad band of light across the sea.
	float3 SunGlare( float3 N, float3 V, float3 L )
	{
		float3 R = reflect( -V, N );
		float RdotL = saturate( dot( R, L ) );
		float fresnel = 0.02 + 0.98 * pow( 1.0 - saturate( dot( N, V ) ), 5.0 );
		float wide = pow( RdotL, lerp( 2000.0, 40.0, g_flSunSpread ) );
		float halo = pow( RdotL, 8.0 ) * g_flSunGlow * 0.05;
		return g_vSunColor * ( wide * 6.0 + halo ) * ( 0.4 + fresnel );
	}

	// Hulls the sea is kept out of (OceanHullMask), to the shape of their collision. Per shape, six rows: three of its
	// world-to-model transform, (first plane, plane count, first triangle, triangle count), and its box's corners; the
	// planes and triangle points they index follow in the same buffer.
	// A float: an int attribute set back to 0 kept its last value.
	float g_flHullMaskCount < Attribute( "OceanHullMaskCount" ); Default1( 0 ); >;
	StructuredBuffer<float4> g_vHullMasks < Attribute( "OceanHullMasks" ); >;

	// Whether a ray from a point along +x crosses a triangle (Moller-Trumbore with the direction fixed).
	bool RayCrossesAlongX( float3 origin, float3 v0, float3 v1, float3 v2 )
	{
		float3 e1 = v1 - v0;
		float3 e2 = v2 - v0;
		float3 pvec = float3( 0.0, -e2.z, e2.y );
		float det = dot( e1, pvec );

		if ( abs( det ) < 1e-7 )
			return false;

		float inv = 1.0 / det;
		float3 tvec = origin - v0;
		float u = dot( tvec, pvec ) * inv;
		if ( u < 0.0 || u > 1.0 )
			return false;

		float3 qvec = cross( tvec, e1 );
		float v = qvec.x * inv;
		if ( v < 0.0 || u + v > 1.0 )
			return false;

		return dot( e2, qvec ) * inv > 0.0;
	}

	bool InsideHull( float3 position )
	{
		float4 p = float4( position, 1.0 );

		int count = (int)( g_flHullMaskCount + 0.5 );

		[loop]
		for ( int h = 0; h < count; h++ )
		{
			int meta = h * 6;
			float3 local = float3( dot( g_vHullMasks[meta + 0], p ), dot( g_vHullMasks[meta + 1], p ), dot( g_vHullMasks[meta + 2], p ) );

			// Nowhere near this hull: the box turns away nearly every pixel of the sea.
			if ( any( local < g_vHullMasks[meta + 4].xyz ) || any( local > g_vHullMasks[meta + 5].xyz ) )
				continue;

			float4 ranges = g_vHullMasks[meta + 3];
			int planeStart = (int)ranges.x, planeCount = (int)ranges.y;
			int triStart = (int)ranges.z, triCount = (int)ranges.w;

			// Convex: inside if behind every face, out at the first one it's in front of.
			if ( planeCount > 0 )
			{
				bool inside = true;

				[loop]
				for ( int i = 0; i < planeCount && inside; i++ )
				{
					float4 plane = g_vHullMasks[planeStart + i];
					inside = dot( plane.xyz, local ) + plane.w <= 0.0;
				}

				if ( inside )
					return true;

				continue;
			}

			// Anything else: inside when a ray from it crosses the surface an odd number of times.
			int crossings = 0;

			[loop]
			for ( int t = 0; t < triCount; t++ )
			{
				int b = triStart + t * 3;
				if ( RayCrossesAlongX( local, g_vHullMasks[b].xyz, g_vHullMasks[b + 1].xyz, g_vHullMasks[b + 2].xyz ) )
					crossings++;
			}

			if ( crossings & 1 )
				return true;
		}

		return false;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 world = i.vOcean.xy;

		if ( g_flHullMaskCount > 0.5 && InsideHull( i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz ) )
			discard;
		// Covered by the finer ring inside.
		float2 fromInner = abs( world - g_vInnerCenter );
		clip( max( fromInner.x, fromInner.y ) - g_flInnerHalf );

		// Normals carry the small cascades further than the geometry can; the mips smooth them out with distance.
		float3 fade = CascadeFade( i.vOcean.z, g_vOceanFadeDistances * 4.0 );

		float4 d = OceanDerivatives( g_sWrap, world, fade, g_vLevelCascades );
		float shear = OceanShear( g_sWrap, world, fade, g_vLevelCascades );
		float3 normal = OceanNormal( d );
		float jacobian = OceanJacobian( d, shear );

		// Rain, near the camera only: further off the drops are smaller than a pixel and would only shimmer.
		float rainMark = 0.0;

		[branch]
		if ( g_flRainRipples > 0.0 )
		{
			float rainFade = saturate( 1.0 - i.vOcean.z / 3000.0 );
			float3 rain = RainRipples( world, g_flTime ) * ( g_flRainRipples * rainFade );
			normal = normalize( normal + float3( rain.xy * 0.8, 0.0 ) );
			rainMark = saturate( rain.z * 0.6 );
		}

		float3 position = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		float3 V = normalize( g_vCameraPositionWs - position );
		float3 L = normalize( g_vSunDirection );

		// Water lets almost no light back out: a dark body, lit only faintly, and the colour comes from the light
		// that scatters out of the crests and from reflections - the sky's, through the engine's lighting, and the
		// sun's broad glare on top.
		Material m = Material::Init( i );
		m.Normal = normal;
		m.Albedo = g_vDeepColor;
		m.Roughness = g_flRoughness;
		m.Metalness = 0.0;
		m.Emission = Scatter( normal, V, L, i.vOcean.w ) + SunGlare( normal, V, L );

		// The persistent foam window near the camera; past its edge, only the crests breaking right now.
		float crestFoam = saturate( ( g_flFoamThreshold - jacobian ) * 2.0 ) * 0.7;
		float amount = crestFoam;

		if ( g_flFoamExtent > 0.0 )
		{
			float2 foamUv = ( world - g_vFoamOrigin ) / g_flFoamExtent;
			float2 edge = min( foamUv, 1.0 - foamUv );
			float inside = saturate( min( edge.x, edge.y ) * 10.0 );
			float window = FoamAt( foamUv );
			amount = lerp( crestFoam, window, inside );
		}

		float foam = max( FoamCover( amount, world / g_flOceanUnitsPerMetre, i.vOcean.z / g_flOceanUnitsPerMetre ), rainMark * 0.6 );
		m.Albedo = lerp( m.Albedo, g_vFoamColor, foam );
		m.Emission *= 1.0 - foam;
		m.Roughness = lerp( m.Roughness, 0.75, foam );

		return ShadingModelStandard::Shade( i, m );
	}
}
