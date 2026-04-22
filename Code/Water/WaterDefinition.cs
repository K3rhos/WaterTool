using Sandbox;

namespace RedSnail.WaterTool;

[AssetType(Name = "Water Definition", Extension = "wtdef", Category = "Water")]
public sealed class WaterDefinition : GameResource
{
	[Property, Group("Detail")] public float WavesIntensity { get; set; } = 4.0f;
	[Property, Group("Detail"), Range(0, 5)] public float WavesSpeed { get; set; } = 0.3f;
	[Property, Group("Detail")] public float WavesScale { get; set; } = 0.05f;
	[Property, Group("Detail")] public Vector2 WavesDirection { get; set; } = new Vector2(1, 0.5f);
	[Property, Group("Detail"), Range(1, 5)] public int WavesOctaves { get; set; } = 3;
	[Property, Group("Detail")] public float WavesLacunarity { get; set; } = 2.0f;
	[Property, Group("Detail"), Range(0, 1)] public float WavesPersistence { get; set; } = 0.5f;
	[Property, Group("Detail"), Range(0, 1)] public float WavesSteepness { get; set; } = 0.5f;

	[Property, Group("Swell")] public float SwellIntensity { get; set; } = 15.0f;
	[Property, Group("Swell"), Range(0, 500)] public float SwellSpeed { get; set; } = 100.0f;
	[Property, Group("Swell")] public float SwellScale { get; set; } = 0.002f;
	[Property, Group("Swell")] public Vector2 SwellDirection { get; set; } = new Vector2(0.7f, 0.3f);
	[Property, Group("Swell"), Range(1, 4)] public int SwellOctaves { get; set; } = 2;
	[Property, Group("Swell")] public float SwellLacunarity { get; set; } = 1.8f;
	[Property, Group("Swell"), Range(0, 1)] public float SwellPersistence { get; set; } = 0.6f;
	[Property, Group("Swell"), Range(0, 1)] public float SwellSteepness { get; set; } = 0.3f;

	public void ApplyTo(RenderAttributes attributes)
	{
		attributes.Set("WavesIntensity", WavesIntensity);
		attributes.Set("WavesSpeed", WavesSpeed);
		attributes.Set("WavesScale", WavesScale);
		attributes.Set("WavesDirection", WavesDirection);
		attributes.Set("WavesOctaves", WavesOctaves);
		attributes.Set("WavesLacunarity", WavesLacunarity);
		attributes.Set("WavesPersistence", WavesPersistence);
		attributes.Set("WavesSteepness", WavesSteepness);

		attributes.Set("SwellIntensity", SwellIntensity);
		attributes.Set("SwellSpeed", SwellSpeed);
		attributes.Set("SwellScale", SwellScale);
		attributes.Set("SwellDirection", SwellDirection);
		attributes.Set("SwellOctaves", SwellOctaves);
		attributes.Set("SwellLacunarity", SwellLacunarity);
		attributes.Set("SwellPersistence", SwellPersistence);
		attributes.Set("SwellSteepness", SwellSteepness);
	}

	protected override Bitmap CreateAssetTypeIcon(int _Width, int _Height)
	{
		return CreateSimpleAssetTypeIcon("water", _Width, _Height, "#4287f5", "white");
	}
}
