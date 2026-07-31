using Autodesk.Revit.DB;

namespace StructuralTools.Models;

/// <summary>
/// Represents information about a staircase element for analytical model conversion.
/// </summary>
public class StaircaseInfo
{
    public ElementId StairId { get; set; } = ElementId.InvalidElementId;
    public string StairName { get; set; } = string.Empty;
    public ElementId? StairTypeId { get; set; }
    public IList<XYZ> TreadPoints { get; set; } = new List<XYZ>();
    public IList<XYZ> StringerLines { get; set; } = new List<XYZ>();
    public XYZ LandingLocation { get; set; } = XYZ.Zero;
    public double TotalHeight { get; set; }
    public double TotalRun { get; set; }
    public int NumberOfTreads { get; set; }
    public bool HasLandings { get; set; }
    public ElementId? MaterialId { get; set; }
    public bool IsLinked { get; set; }
    public Document? HostDocument { get; set; }
}

/// <summary>
/// Represents an analytical member to be created from staircase geometry.
/// </summary>
public class AnalyticalMember
{
    public XYZ StartPoint { get; set; } = XYZ.Zero;
    public XYZ EndPoint { get; set; } = XYZ.Zero;
    public AnalyticalMemberType MemberType { get; set; } = AnalyticalMemberType.Beam;
    public ElementId? MaterialId { get; set; }
    public string Description { get; set; } = string.Empty;
    public ElementId SourceStairId { get; set; } = ElementId.InvalidElementId;
}

/// <summary>
/// Types of analytical members that can be created.
/// </summary>
public enum AnalyticalMemberType
{
    Beam,
    Column,
    Brace,
    Floor
}
