using Sandbox;

namespace RedSnail.WaterTool.Editor;

public partial class WaterFlowWindow
{
	private const float GIZMO_BOX_SIZE = 2.0f;
	private const float LINE_THICKNESS = 2.0f;
	private const float TANGENT_LINE_THICKNESS = 0.8f;



	private void DrawGizmos()
	{
		// Spline points live in the component's local space
		using (Gizmo.Scope("water_flow_editor", _targetComponent.WorldTransform))
		{
			DrawSplineSegments();
			DrawPositionGizmo();
			DrawPointControls();
		}
	}



	private void DrawSplineSegments()
	{
		_targetComponent.Spline.ConvertToPolyline(ref _polyLine);

		for (int i = 0; i < _polyLine.Count - 1; i++)
			DrawSegment(i, _polyLine[i], _polyLine[i + 1]);
	}



	private void DrawSegment(int _Index, Vector3 _Start, Vector3 _End)
	{
		using (Gizmo.Scope("segment" + _Index))
		using (Gizmo.Hitbox.LineScope())
		{
			Gizmo.Draw.LineThickness = LINE_THICKNESS;
			Gizmo.Hitbox.AddPotentialLine(_Start, _End, LINE_THICKNESS * 2.0f);
			Gizmo.Draw.Line(_Start, _End);

			if (Gizmo.IsHovered && Gizmo.HasMouseFocus)
				HandleSegmentHover(_Start, _End);
		}
	}



	// Hovering a segment shows a preview handle at the closest point; clicking
	// inserts a spline point there (splitting the segment without changing the shape).
	private void HandleSegmentHover(Vector3 _Start, Vector3 _End)
	{
		Gizmo.Draw.Color = Color.Cyan;

		if (!new Line(_Start, _End).ClosestPoint(Gizmo.CurrentRay.ToLocal(Gizmo.Transform), out Vector3 pointOnLine, out _))
			return;

		var hoverSample = _targetComponent.Spline.SampleAtClosestPosition(pointOnLine);
		DrawHoverHandle(pointOnLine, hoverSample.Tangent);

		if (Gizmo.HasClicked && Gizmo.Pressed.This)
			InsertPointAtHover(hoverSample.Distance);
	}



	private void DrawHoverHandle(Vector3 _Position, Vector3 _Tangent)
	{
		using (Gizmo.Scope("hover_handle", new Transform(_Position, Rotation.LookAt(_Tangent))))
		using (Gizmo.GizmoControls.PushFixedScale())
		{
			Gizmo.Draw.SolidBox(BBox.FromPositionAndSize(Vector3.Zero, GIZMO_BOX_SIZE));
		}
	}



	private void InsertPointAtHover(float _Distance)
	{
		using (CreateUndoScope("Insert Water Flow Point"))
		{
			SelectedPointIndex = _targetComponent.Spline.AddPointAtDistance(_Distance, true);
			_inTangentSelected = false;
			_outTangentSelected = false;
		}

		UpdateWindowTitle();
	}



	// ─────────────────────────────────────────────────────────────────────────
	// Position gizmo — drives the selected point (or its selected tangent handle)
	// ─────────────────────────────────────────────────────────────────────────

	private void DrawPositionGizmo()
	{
		if (!IsSelectedPointValid())
			return;

		// The source point's position is locked to the GameObject origin — only show
		// the move gizmo for it when one of its tangent handles is selected.
		if (IsSourcePointSelected && !_inTangentSelected && !_outTangentSelected)
			return;

		Vector3 gizmoPosition = CalculateGizmoPosition();

		if (!Gizmo.IsShiftPressed)
			_draggingOutNewPoint = false;

		using (Gizmo.Scope("position", new Transform(gizmoPosition)))
		{
			HandlePositionControl();
		}
	}



	private Vector3 CalculateGizmoPosition()
	{
		Vector3 position = _selectedPoint.Position;

		if (_inTangentSelected)
			position += _selectedPoint.In;
		else if (_outTangentSelected)
			position += _selectedPoint.Out;

		return position;
	}



	private void HandlePositionControl()
	{
		_moveInProgress = false;

		if (Gizmo.Control.Position("water_flow_move", Vector3.Zero, out Vector3 delta))
		{
			_moveInProgress = true;
			_movementUndoScope ??= CreateUndoScope("Move Water Flow Point");

			if (_inTangentSelected)
				MoveSelectedPointInTangent(delta);
			else if (_outTangentSelected)
				MoveSelectedPointOutTangent(delta);
			else
				HandlePointMove(delta);
		}

		// Close the drag's undo scope once the mouse is released
		if (!_moveInProgress && Gizmo.WasLeftMouseReleased)
		{
			_movementUndoScope?.Dispose();
			_movementUndoScope = null;
		}
	}



