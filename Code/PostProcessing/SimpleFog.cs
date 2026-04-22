using Sandbox;
using Sandbox.Rendering;

namespace RedSnail.WaterTool;

[Title("Simple Fog")]
[Category("Post Processing")]
[Icon("foggy")]
public sealed class SimpleFog : BasePostProcess<SimpleFog>
{
	[Property] private Color Color { get; set; } = Color.White;
	[Property, Range(0, 1)] private float Intensity { get; set; } = 0.01f;
	[Property, Range(0, 1)] private float Opacity { get; set; } = 0.5f;



	public override void Render()
	{
		float opacity = GetWeighted(x => x.Opacity);

		if (opacity.AlmostEqual(0.0f))
			return;

		Attributes.Set("Color", GetWeighted(x => x.Color));
		Attributes.Set("Intensity", GetWeighted(x => x.Intensity));
		Attributes.Set("Opacity", opacity);

		Material shader = Material.FromShader("pp_simplefog");
		BlitMode blit = BlitMode.WithBackbuffer(shader, Stage.BeforePostProcess, 60);
		Blit(blit, "Simple Fog");
	}
}
