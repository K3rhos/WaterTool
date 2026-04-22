using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace RedSnail.WaterTool;

/// <summary>
/// Excludes the water surface inside a mesh hull rather than an approximated box volume.
/// Place on the same GameObject as the ModelRenderer. The physics collision mesh is extracted
/// once and uploaded to the GPU as a triangle list; only the WorldToLocal matrix is updated
/// each frame as the object moves or rotates.
/// </summary>
[Title("Hull Water Exclusion Volume"), Group("Water"), Icon("sailing")]
public sealed class HullWaterExclusionVolume : Component, Component.ExecuteInEditor
{
	/// <summary>Triangle vertices in model LOCAL space, flat (v0,v1,v2, v0,v1,v2 …).</summary>
	public Vector3[] LocalTriangles { get; private set; } = Array.Empty<Vector3>();

	/// <summary>AABB of all local triangles, used for early GPU rejection.</summary>
	public BBox LocalAABB { get; private set; }

	private Model _lastModel;

	protected override void OnEnabled()
	{
		RebuildMesh();

		WaterManager.Current?.Register(this);
	}

	protected override void OnDisabled()
	{
		WaterManager.Current?.Unregister(this);
	}

	protected override void OnUpdate()
	{
		var model = GetComponent<ModelRenderer>()?.Model;
		
		if (model != _lastModel)
			RebuildMesh();
	}

	private void RebuildMesh()
	{
		var model = GetComponent<ModelRenderer>()?.Model;

		if (model == null)
		{
			LocalTriangles = Array.Empty<Vector3>();
			LocalAABB = default;
			_lastModel = null;
			Log.Warning($"{nameof(HullWaterExclusionVolume)}: No ModelRenderer or Model found.");
			return;
		}

		_lastModel = model;

		var tris = new List<Vector3>();
		var aabbMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
		var aabbMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

		// Prefer the physics collision mesh — it's already simplified and watertight.
		var physics = model.Physics;
		if (physics != null)
		{
			foreach (var part in physics.Parts)
			{
				foreach (var meshPart in part.Meshes)
				{
					foreach (var tri in meshPart.GetTriangles())
					{
						tris.Add(tri.A);
						tris.Add(tri.B);
						tris.Add(tri.C);

						aabbMin = Vector3.Min(aabbMin, Vector3.Min(tri.A, Vector3.Min(tri.B, tri.C)));
						aabbMax = Vector3.Max(aabbMax, Vector3.Max(tri.A, Vector3.Max(tri.B, tri.C)));
					}
				}

				// Convex hull shapes have no MeshParts — triangulate each hull instead.
				foreach (var hullPart in part.Hulls)
				{
					var pts = hullPart.GetPoints()?.ToArray();
					if (pts == null || pts.Length < 4) continue;
					TriangulateConvexHull(pts, tris, ref aabbMin, ref aabbMax);
				}
			}
		}

		// Fallback: render mesh (may have more triangles, less ideal for GPU iteration)
		if (tris.Count == 0)
		{
			var vertices = model.GetVertices();
			var indices = model.GetIndices();

			if (vertices != null && indices != null)
			{
				for (int i = 0; i + 2 < indices.Length; i += 3)
				{
					Vector3 v0 = vertices[indices[i + 0]].Position;
					Vector3 v1 = vertices[indices[i + 1]].Position;
					Vector3 v2 = vertices[indices[i + 2]].Position;

					tris.Add(v0);
					tris.Add(v1);
					tris.Add(v2);

					aabbMin = Vector3.Min(aabbMin, Vector3.Min(v0, Vector3.Min(v1, v2)));
					aabbMax = Vector3.Max(aabbMax, Vector3.Max(v0, Vector3.Max(v1, v2)));
				}
			}
		}

		LocalTriangles = tris.ToArray();
		LocalAABB = tris.Count > 0 ? new BBox(aabbMin, aabbMax) : default;
	}

