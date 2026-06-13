using System;
using System.Collections.Generic;
using Sandbox;
using Editor;

namespace RedSnail.WaterTool.Editor;

/// <summary>
/// Overlay window + gizmo host for editing a WaterFlow's spline. Modeled on the
/// RoadTool spline editor: a single selected point drives the position gizmo and
/// the In/Out tangent handles, clicking a segment inserts a point, shift-drag
/// extrudes a new point.
/// </summary>
public partial class WaterFlowWindow : WidgetWindow
{
	private WaterFlow _targetComponent;
	private static bool _isClosed;

	private ControlWidget _positionControl;
	private ControlWidget _inTangentControl;
	private ControlWidget _outTangentControl;

	private bool _inTangentSelected;
	private bool _outTangentSelected;
	private bool _draggingOutNewPoint;
	private bool _moveInProgress;

	private List<Vector3> _polyLine = [];
	private IDisposable _movementUndoScope;



	public WaterFlowWindow()
	{
		ContentMargins = 0;
		Layout = Layout.Column();

		MaximumWidth = 460;
		MinimumWidth = 320;

		Rebuild();
	}



	public void OnDisabled()
	{
		_movementUndoScope?.Dispose();
		_movementUndoScope = null;
	}



	public void OnUpdate()
	{
		if (!_targetComponent.IsValid())
			return;

		DrawGizmos();
	}



	public void OnSelectionChanged(WaterFlow _Target)
	{
		_targetComponent = _Target;

		_inTangentSelected = false;
		_outTangentSelected = false;

		// Rebuild first so the tangent ControlWidgets exist before SelectPoint's
		// SelectedPointIndex setter triggers ToggleTangentInput.
		Rebuild();

		SelectPoint(0);
	}



	/// <summary>
	/// Describes how the spline behaves at a point. Mirrors Spline.HandleMode but
	/// carries the inspector icons so it renders as an icon picker in the control sheet.
	/// </summary>
	public enum HandleModeProxy
	{
		[Icon("auto_fix_high")] Auto,
		[Icon("show_chart")] Linear,
		[Icon("open_in_full")] Mirrored,
		[Icon("call_split")] Split,
	}



	private IDisposable CreateUndoScope(string _Name)
	{
		return SceneEditorSession.Active.UndoScope(_Name).WithComponentChanges(_targetComponent).Push();
	}
}
