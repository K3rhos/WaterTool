using Sandbox;
using Editor;

namespace RedSnail.WaterTool.Editor;

/// <summary>
/// Scene editor tool for the WaterFlow component. Activates when a WaterFlow is
/// selected and hosts the spline editor: select points, drag them and their In/Out
/// tangent handles (for curved rivers), click on the river to insert a point, and
/// shift-drag a point to extrude a new one. All edits are undo-aware and rebuild
/// the river mesh live.
/// </summary>
[Title("Water Flow")]
[Icon("waves")]
[Alias("water_flow")]
[Group("1")]
[Order(1)]
public class WaterFlowTool : EditorTool<WaterFlow>
{
	private WaterFlowWindow m_Window;
	private WaterFlow m_Selected;



	public override void OnEnabled()
	{
		m_Window = new WaterFlowWindow();

		AddOverlay(m_Window, TextFlag.RightBottom, 10);

		OnSelectionChanged();
	}



	public override void OnDisabled()
	{
		m_Window?.OnDisabled();
	}



	public override void OnUpdate()
	{
		m_Window?.OnUpdate();
	}



	public override void OnSelectionChanged()
	{
		WaterFlow target = GetSelectedComponent<WaterFlow>();

		if (!target.IsValid())
			return;

		// Only re-target when the component itself changes — otherwise this fires on
		// every property edit and would reset the selected point each time.
		if (target != m_Selected)
		{
			m_Window?.OnSelectionChanged(target);

			m_Selected = target;
		}
	}
}
