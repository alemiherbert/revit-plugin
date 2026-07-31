using Autodesk.Revit.DB;

namespace StructuralTools.Services;

/// <summary>
/// Service for geometry calculations and intersections.
/// </summary>
public class GeometryService
{
    /// <summary>
    /// Calculates the distance between two line segments in 3D.
    /// </summary>
    public static double DistanceBetweenLines(XYZ start1, XYZ end1, XYZ start2, XYZ end2)
    {
        var dir1 = (end1 - start1).Normalize();
        var dir2 = (end2 - start2).Normalize();
        
        var cross = dir1.CrossProduct(dir2);
        var crossNorm = cross.Normalize();
        
        if (crossNorm.IsZeroLength())
        {
            // Lines are parallel
            return DistancePointToLine(start1, start2, end2);
        }
        
        var delta = start2 - start1;
        return Math.Abs(delta.DotProduct(crossNorm));
    }

    /// <summary>
    /// Calculates the distance from a point to a line segment.
    /// </summary>
    public static double DistancePointToLine(XYZ point, XYZ lineStart, XYZ lineEnd)
    {
        var lineVec = lineEnd - lineStart;
        var pointVec = point - lineStart;
        
        double lineLen = lineVec.Length;
        if (lineLen < 1e-10)
            return pointVec.Length;
            
        double proj = pointVec.DotProduct(lineVec) / (lineLen * lineLen);
        proj = Math.Max(0, Math.Min(1, proj));
        
        var closest = lineStart + proj * lineVec;
        return point.DistanceTo(closest);
    }

    /// <summary>
    /// Checks if two line segments are approximately collinear.
    /// </summary>
    public static bool AreLinesCollinear(XYZ start1, XYZ end1, XYZ start2, XYZ end2, double tolerance = 0.01)
    {
        var dir1 = (end1 - start1).Normalize();
        var dir2 = (end2 - start2).Normalize();
        
        // Check if directions are parallel
        double dot = Math.Abs(dir1.DotProduct(dir2));
        if (dot < 0.99) // Not parallel
            return false;
            
        // Check if start2 is close to line1
        double dist = DistancePointToLine(start2, start1, end1);
        return dist < tolerance;
    }

    /// <summary>
    /// Finds the intersection point of two lines in the XY plane.
    /// Returns null if lines are parallel or don't intersect within segments.
    /// </summary>
    public static XYZ? FindIntersection2D(XYZ start1, XYZ end1, XYZ start2, XYZ end2)
    {
        double x1 = start1.X, y1 = start1.Y;
        double x2 = end1.X, y2 = end1.Y;
        double x3 = start2.X, y3 = start2.Y;
        double x4 = end2.X, y4 = end2.Y;
        
        double denom = (y4 - y3) * (x2 - x1) - (x4 - x3) * (y2 - y1);
        
        if (Math.Abs(denom) < 1e-10)
            return null; // Parallel
            
        double ua = ((x4 - x3) * (y1 - y3) - (y4 - y3) * (x1 - x3)) / denom;
        double ub = ((x2 - x1) * (y1 - y3) - (y2 - y1) * (x1 - x3)) / denom;
        
        if (ua < 0 || ua > 1 || ub < 0 || ub > 1)
            return null; // Intersection outside segments
            
        return new XYZ(x1 + ua * (x2 - x1), y1 + ua * (y2 - y1), (start1.Z + end1.Z) / 2);
    }

    /// <summary>
    /// Projects a point onto a plane defined by origin and normal.
    /// </summary>
    public static XYZ ProjectPointToPlane(XYZ point, XYZ planeOrigin, XYZ planeNormal)
    {
        var normalized = planeNormal.Normalize();
        var toPoint = point - planeOrigin;
        double dist = toPoint.DotProduct(normalized);
        return point - dist * normalized;
    }

    /// <summary>
    /// Calculates the area of a polygon defined by points in order.
    /// Assumes points are coplanar.
    /// </summary>
    public static double CalculatePolygonArea(IList<XYZ> points)
    {
        if (points.Count < 3) return 0;
        
        // Use shoelace formula projected to XY plane
        double area = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var p1 = points[i];
            var p2 = points[(i + 1) % points.Count];
            area += p1.X * p2.Y - p2.X * p1.Y;
        }
        
        return Math.Abs(area) / 2;
    }

    /// <summary>
    /// Gets the centroid of a list of points.
    /// </summary>
    public static XYZ GetCentroid(IList<XYZ> points)
    {
        if (points.Count == 0) return XYZ.Zero;
        
        double x = 0, y = 0, z = 0;
        foreach (var pt in points)
        {
            x += pt.X;
            y += pt.Y;
            z += pt.Z;
        }
        
        return new XYZ(x / points.Count, y / points.Count, z / points.Count);
    }
}
