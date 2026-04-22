using Sandbox;
using Sandbox.Rendering;

namespace RedSnail.WaterTool;

[Title("Wobble Effect")]
[Category("Post Processing")]
[Icon("waves")]
public sealed class WobbleEffect : BasePostProcess<WobbleEffect>
{
	[Property, Range(0.1f, 100.0f)] private float Frequency { get; set; } = 20.0f;
	[Property, Range(0.1f, 10.0f)] private float Amplitude { get; set; } = 1.0f;
	[Property, Range(0.1f, 10.0f)] private float Speed { get; set; } = 1.0f;



	public override void Render()
	{
		float amplitude = GetWeighted(x => x.Amplitude);

		if (amplitude.AlmostEqual(0.0f))
			return;

		Attributes.Set("Frequency", Frequency);
		Attributes.Set("Amplitude", amplitude);
		Attributes.Set("Speed", Speed);

		Material shader = Material.FromShader("pp_wobble");
		BlitMode blit = BlitMode.WithBackbuffer(shader, Stage.BeforePostProcess, 70);
		Blit(blit, "Wobble Effect");
	}
}
