using Sandbox;

namespace RedSnail.WaterTool.Editor;

public partial class WaterFlowWindow
{
	[Title("Tangent Mode")]
	private HandleModeProxy _selectedPointTangentMode
	{
		get => IsSelectedPointValid() ? (HandleModeProxy)_selectedPoint.Mode : HandleModeProxy.Auto;
		set
		{
			using (CreateUndoScope("Update Water Flow Point"))
			{
				_selectedPoint = _selectedPoint with { Mode = (Spline.HandleMode)value };
				ToggleTangentInput();
			}
		}
	}

	[Title("Position")]
	private Vector3 _selectedPointPosition
	{
		get => IsSelectedPointValid() ? _selectedPoint.Position : Vector3.Zero;
		set
		{
			using (CreateUndoScope("Update Water Flow Point"))
			{
				_selectedPoint = _selectedPoint with { Position = value };
			}
		}
	}

	[Title("In Tangent")]
	private Vector3 _selectedPointIn
	{
		get => IsSelectedPointValid() ? _selectedPoint.In : Vector3.Zero;
		set
		{
			using (CreateUndoScope("Update Water Flow Point"))
			{
				_selectedPoint = _selectedPoint with { In = value };
			}
		}
	}

	[Title("Out Tangent")]
	private Vector3 _selectedPointOut
	{
		get => IsSelectedPointValid() ? _selectedPoint.Out : Vector3.Zero;
		set
		{
			using (CreateUndoScope("Update Water Flow Point"))
			{
				_selectedPoint = _selectedPoint with { Out = value };
			}
		}
	}

	private int SelectedPointIndex
	{
		get;
		set
		{
			field = value;
			ToggleTangentInput();
		}
	}

	private Spline.Point _selectedPoint
	{
		get => IsSelectedPointValid() ? _targetComponent.Spline.GetPoint(SelectedPointIndex) : new Spline.Point();
		set
		{
			using (CreateUndoScope("Update Water Flow Point"))
			{
				_targetComponent.Spline.UpdatePoint(SelectedPointIndex, value);
			}
		}
	}



	private bool IsSelectedPointValid()
	{
		return _targetComponent.IsValid()
			&& SelectedPointIndex >= 0
			&& SelectedPointIndex < _targetComponent.Spline.PointCount;
	}



	// The In/Out fields are only meaningful for user-driven tangents (Mirrored/Split).
	// The source point (index 0) is pinned to the GameObject origin, so its position
	// field is locked too.
	private void ToggleTangentInput()
	{
		bool isAutoOrLinear = _selectedPoint.Mode is Spline.HandleMode.Auto or Spline.HandleMode.Linear;

		if (_inTangentControl.IsValid())
			_inTangentControl.Enabled = !isAutoOrLinear;

		if (_outTangentControl.IsValid())
			_outTangentControl.Enabled = !isAutoOrLinear;

		if (_positionControl.IsValid())
			_positionControl.Enabled = !IsSourcePointSelected;
	}



	// True when the locked source point (index 0) is the active selection.
	private bool IsSourcePointSelected => SelectedPointIndex == 0;



	private void MoveSelectedPoint(Vector3 _Delta)
	{
		var updatedPoint = _selectedPoint with { Position = _selectedPoint.Position + _Delta };

		_targetComponent.Spline.UpdatePoint(SelectedPointIndex, updatedPoint);
	}



	private void MoveSelectedPointInTangent(Vector3 _Delta)
	{
		var updatedPoint = _selectedPoint;
		updatedPoint.In += _Delta;

		// Dragging a tangent on an Auto point promotes it to a user-driven mode
		if (_selectedPointTangentMode == HandleModeProxy.Auto)
			updatedPoint.Mode = Spline.HandleMode.Mirrored;

		if (_selectedPointTangentMode is HandleModeProxy.Mirrored or HandleModeProxy.Auto)
			updatedPoint.Out = -updatedPoint.In;

		_targetComponent.Spline.UpdatePoint(SelectedPointIndex, updatedPoint);
	}



	private void MoveSelectedPointOutTangent(Vector3 _Delta)
	{
		var updatedPoint = _selectedPoint;
		updatedPoint.Out += _Delta;

		if (_selectedPointTangentMode == HandleModeProxy.Auto)
			updatedPoint.Mode = Spline.HandleMode.Mirrored;

		if (_selectedPointTangentMode is HandleModeProxy.Mirrored or HandleModeProxy.Auto)
			updatedPoint.In = -updatedPoint.Out;

		_targetComponent.Spline.UpdatePoint(SelectedPointIndex, updatedPoint);
	}



	private void SelectPoint(int _Index)
	{
		SelectedPointIndex = _Index;
		_inTangentSelected = false;
		_outTangentSelected = false;

		UpdateWindowTitle();
	}
}
