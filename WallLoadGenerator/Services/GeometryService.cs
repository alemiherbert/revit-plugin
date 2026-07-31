using Autodesk.Revit.DB;
using WallLoadGenerator.Models;

namespace WallLoadGenerator.Services;

/// <summary>
/// Service for geometric calculations and spatial queries.
/// </summary>
public class GeometryService
{
    /// <summary>
    /// Checks if a point lies on a floor element.
    /// </summary>
    public bool PointIsOnFloor(XYZ point, Floor floor)
    {
        // Get floor boundary
        var options = new SpatialElementBoundaryOptions();
        var boundaries = floor.GetBoundarySegments(options);
        
        foreach (var boundary in boundaries)
        {
            foreach (var segment in boundary)
            {
                var curve = segment.GetCurve();
                
                // Check if point is within floor boundary
                if (IsPointInPolygon(point, curve))
                    return true;
            }
        }
        
        return false;
    }

    /// <summary>
    /// Simplified point-in-polygon check for floor boundaries.
    /// </summary>
    private bool IsPointInPolygon(XYZ point, Curve boundary)
    {
        // Project to 2D for simpler calculation
        var pt2D = new UV(point.X, point.Y);
        
        // Use Revit's built-in intersection test
        var intersect = boundary.Intersect(new Line(
            Line.CreateBound(
                new XYZ(point.X, point.Y, point.Z - 100),
                new XYZ(point.X, point.Y, point.Z + 100)
            )
        ));
        
        return intersect != SetComparisonResult.Empty;
    }

    /// <summary>
    /// Determines if two line loads are coincident (on the same edge).
    /// </summary>
    public bool LoadsAreCoincident(WallLoad load1, WallLoad load2, double tolerancePercent)
    {
        // Check if loads are on the same floor
        if (load1.FloorId != load2.FloorId)
            return false;
        
        // Check if start and end points are close enough
        double tolerance = UnitConversionService.FeetToMm(10) * (tolerancePercent / 100.0);
        
        bool startClose = Distance(load1.StartPoint, load2.StartPoint) < tolerance;
        bool endClose = Distance(load1.EndPoint, load2.EndPoint) < tolerance;
        
        bool reverseStartClose = Distance(load1.StartPoint, load2.EndPoint) < tolerance;
        bool reverseEndClose = Distance(load1.EndPoint, load2.StartPoint) < tolerance;
        
        return (startClose && endClose) || (reverseStartClose && reverseEndClose);
    }

    /// <summary>
    /// Calculates distance between two points.
    /// </summary>
    public double Distance(XYZ p1, XYZ p2)
    {
        return p1.DistanceTo(p2);
    }

    /// <summary>
    /// Projects a wall location line onto a floor plane.
    /// </summary>
    public Line? ProjectLineOntoFloor(Line wallLine, Floor floor)
    {
        // Get floor elevation
        var levelId = floor.LevelId;
        if (levelId == null || levelId == ElementId.InvalidElementId)
            return null;
            
        var level = GetDocument(floor.Document).GetElement(levelId) as Level;
        if (level == null)
            return null;
        
        // Project line endpoints to floor elevation
        var floorElevation = level.Elevation;
        
        var startProjected = new XYZ(wallLine.GetEndPoint(0).X, wallLine.GetEndPoint(0).Y, floorElevation);
        var endProjected = new XYZ(wallLine.GetEndPoint(1).X, wallLine.GetEndPoint(1).Y, floorElevation);
        
        return Line.CreateBound(startProjected, endProjected);
    }

    /// <summary>
    /// Finds intersection points between a wall and floor edges.
    /// </summary>
    public List<XYZ> FindWallFloorIntersections(WallInfo wall, Floor floor)
    {
        var intersections = new List<XYZ>();
        
        var wallLine = Line.CreateBound(wall.LocationStart, wall.LocationEnd);
        
        var options = new SpatialElementBoundaryOptions();
        var boundaries = floor.GetBoundarySegments(options);
        
        foreach (var boundary in boundaries)
        {
            foreach (var segment in boundary)
            {
                var floorEdge = segment.GetCurve();
                
                var result = wallLine.Intersect(floorEdge);
                if (result == SetComparisonResult.Overlap || result == SetComparisonResult.Subset)
                {
                    // Lines overlap or intersect
                    // Get intersection point(s)
                    var intersectionsRaw = new IntersectionResultArray();
                    if (wallLine.Intersect(floorEdge, out intersectionsRaw) == SetComparisonResult.Overlap)
                    {
                        for (int i = 0; i < intersectionsRaw.Size; i++)
                        {
                            intersections.Add(intersectionsRaw.get_Item(i).XYZPoint);
                        }
                    }
                }
            }
        }
        
        return intersections;
    }

    /// <summary>
    /// Checks if two curves are parallel.
    /// </summary>
    public bool CurvesAreParallel(Curve c1, Curve c2, double angularTolerance = 0.01)
    {
        var dir1 = c1.Direction;
        var dir2 = c2.Direction;
        
        double dot = dir1.DotProduct(dir2);
        return Math.Abs(Math.Abs(dot) - 1.0) < angularTolerance;
    }

    /// <summary>
    /// Gets the document from an element.
    /// </summary>
    private Document GetDocument(Element element) => element.Document;
}
