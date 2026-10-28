namespace OceanLib;

/// <summary>
/// One state of the sea, everything that changes between a calm and a storm. The ocean blends between two of these
/// (<see cref="Ocean.BlendTo"/>), so the weather can build or ease off while the sea keeps moving.
/// </summary>
public readonly record struct OceanSea( float WindSpeed, float SignificantHeight, float Choppiness, float Scatter, float CrestFoam )
{
	/// <summary>A moderate, fresh sea: what an ocean shows with no preset.</summary>
	public static OceanSea Default => new( 9.0f, 2.5f, 1.3f, 0.8f, 1.0f );

	public static OceanSea Lerp( OceanSea a, OceanSea b, float t ) => new(
		MathX.Lerp( a.WindSpeed, b.WindSpeed, t ),
		MathX.Lerp( a.SignificantHeight, b.SignificantHeight, t ),
		MathX.Lerp( a.Choppiness, b.Choppiness, t ),
		MathX.Lerp( a.Scatter, b.Scatter, t ),
		MathX.Lerp( a.CrestFoam, b.CrestFoam, t ) );
}

/// <summary>A sea state saved as an asset, for an <see cref="Ocean"/> to start from or blend to.</summary>
[AssetType( Name = "Ocean Preset", Extension = "ocnsea", Category = "Ocean" )]
public sealed class OceanPreset : GameResource
{
	/// <summary>Wind speed in metres per second: sets how long the biggest waves are.</summary>
	[Property, Range( 1, 40 )] public float WindSpeed { get; set; } = 9.0f;

	/// <summary>The mean height of the biggest third of the waves, trough to crest, in metres.</summary>
	[Property, Range( 0, 15 )] public float SignificantHeight { get; set; } = 2.5f;

	/// <summary>How far the crests lean and pinch: 0 is smooth rolling waves, around 1.5 sharp peaks.</summary>
	[Property, Range( 0, 2.5f )] public float Choppiness { get; set; } = 1.3f;

	/// <summary>How strongly light scattering out of the crests glows.</summary>
	[Property, Range( 0, 4 )] public float Scatter { get; set; } = 0.8f;

	/// <summary>Foam per second a fully breaking crest makes. 0 leaves foam only round what's in the water.</summary>
	[Property, Range( 0, 20 )] public float CrestFoam { get; set; } = 1.0f;

	public OceanSea Sea => new( WindSpeed, SignificantHeight, Choppiness, Scatter, CrestFoam );

	protected override Bitmap CreateAssetTypeIcon( int width, int height )
	{
		return CreateSimpleAssetTypeIcon( "waves", width, height, "#1f6f8b", "white" );
	}
}