	// Shift-dragging a point extrudes a fresh point and drags that instead, so you
	// can quickly extend or branch the river without using the insert button.
	private void HandlePointMove(Vector3 _Delta)
	{
		if (Gizmo.IsShiftPressed && !_draggingOutNewPoint)
		{
			_draggingOutNewPoint = true;

			var currentPoint = _targetComponent.Spline.GetPoint(SelectedPointIndex);
			_targetComponent.Spline.InsertPoint(SelectedPointIndex + 1, currentPoint);
			SelectedPointIndex++;

			UpdateWindowTitle();
		}
		else
		{
			MoveSelectedPoint(_Delta);
		}
	}



	// ─────────────────────────────────────────────────────────────────────────
	// Point + tangent handles
	// ─────────────────────────────────────────────────────────────────────────

	private void DrawPointControls()
	{
		var spline = _targetComponent.Spline;

		for (int i = 0; i < spline.PointCount; i++)
		{
			// Hide the duplicated closing point of a looped spline
			if (spline.IsLoop && i == spline.SegmentCount)
				continue;

			DrawPointControl(i, spline.GetPoint(i));
		}
	}



	private void DrawPointControl(int _Index, Spline.Point _Point)
	{
		using (Gizmo.Scope("point_controls" + _Index, new Transform(_Point.Position)))
		{
			Gizmo.Draw.IgnoreDepth = true;

			DrawPointPositionHandle(_Index);

			if (SelectedPointIndex == _Index)
				DrawTangentHandles(_Point);
		}
	}



	private void DrawPointPositionHandle(int _Index)
	{
		using (Gizmo.Scope("position"))
		using (Gizmo.GizmoControls.PushFixedScale())
		{
			Gizmo.Hitbox.DepthBias = 0.1f;
			Gizmo.Hitbox.BBox(BBox.FromPositionAndSize(Vector3.Zero, GIZMO_BOX_SIZE));

			bool isSelected = _Index == SelectedPointIndex && !_inTangentSelected && !_outTangentSelected;

			// Source = green, mouth = red, otherwise white; selected/hovered = cyan
			Gizmo.Draw.Color = _Index == 0
				? Color.Green
				: (_Index == _targetComponent.Spline.PointCount - 1 ? Color.Red : Color.White);

			if (Gizmo.IsHovered || isSelected)
				Gizmo.Draw.Color = Color.Cyan;

			Gizmo.Draw.SolidBox(BBox.FromPositionAndSize(Vector3.Zero, GIZMO_BOX_SIZE));

			if (Gizmo.HasClicked && Gizmo.Pressed.This)
				SelectPoint(_Index);
		}
	}



	private void DrawTangentHandles(Spline.Point _Point)
	{
		Gizmo.Draw.Color = Color.White;
		Gizmo.Draw.LineThickness = TANGENT_LINE_THICKNESS;

		DrawTangentHandle("in_tangent", _Point.In, ref _inTangentSelected, ref _outTangentSelected);
		DrawTangentHandle("out_tangent", _Point.Out, ref _outTangentSelected, ref _inTangentSelected);
	}



	private void DrawTangentHandle(string _Name, Vector3 _Offset, ref bool _ThisSelected, ref bool _OtherSelected)
	{
		using (Gizmo.Scope(_Name, new Transform(_Offset)))
		{
			bool isMirroredOrAuto = _selectedPointTangentMode is HandleModeProxy.Mirrored or HandleModeProxy.Auto;

			if (isMirroredOrAuto && (_ThisSelected || _OtherSelected))
				Gizmo.Draw.Color = Color.Cyan;

			// Line back to the point (scope is at the handle offset → point is -offset)
			Gizmo.Draw.Line(-_Offset, Vector3.Zero);

			// Linear handles have no draggable box (tangents are implicit)
			if (_selectedPointTangentMode != HandleModeProxy.Linear)
				DrawTangentBox(ref _ThisSelected, ref _OtherSelected);
		}
	}



	private void DrawTangentBox(ref bool _ThisSelected, ref bool _OtherSelected)
	{
		using (Gizmo.GizmoControls.PushFixedScale())
		{
			Gizmo.Hitbox.DepthBias = 0.1f;
			Gizmo.Hitbox.BBox(BBox.FromPositionAndSize(Vector3.Zero, GIZMO_BOX_SIZE));

			if (Gizmo.IsHovered || _ThisSelected)
				Gizmo.Draw.Color = Color.Cyan;

			Gizmo.Draw.SolidBox(BBox.FromPositionAndSize(Vector3.Zero, GIZMO_BOX_SIZE));

			if (Gizmo.HasClicked && Gizmo.Pressed.This)
			{
				_ThisSelected = true;
				_OtherSelected = false;
			}
		}
	}
}