	// N³ convex hull triangulation.
	// Finds each hull face by collecting ALL coplanar vertices, then fan-triangulates once per face.
	// Without this, rectangular faces (4 coplanar verts) emit C(4,3)=4 overlapping triangles,
	// flipping the ray parity and incorrectly marking exterior points as inside.
	private static void TriangulateConvexHull(Vector3[] verts, List<Vector3> result, ref Vector3 aabbMin, ref Vector3 aabbMax)
	{
		int n = verts.Length;
		if (n < 4) return;

		var centroid = Vector3.Zero;
		foreach (var v in verts) centroid += v;
		centroid /= n;

		var processedFaces = new HashSet<string>();

		for (int i = 0; i < n; i++)
			for (int j = i + 1; j < n; j++)
				for (int k = j + 1; k < n; k++)
				{
					Vector3 A = verts[i], B = verts[j], C = verts[k];
					Vector3 rawNormal = Vector3.Cross(B - A, C - A);
					if (rawNormal.LengthSquared < 1e-8f) continue;
					Vector3 normal = rawNormal.Normal; // normalize so d = actual distance in units

					bool pos = false, neg = false;
					var faceIndices = new List<int> { i, j, k };

					for (int m = 0; m < n; m++)
					{
						if (m == i || m == j || m == k) continue;
						float d = Vector3.Dot(normal, verts[m] - A);
						if (MathF.Abs(d) < 0.01f)
							faceIndices.Add(m);   // coplanar — part of this face
						else if (d > 0f) pos = true;
						else neg = true;
					}

					if (pos && neg) continue;     // interior edge, not a hull face
					if (!pos && !neg) continue;   // degenerate — no non-coplanar vertices

					// Canonical key: sorted vertex indices — each face processed exactly once.
					faceIndices.Sort();
					string key = string.Join(",", faceIndices);
					if (!processedFaces.Add(key)) continue;

					// Collect face vertices and sort by angle around the face centroid.
					var faceVerts = faceIndices.Select(idx => verts[idx]).ToList();
					var fc = Vector3.Zero;
					foreach (var fv in faceVerts) fc += fv;
					fc /= faceVerts.Count;

					// Build a 2D frame in the face plane for angle sorting.
					var outward = (Vector3.Dot(normal, centroid - A) < 0f) ? normal : -normal;
					var tan = faceVerts.Select(fv => fv - fc).FirstOrDefault(d => d.LengthSquared > 1e-8f);
					tan = tan.Normal;
					var bitan = Vector3.Cross(outward.Normal, tan);

					faceVerts.Sort((p, q) =>
					{
						float ap = MathF.Atan2(Vector3.Dot(p - fc, bitan), Vector3.Dot(p - fc, tan));
						float aq = MathF.Atan2(Vector3.Dot(q - fc, bitan), Vector3.Dot(q - fc, tan));
						return ap.CompareTo(aq);
					});

					// Fan triangulate the face.
					for (int t = 1; t < faceVerts.Count - 1; t++)
					{
						var ta = faceVerts[0]; var tb = faceVerts[t]; var tc = faceVerts[t + 1];
						result.Add(ta); result.Add(tb); result.Add(tc);
						aabbMin = Vector3.Min(aabbMin, Vector3.Min(ta, Vector3.Min(tb, tc)));
						aabbMax = Vector3.Max(aabbMax, Vector3.Max(ta, Vector3.Max(tb, tc)));
					}
				}
	}

	/// <summary>
	/// Fills the 4 rows of the WorldToLocal matrix (row-major, for mul(M, float4(worldPos,1)) in HLSL).
	/// </summary>
	/// <summary>
	/// Matches WorldTransform.PointToLocal = Rotation.Inverse * (worldPt - Position) / Scale.
	/// In s&box: Forward=(1,0,0)=localX, Left=-Right=(0,1,0)=localY, Up=(0,0,1)=localZ.
	/// </summary>
	public void GetWorldToLocalRows(out Vector4 r0, out Vector4 r1, out Vector4 r2, out Vector4 r3)
	{
		Vector3 fwd = WorldRotation.Forward;        // world-space local X axis
		Vector3 left = -WorldRotation.Right;          // world-space local Y axis  (Right = -Y in s&box)
		Vector3 up = WorldRotation.Up;             // world-space local Z axis
		Vector3 pos = WorldPosition;
		Vector3 scale = WorldScale;

		float isx = MathF.Abs(scale.x) > 1e-6f ? 1f / scale.x : 0f;
		float isy = MathF.Abs(scale.y) > 1e-6f ? 1f / scale.y : 0f;
		float isz = MathF.Abs(scale.z) > 1e-6f ? 1f / scale.z : 0f;

		r0 = new Vector4(fwd.x * isx, fwd.y * isx, fwd.z * isx, -Vector3.Dot(fwd, pos) * isx);
		r1 = new Vector4(left.x * isy, left.y * isy, left.z * isy, -Vector3.Dot(left, pos) * isy);
		r2 = new Vector4(up.x * isz, up.y * isz, up.z * isz, -Vector3.Dot(up, pos) * isz);
		r3 = new Vector4(0f, 0f, 0f, 1f);
	}

	protected override void DrawGizmos()
	{
		if (!Gizmo.IsSelected || LocalTriangles == null || LocalTriangles.Length == 0)
			return;

		Gizmo.Draw.Color = Color.Yellow.WithAlpha(0.5f);
		Gizmo.Draw.LineBBox(LocalAABB);
	}
}
