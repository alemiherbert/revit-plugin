using Autodesk.Revit.DB;

namespace StructuralTools.SketchEngine;

/// <summary>
/// Offsets a set of panel corners from the top/nosing surface to the
/// structural mid-surface of the waist slab.
///
/// The analytical panel in Revit represents the structural centroid of the
/// slab. For a concrete stair with waist thickness <c>t</c> the panel must
/// be placed at <c>t/2</c> below the walking surface along the local slab
/// normal — not straight down, but perpendicular to the inclined face.
///
/// Usage
/// -----
/// For inclined run panels:
///   <c>var mid = MidSurfaceOffset.Apply(corners, thicknessFt);</c>
///
/// For flat landing panels the normal is always (0,0,1) so the offset is
/// purely vertical:
///   <c>var mid = MidSurfaceOffset.ApplyHorizontal(corners, thicknessFt);</c>
/// </summary>
public static class MidSurfaceOffset
{
    /// <summary>
    /// Compute the slab normal from the panel corners and shift every corner
    /// by <c>−normal × (thicknessFt / 2)</c> (i.e. into the concrete).
    ///
    /// The normal is derived from the first non-degenerate triangle in the
    /// corner list and always has a non-negative Z component (points toward
    /// the upper face of the slab).
    /// </summary>
    public static List<XYZ> Apply(IList<XYZ> corners, double thicknessFt)
    {
        var normal = ComputeSlabNormal(corners);
        // Shift inward (into the concrete) = subtract the upward-pointing normal.
        var delta = normal.Multiply(thicknessFt / 2.0);
        return corners.Select(c => c.Subtract(delta)).ToList();
    }

    /// <summary>
    /// Specialisation for horizontal (landing) panels.
    /// The slab normal is always (0, 0, 1) so only the Z coordinate changes.
    /// </summary>
    public static List<XYZ> ApplyHorizontal(IList<XYZ> corners, double thicknessFt)
    {
        double dz = thicknessFt / 2.0;
        return corners.Select(c => new XYZ(c.X, c.Y, c.Z - dz)).ToList();
    }

    // ------------------------------------------------------------------
    // Internal
    // ------------------------------------------------------------------

    /// <summary>
    /// Compute a unit normal to the plane defined by the corner list.
    /// Uses the first two non-collinear edges from <c>corners[0]</c>.
    /// The result always has Z ≥ 0 (points toward the upper face).
    /// </summary>
    internal static XYZ ComputeSlabNormal(IList<XYZ> corners)
    {
        if (corners.Count < 3) return new XYZ(0, 0, 1);

        XYZ origin = corners[0];
        XYZ? normal = null;

        for (int i = 1; i < corners.Count - 1 && normal == null; i++)
        {
            XYZ edge1 = corners[i].Subtract(origin);
            XYZ edge2 = corners[i + 1].Subtract(origin);
            XYZ cross = edge1.CrossProduct(edge2);
            if (cross.GetLength() > 1e-9)
                normal = cross.Normalize();
        }

        normal ??= new XYZ(0, 0, 1);

        // Ensure the normal points toward the upper face (positive Z for
        // inclined slabs, or correct side for near-vertical members).
        return normal.Z < 0 ? normal.Negate() : normal;
    }
}
