using Autodesk.Revit.DB;

namespace StructuralTools.StaircaseEngine;

/// <summary>
/// Resolves shared edges between adjacent panels by snapping their corner
/// XYZ coordinates to be exactly equal. This guarantees the Revit analytical
/// solver sees continuity at run→landing boundaries.
///
/// Algorithm:
///   For each unordered pair of adjacent nodes in the graph (processed once):
///     Get the trailing-edge corners of the lower node's last panel.
///     Get the leading-edge corners of the upper node's first panel.
///     For each upper-panel corner on the leading edge, find the nearest
///     lower-panel corner on the trailing edge. If within tolerance,
///     replace the upper corner with the lower corner (same XYZ reference).
/// </summary>
public static class ConnectivityResolver
{
    /// <summary>
    /// Snap shared edges between all adjacent panels in the graph.
    /// Modifies the <paramref name="panels"/> list in place.
    /// </summary>
    public static int ResolveSharedEdges(
        StairGraph graph,
        List<PanelGeometry> panels)
    {
        int snaps = 0;
        var visited = new HashSet<(ElementId, ElementId)>();

        foreach (var node in graph.Nodes)
        {
            foreach (var neighbor in node.ConnectedTo)
            {
                // Process each unordered pair only once.
                var key = (node.ElementId, neighbor.ElementId);
                var reverse = (neighbor.ElementId, node.ElementId);
                if (visited.Contains(key) || visited.Contains(reverse)) continue;
                visited.Add(key);

                // Get this node's panels and the neighbor's panels.
                var nodePanels = panels
                    .Where(p => p.SourceElementId == node.ElementId)
                    .OrderBy(p => p.Corners[0].Z)
                    .ToList();
                var neighborPanels = panels
                    .Where(p => p.SourceElementId == neighbor.ElementId)
                    .OrderBy(p => p.Corners[0].Z)
                    .ToList();

                if (nodePanels.Count == 0 || neighborPanels.Count == 0) continue;

                // Determine which node is the lower (run) and which is the upper (landing).
                bool nodeIsLower = node.BaseElevation <= neighbor.BaseElevation;
                var lowerPanels = nodeIsLower ? nodePanels : neighborPanels;
                var upperPanels = nodeIsLower ? neighborPanels : nodePanels;

                var lowerLast = lowerPanels.Last();
                var upperFirst = upperPanels.First();

                snaps += SnapSharedEdge(lowerLast, upperFirst);
            }
        }

        return snaps;
    }

    /// <summary>
    /// Snap the trailing edge of <paramref name="lower"/> to the leading edge
    /// of <paramref name="upper"/>. Modifies <paramref name="upper"/>'s corners
    /// in place.
    ///
    /// Uses proximity rather than Z-sort to find shared-edge corners.
    /// Z-sort is unreliable for flat landing panels (all corners at the same Z),
    /// where <c>Take(2)</c> would pick arbitrary corners instead of the two that
    /// face the adjacent run.
    /// </summary>
    private static int SnapSharedEdge(PanelGeometry lower, PanelGeometry upper)
    {
        int snaps = 0;

        // Select the 2 corners of the lower panel that are closest to any
        // corner of the upper panel — these are the lower panel's trailing edge,
        // regardless of whether the lower panel is inclined (run) or flat (landing).
        var lowerTop = lower.Corners
            .OrderBy(lc => upper.Corners.Min(uc => lc.DistanceTo(uc)))
            .Take(2)
            .ToList();

        // Select the 2 corners of the upper panel that are closest to any
        // corner of the lower panel — these are the upper panel's leading edge,
        // regardless of whether the upper panel is inclined or flat.
        var upperBottomIndices = upper.Corners
            .Select((c, i) => (c, i, dist: lower.Corners.Min(lc => c.DistanceTo(lc))))
            .OrderBy(t => t.dist)
            .Take(2)
            .Select(t => t.i)
            .ToHashSet();

        // For each leading-edge corner of the upper panel, find the nearest
        // trailing-edge corner of the lower panel and snap to it.
        for (int i = 0; i < upper.Corners.Count; i++)
        {
            if (!upperBottomIndices.Contains(i)) continue;

            var c = upper.Corners[i];
            XYZ? nearest = null;
            double nearestDist = double.MaxValue;
            foreach (var lc in lowerTop)
            {
                double d = c.DistanceTo(lc);
                if (d < nearestDist)
                {
                    nearestDist = d;
                    nearest = lc;
                }
            }

            if (nearest != null && nearestDist < EngineConfig.EdgeSnapTolerance)
            {
                upper.Corners[i] = nearest;
                snaps++;
            }
        }

        return snaps;
    }
}
