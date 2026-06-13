using Sandbox;
using Editor;

namespace RedSnail.WaterTool.Editor;

public partial class WaterFlowWindow
{
	private const int HEADER_HEIGHT = 32;



	private void Rebuild()
	{
		Layout.Clear(true);
		Layout.Margin = 0;

		Icon = _isClosed ? "" : "waves";
		UpdateWindowTitle();
		IsGrabbable = !_isClosed;

		if (_isClosed)
		{
			BuildClosedState();
			return;
		}

		MinimumWidth = 360;
		BuildHeader();

		if (_targetComponent.IsValid())
			BuildControlSheet();

		Layout.Margin = 4;
	}



	private void BuildClosedState()
	{
		var closedRow = Layout.AddRow();

		closedRow.Add(new IconButton("waves", () => { _isClosed = false; Rebuild(); })
		{
			ToolTip = "Open Water Flow Spline Editor",
			FixedHeight = HEADER_HEIGHT,
			FixedWidth = HEADER_HEIGHT,
			Background = Color.Transparent
		});

		MinimumWidth = 0;
	}



	private void BuildHeader()
	{
		var headerRow = Layout.AddRow();

		headerRow.AddStretchCell();

		headerRow.Add(new IconButton("info")
		{
			ToolTip = GetInfoTooltip(),
			FixedHeight = HEADER_HEIGHT,
			FixedWidth = HEADER_HEIGHT,
			Background = Color.Transparent
		});

		headerRow.Add(new IconButton("close", CloseWindow)
		{
			ToolTip = "Close Editor",
			FixedHeight = HEADER_HEIGHT,
			FixedWidth = HEADER_HEIGHT,
			Background = Color.Transparent
		});
	}



	private string GetInfoTooltip()
	{
		return "Edit the river's spline.\n\n" +
			   "• Click a point to select it, then drag it or its In/Out tangent handles.\n" +
			   "• Tangent Mode controls the curve: Auto smooths, Linear makes sharp corners,\n" +
			   "  Mirrored/Split let you shape the bend by hand.\n" +
			   "• Click anywhere on the river to insert a point there.\n" +
			   "• Hold Shift while dragging a point to drag out a new one.\n\n" +
			   "The source point is green, the mouth is red.";
	}



	private void BuildControlSheet()
	{
		var serialized = this.GetSerialized();
		var controlSheet = new ControlSheet();

		controlSheet.AddRow(serialized.GetProperty(nameof(_selectedPointTangentMode)));
		_positionControl = controlSheet.AddRow(serialized.GetProperty(nameof(_selectedPointPosition)));
		_inTangentControl = controlSheet.AddRow(serialized.GetProperty(nameof(_selectedPointIn)));
		_outTangentControl = controlSheet.AddRow(serialized.GetProperty(nameof(_selectedPointOut)));

		controlSheet.AddLayout(BuildControlButtons());

		Layout.Add(controlSheet);

		ToggleTangentInput();
	}



	private Layout BuildControlButtons()
	{
		var row = Layout.Row();
		row.Spacing = 16;
		row.Margin = 8;

		row.Add(CreateNavigationButton("skip_previous", -1, "Go to previous point"));
		row.Add(CreateNavigationButton("skip_next", 1, "Go to next point"));
		row.Add(CreateDeleteButton());
		row.Add(CreateAddButton());

		return row;
	}



	private IconButton CreateNavigationButton(string _Icon, int _Direction, string _Tooltip)
	{
		return new IconButton(_Icon, () =>
		{
			if (_Direction < 0)
				SelectedPointIndex = int.Max(0, SelectedPointIndex - 1);
			else
				SelectedPointIndex = int.Min(_targetComponent.Spline.PointCount - 1, SelectedPointIndex + 1);

			SelectPoint(SelectedPointIndex);
			Focus();
		})
		{ ToolTip = _Tooltip };
	}



	private IconButton CreateDeleteButton()
	{
		return new IconButton("delete", () =>
		{
			// The source point can't be deleted, and rivers need at least two points
			if (IsSourcePointSelected || _targetComponent.Spline.PointCount <= 2)
				return;

			using (CreateUndoScope("Delete Water Flow Point"))
			{
				_targetComponent.Spline.RemovePoint(SelectedPointIndex);
				SelectedPointIndex = int.Max(0, SelectedPointIndex - 1);
			}

			UpdateWindowTitle();
			Focus();
		})
		{ ToolTip = "Delete the selected point (the source point is locked; minimum 2 points)" };
	}



	private IconButton CreateAddButton()
	{
		return new IconButton("add", () =>
		{
			using (CreateUndoScope("Add Water Flow Point"))
			{
				InsertNewPoint();
				SelectedPointIndex++;
			}

			UpdateWindowTitle();
			Focus();
		})
		{
			ToolTip = "Insert a point after the selected one.\n" +
					  "You can also click on the river, or Shift-drag a point."
		};
	}



	private void InsertNewPoint()
	{
		var spline = _targetComponent.Spline;

		if (SelectedPointIndex == spline.PointCount - 1)
		{
			// Extend past the mouth, following the spline tangent
			float distance = spline.GetDistanceAtPoint(SelectedPointIndex);
			Vector3 tangent = spline.SampleAtDistance(distance).Tangent;
			Vector3 newPosition = _selectedPoint.Position + tangent * 256.0f;

			spline.InsertPoint(SelectedPointIndex + 1, _selectedPoint with { Position = newPosition });
		}
		else
		{
			// Split the segment toward the next point
			float currentDist = spline.GetDistanceAtPoint(SelectedPointIndex);
			float nextDist = spline.GetDistanceAtPoint(SelectedPointIndex + 1);

			spline.AddPointAtDistance((currentDist + nextDist) / 2.0f, true);
		}
	}



	private void UpdateWindowTitle()
	{
		WindowTitle = _isClosed
			? ""
			: $"Water Flow — Point [{SelectedPointIndex}] — {_targetComponent?.GameObject?.Name ?? ""}";
	}



	private void CloseWindow()
	{
		_isClosed = true;
		Rebuild();
		Position = Parent.Size - 32;
	}
}
