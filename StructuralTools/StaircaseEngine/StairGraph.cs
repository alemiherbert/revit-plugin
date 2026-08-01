using Autodesk.Revit.DB;

namespace StructuralTools.StaircaseEngine;

/// <summary>
/// Type of stair component node in the <see cref="StairGraph"/>.
/// </summary>
public enum StairNodeType
{
    Run,
    Landing
}

/// <summary>
/// Sub-classification of a Run node.
/// Only sketch-based straight runs are supported; curved and winder
/// runs are not processed.
/// </summary>
public enum RunTag
{
    StraightRun
}

/// <summary>
/// One node in the <see cref="StairGraph"/> — represents a single StairsRun
/// or StairsLanding with its classification and adjacency information.
/// </summary>
public class StairNode
{
    public ElementId ElementId { get; set; } = ElementId.InvalidElementId;
    public StairNodeType Type { get; set; }
    public RunTag? Tag { get; set; }  // null for landings

    /// <summary>Adjacent nodes (connected via shared edges).</summary>
    public List<StairNode> ConnectedTo { get; } = new();

    /// <summary>Base elevation in absolute model Z (feet).</summary>
    public double BaseElevation { get; set; }

    /// <summary>Top elevation in absolute model Z (feet).</summary>
    public double TopElevation { get; set; }

    /// <summary>Concrete reference to the Revit element.</summary>
    public Element? SourceElement { get; set; }

    /// <summary>Convenience: true if this is a branching node (degree > 2).</summary>
    public bool IsBranching => ConnectedTo.Count > 2;
}

/// <summary>
/// Graph of a stair's structural topology — nodes are runs/landings, edges
/// are shared boundaries. Used to drive engine routing and connectivity
/// resolution.
/// </summary>
public class StairGraph
{
    public List<StairNode> Nodes { get; } = new();

    /// <summary>The lowest-elevation node — entry point of the stair.</summary>
    public StairNode? RootNode { get; set; }

    /// <summary>True for Split Straight Stairs (any landing with degree > 2).</summary>
    public bool IsBranching { get; set; }

    /// <summary>
    /// Return all nodes sorted bottom-to-top by base elevation.
    /// Secondary sort: runs before landings at the same elevation (so the
    /// run's trailing edge snaps to the landing's leading edge correctly
    /// in <see cref="ConnectivityResolver"/>).
    /// </summary>
    public IEnumerable<StairNode> NodesBottomToTop()
        => Nodes.OrderBy(n => n.BaseElevation)
                .ThenBy(n => n.Type == StairNodeType.Run ? 0 : 1);
}
