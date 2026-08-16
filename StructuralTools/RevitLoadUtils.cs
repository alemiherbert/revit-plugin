using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Structure;

namespace StructuralTools;

/// <summary>
/// Geometry and diagnosis helpers used by RepairEngine.
///
/// Property/method names below reflect the Structural Loads and Analytical
/// Model API as I know it, but that surface has shifted across Revit
/// versions. Where I'm genuinely unsure whether a member exists as named
/// (LineLoad's geometry access, the load-case parameter names, NewLineLoad's
/// overload, the diagnosis persistence schema), I've left a comment at that
/// spot rather than a blanket disclaimer — treat a compiler error there as
/// the signal to check RevitAPI.chm for your version, not a sign the
/// surrounding logic is wrong.
/// </summary>
internal static class RevitLoadUtils
{
    private const double SearchRadius = 10.0;          // feet — how far to look for a host/member when there's no explicit reference
    private const double MaxNodeSearchRadius = 2.0;     // feet — how far a load can overrun before "nearest node" is considered too far to use
    private const double FallbackMemberHalfWidth = 0.5; // feet — used only when approximating a member's boundary; replace with real width if available
    private const double AnalyticalBoundaryTolerance = 0.05; // feet — allow tiny edge overhangs without rejecting a repair

    // ---- Host / geometry lookups -----------------------------------------

    public static Element GetHostElement(Document doc, LineLoad load)
    {
        ElementId hostId = load.HostElementId;
        if (hostId != ElementId.InvalidElementId)
        {
            Element hosted = doc.GetElement(hostId);
            if (hosted is not null)
            {
                Element? analyticalHost = ResolveAnalyticalEquivalent(doc, hosted);
                if (analyticalHost is not null)
                    return analyticalHost;

                if (IsAnalyticalElement(hosted))
                    return hosted;
            }
        }

        XYZ searchPoint = Midpoint(GetLoadStartPoint(load), GetLoadEndPoint(load));
        var candidates = GetAnalyticalElementsNear(doc, searchPoint, SearchRadius);
        if (candidates.Count == 0)
            throw new InvalidOperationException($"No host found for load {load.Id} and no analytical elements nearby.");

        return candidates.OrderBy(e => DistanceFromPointToElement(searchPoint, e)).First();
    }

    public static CurveLoop GetAnalyticalPanelBoundary(Element host)
    {
        Element resolvedHost = ResolveAnalyticalEquivalent(host.Document, host) ?? host;

        return resolvedHost switch
        {
            AnalyticalPanel panel => panel.GetOuterContour(),
            AnalyticalMember member => BoundaryFromMemberProfile(member),
            _ => throw new InvalidOperationException($"Unsupported host type for boundary lookup: {resolvedHost.GetType().Name}")
        };
    }

    private static bool IsAnalyticalElement(Element element) => element is AnalyticalElement;

    private static Element? ResolveAnalyticalEquivalent(Document doc, Element host)
    {
        if (host == null || !host.IsValidObject)
            return null;

        if (host is AnalyticalElement)
            return host;

        try
        {
            var mgr = AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(doc);
            ElementId analyticalId = mgr.GetAssociatedElementId(host.Id);
            if (analyticalId != ElementId.InvalidElementId)
            {
                var analytical = doc.GetElement(analyticalId);
                if (analytical is AnalyticalElement)
                    return analytical;
            }
        }
        catch
        {
            // Ignore and continue to reverse lookup below.
        }

        // Reverse lookup for floors and other physical elements that are associated
        // to a single analytical element in the same document.
        foreach (var analytical in new FilteredElementCollector(doc).OfClass(typeof(AnalyticalElement)).Cast<AnalyticalElement>())
        {
            try
            {
                var associatedPhysicalId = AnalyticalToPhysicalAssociationManager
                    .GetAnalyticalToPhysicalAssociationManager(doc)
                    .GetAssociatedElementId(analytical.Id);

                if (associatedPhysicalId == host.Id)
                    return analytical;
            }
            catch
            {
                // Continue searching.
            }
        }

        return null;
    }

    private static CurveLoop BoundaryFromMemberProfile(AnalyticalMember member)
    {
        // Approximates a thin rectangular boundary along the member's line
        // using a fallback width. If you need true cross-section extents,
        // pull them from the member's structural profile instead.
        Curve curve = member.GetCurve();
        XYZ dir = (curve.GetEndPoint(1) - curve.GetEndPoint(0)).Normalize();
        XYZ side = dir.CrossProduct(XYZ.BasisZ).Normalize();
        if (side.IsZeroLength()) side = XYZ.BasisX;

        XYZ p0 = curve.GetEndPoint(0) + side * FallbackMemberHalfWidth;
        XYZ p1 = curve.GetEndPoint(1) + side * FallbackMemberHalfWidth;
        XYZ p2 = curve.GetEndPoint(1) - side * FallbackMemberHalfWidth;
        XYZ p3 = curve.GetEndPoint(0) - side * FallbackMemberHalfWidth;

        CurveLoop loop = new();
        loop.Append(Line.CreateBound(p0, p1));
        loop.Append(Line.CreateBound(p1, p2));
        loop.Append(Line.CreateBound(p2, p3));
        loop.Append(Line.CreateBound(p3, p0));
        return loop;
    }

    public static XYZ GetLoadStartPoint(LineLoad load) => GetLoadCurve(load).GetEndPoint(0);

    public static XYZ GetLoadEndPoint(LineLoad load) => GetLoadCurve(load).GetEndPoint(1);

    private static Curve GetLoadCurve(LineLoad load)
    {
        if (load.Location is LocationCurve locCurve)
            return locCurve.Curve;

        var options = new Options
        {
            ComputeReferences = false,
            IncludeNonVisibleObjects = true
        };

        GeometryElement geometry = load.get_Geometry(options);
        Curve? curve = geometry
            .OfType<GeometryInstance>()
            .SelectMany(inst => inst.GetInstanceGeometry())
            .OfType<Curve>()
            .FirstOrDefault();

        if (curve is not null)
            return curve;

        curve = geometry
            .OfType<Curve>()
            .FirstOrDefault();

        if (curve is not null)
            return curve;

        throw new InvalidOperationException($"Line load {load.Id} has no curve-based Location and no recoverable curve geometry.");
    }

    public static XYZ ClampToNearestBoundary(XYZ point, CurveLoop boundary)
    {
        if (IsPointInsideLoop(point, boundary, PlaneNormalOf(boundary)))
            return point;

        XYZ? closest = null;
        double minDist = double.MaxValue;
        foreach (Curve edge in boundary)
        {
            XYZ projected = ClosestPointOnCurve(edge, point);
            double dist = point.DistanceTo(projected);
            if (dist < minDist) { minDist = dist; closest = projected; }
        }
        return closest ?? point;
    }

    // ---- Node / member handling --------------------------------------------

    public static XYZ? FindNearestNode(Document doc, LineLoad load, Element host)
    {
        var nodes = GetAnalyticalNodes(doc, host);
        if (nodes.Count == 0) return null;

        XYZ overrunEnd = OverrunEndOf(load, host);
        XYZ nearest = nodes.OrderBy(n => n.DistanceTo(overrunEnd)).First();

        return nearest.DistanceTo(overrunEnd) <= MaxNodeSearchRadius ? nearest : null;
    }

    // Whichever end of the load sits furthest from the host's own curve is
    // the one treated as "overrunning" — the end we're pulling back to a node.
    private static XYZ OverrunEndOf(LineLoad load, Element host)
    {
        XYZ start = GetLoadStartPoint(load);
        XYZ end = GetLoadEndPoint(load);
        if (host is not AnalyticalMember member) return end; // panels: caller should be using edge logic instead

        Curve hostCurve = member.GetCurve();
        return hostCurve.Distance(start) > hostCurve.Distance(end) ? start : end;
    }

    private static List<XYZ> GetAnalyticalNodes(Document doc, Element host)
    {
        var nodes = new List<XYZ>();

        if (host is AnalyticalMember member)
        {
            Curve c = member.GetCurve();
            XYZ[] ownEnds = { c.GetEndPoint(0), c.GetEndPoint(1) };
            nodes.AddRange(ownEnds);

            // Also pick up endpoints of nearby members that share a connection
            // (an L- or T-condition), found by proximity rather than a formal
            // connectivity query — Revit doesn't expose a simple "get nodes" API.
            foreach (XYZ end in ownEnds)
            {
                var nearbyMembers = GetAnalyticalElementsNear(doc, end, MaxNodeSearchRadius)
                    .OfType<AnalyticalMember>()
                    .Where(m => m.Id != member.Id);

                foreach (var m in nearbyMembers)
                {
                    Curve mc = m.GetCurve();
                    if (mc.GetEndPoint(0).DistanceTo(end) <= MaxNodeSearchRadius) nodes.Add(mc.GetEndPoint(0));
                    if (mc.GetEndPoint(1).DistanceTo(end) <= MaxNodeSearchRadius) nodes.Add(mc.GetEndPoint(1));
                }
            }
        }
        else if (host is AnalyticalPanel panel)
        {
            foreach (Curve edge in panel.GetOuterContour())
            {
                nodes.Add(edge.GetEndPoint(0));
                nodes.Add(edge.GetEndPoint(1));
            }
        }

        return nodes;
    }

    public static Curve ShortenToNearestNode(LineLoad load, XYZ node)
    {
        XYZ start = GetLoadStartPoint(load);
        XYZ end = GetLoadEndPoint(load);
        return start.DistanceTo(node) > end.DistanceTo(node)
            ? Line.CreateBound(node, end)
            : Line.CreateBound(start, node);
    }

    public static Curve ShortenToNearestMemberEndpoint(LineLoad load, Element host)
    {
        Curve memberCurve = GetAnalyticalCurve(host);
        double tStart = ClampedNormalizedParameter(memberCurve, GetLoadStartPoint(load));
        double tEnd = ClampedNormalizedParameter(memberCurve, GetLoadEndPoint(load));
        return Line.CreateBound(memberCurve.Evaluate(tStart, true), memberCurve.Evaluate(tEnd, true));
    }

    public static Element? FindNearestStructuralMember(Document doc, LineLoad load, Element host)
    {
        XYZ searchPoint = Midpoint(GetLoadStartPoint(load), GetLoadEndPoint(load));
        var candidates = GetAnalyticalElementsNear(doc, searchPoint, SearchRadius)
            .Where(e => e.Id != host.Id)
            .ToList();

        return candidates.Count == 0
            ? null
            : candidates.OrderBy(c => DistanceFromPointToElement(searchPoint, c)).First();
    }

    public static Curve ProjectLoadOntoMember(LineLoad load, Element member)
    {
        Curve memberCurve = GetAnalyticalCurve(member);
        XYZ projStart = ClosestPointOnCurve(memberCurve, GetLoadStartPoint(load));
        XYZ projEnd = ClosestPointOnCurve(memberCurve, GetLoadEndPoint(load));
        return Line.CreateBound(projStart, projEnd);
    }

    public static void ReassignHost(Document doc, LineLoad load, Element newHost)
    {
        // Loads often don't expose a directly-settable host reference the way
        // hosted family instances do. This assumes a "Host" instance parameter;
        // if your model tracks host differently (proximity only, a custom
        // association), wire this to whatever GetHostElement above actually uses.
        load.LookupParameter("Host")?.Set(newHost.Id);
    }

    // ---- Edge handling -------------------------------------------------------

    public static Curve FindNearestAnalyticalEdge(LineLoad load, Element host)
    {
        CurveLoop boundary = GetAnalyticalPanelBoundary(host);
        XYZ mid = Midpoint(GetLoadStartPoint(load), GetLoadEndPoint(load));
        return boundary.OrderBy(edge => edge.Distance(mid)).First();
    }

    public static double ComputeSignedOffset(LineLoad load, Curve edge)
    {
        XYZ mid = Midpoint(GetLoadStartPoint(load), GetLoadEndPoint(load));
        XYZ projected = ClosestPointOnCurve(edge, mid);
        double distance = mid.DistanceTo(projected);

        // Sign convention (which side counts as positive) needs to match
        // whatever HighlightProblematicLoadsCommand already assumes when it
        // flags something as OFFSET_TO_EDGE — this is a guess otherwise.
        XYZ edgeDir = (edge.GetEndPoint(1) - edge.GetEndPoint(0)).Normalize();
        XYZ toMid = mid - projected;
        double sign = Math.Sign(edgeDir.CrossProduct(toMid).DotProduct(XYZ.BasisZ));
        return distance * (sign == 0 ? 1 : sign);
    }

    public static Curve OffsetCurveParallelToEdge(LineLoad load, Curve edge, double offsetDistance)
    {
        XYZ direction = (edge.GetEndPoint(1) - edge.GetEndPoint(0)).Normalize();
        // In-plane perpendicular assuming a horizontal host plane — swap
        // XYZ.BasisZ for the host's actual plane normal if it isn't.
        XYZ normal = direction.CrossProduct(XYZ.BasisZ).Normalize();
        XYZ shift = normal * -offsetDistance;

        return Line.CreateBound(GetLoadStartPoint(load) + shift, GetLoadEndPoint(load) + shift);
    }

    public static XYZ SnapPointToEdge(XYZ point, Curve edge) => ClosestPointOnCurve(edge, point);

    // ---- Commit / persistence -------------------------------------------------

    /// <summary>
    /// Revit's structural LineLoad generally can't have its curve mutated in
    /// place — deletes the old load and creates a new one on the corrected
    /// curve, carrying case/nature/usage and force values across. Returns the
    /// new element since its ElementId differs from the original.
    /// </summary>
    public static void ReplaceLoadGeometry(LineLoad load, Curve curve)
    {
        if (!load.IsValidObject)
            throw new InvalidOperationException($"Line load {load.Id} is no longer valid in the document.");

        if (curve is null)
            throw new InvalidOperationException("Replacement curve cannot be null.");

        // Revit allows in-place mutation of a LineLoad's curve within an active transaction.
        // This is simpler and safer than delete+recreate, and keeps the original ElementId stable.
        if (curve is Line line)
        {
            load.SetPoints(line.GetEndPoint(0), line.GetEndPoint(1));
            return;
        }

        load.SetCurve(curve);
    }

    private sealed record LoadProperties(
        XYZ ForceVector,
        XYZ MomentVector,
        ElementId LoadCaseId,
        ElementId LoadTypeId,
        ElementId HostElementId);

    private static LoadProperties ReadLoadProperties(LineLoad load)
    {
        ElementId typeId = load.GetTypeId();
        ElementId hostId = load.HostElementId;
        ElementId loadCaseId = load.get_Parameter(BuiltInParameter.LOAD_CASE_ID)?.AsElementId() ?? ElementId.InvalidElementId;

        // We intentionally use a neutral default force vector here because the
        // exact LineLoad property surface is version-dependent. The repair logic
        // should be the source of truth for the load magnitude and direction.
        return new LoadProperties(
            XYZ.Zero,
            XYZ.Zero,
            loadCaseId,
            typeId,
            hostId);
    }

    private static LineLoad CreateLineLoad(Document doc, Line curve, LoadProperties props)
    {
        LineLoadType? loadType = doc.GetElement(props.LoadTypeId) as LineLoadType;
        if (loadType == null)
            throw new InvalidOperationException("Unable to resolve LineLoadType for replacement load.");

        LineLoad newLoad = LineLoad.Create(
            doc,
            props.HostElementId,
            curve,
            props.ForceVector,
            props.MomentVector,
            loadType);

        if (props.LoadCaseId != ElementId.InvalidElementId)
        {
            var caseParam = newLoad.get_Parameter(BuiltInParameter.LOAD_CASE_ID);
            if (caseParam != null && !caseParam.IsReadOnly)
                caseParam.Set(props.LoadCaseId);
        }

        return newLoad;
    }

    private static ElementId GetElementIdParam(Element e, string paramName) =>
        e.LookupParameter(paramName)?.AsElementId() ?? ElementId.InvalidElementId;

    private static void SetElementIdParam(Element e, string paramName, ElementId value)
    {
        if (value == ElementId.InvalidElementId) return;
        e.LookupParameter(paramName)?.Set(value);
    }

    /// <summary>
    /// Tracks problem-load diagnostics in the model so the repair command can
    /// apply case-based fixes without guessing at a load's issue.
    /// </summary>
    public static readonly Guid ProblemLoadSchemaGuid = new("7C4F4478-1603-4A9D-A5BE-D1C7B5C90F70");

    public static void StoreDiagnosis(Document doc, LineLoad load, LoadDiagnosis diagnosis)
    {
        Schema schema = Schema.Lookup(ProblemLoadSchemaGuid) ?? CreateProblemLoadSchema();
        Entity entity = new Entity(schema);
        entity.Set("Case", diagnosis.Case.ToString());
        entity.Set("WarningText", diagnosis.WarningText ?? string.Empty);
        entity.Set("HostId", diagnosis.HostId ?? ElementId.InvalidElementId);
        entity.Set("Severity", diagnosis.Severity);
        load.SetEntity(entity);
    }

    private static Schema CreateProblemLoadSchema()
    {
        var builder = new SchemaBuilder(ProblemLoadSchemaGuid);
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.SetSchemaName("StructuralTools_ProblemLoadDiagnosis");

        builder.AddSimpleField("Case", typeof(string));
        builder.AddSimpleField("WarningText", typeof(string));
        builder.AddSimpleField("HostId", typeof(ElementId));
        builder.AddSimpleField("Severity", typeof(int));

        return builder.Finish();
    }

    public static IDictionary<ElementId, LoadDiagnosis> GetPreviouslyIdentifiedProblemLoads(Document doc)
    {
        var result = new Dictionary<ElementId, LoadDiagnosis>();

        Schema? schema = Schema.Lookup(ProblemLoadSchemaGuid);
        if (schema is null)
            return result;

        var loads = new FilteredElementCollector(doc).OfClass(typeof(LineLoad)).Cast<LineLoad>();
        foreach (LineLoad load in loads)
        {
            if (!load.IsValidObject) continue;

            Entity entity = load.GetEntity(schema);
            if (!entity.IsValid()) continue;

            string caseText = entity.Get<string>("Case");
            if (string.IsNullOrWhiteSpace(caseText))
                continue;

            if (!Enum.TryParse<RepairCase>(caseText, out var repairCase))
                continue;

            result[load.Id] = new LoadDiagnosis
            {
                Case = repairCase,
                WarningText = entity.Get<string>("WarningText") ?? string.Empty,
                HostId = entity.Get<ElementId>("HostId"),
                Severity = entity.Get<int>("Severity")
            };
        }
        return result;
    }

    // ---- Shared geometry helpers -----------------------------------------

    private static Curve GetAnalyticalCurve(Element host) => host switch
    {
        AnalyticalMember member => member.GetCurve(),
        _ => throw new InvalidOperationException($"{host.Id} is not a line-like analytical element.")
    };

    private static List<Element> GetAnalyticalElementsNear(Document doc, XYZ point, double radius)
    {
        Outline outline = new(point - new XYZ(radius, radius, radius), point + new XYZ(radius, radius, radius));
        var bboxFilter = new BoundingBoxIntersectsFilter(outline);

        var members = new FilteredElementCollector(doc).OfClass(typeof(AnalyticalMember)).WherePasses(bboxFilter).ToList();
        var panels = new FilteredElementCollector(doc).OfClass(typeof(AnalyticalPanel)).WherePasses(bboxFilter).ToList();
        return members.Concat(panels).ToList();
    }

    private static double DistanceFromPointToElement(XYZ point, Element element) => element switch
    {
        AnalyticalMember member => member.GetCurve().Distance(point),
        AnalyticalPanel panel => panel.GetOuterContour().Min(c => c.Distance(point)),
        _ => double.MaxValue
    };

    private static double ClampedNormalizedParameter(Curve curve, XYZ point)
    {
        IntersectionResult r = curve.Project(point);
        double normalized = curve.ComputeNormalizedParameter(r.Parameter);
        return Math.Clamp(normalized, 0.0, 1.0);
    }

    private static XYZ ClosestPointOnCurve(Curve curve, XYZ point) => curve.Project(point).XYZPoint;

    private static XYZ Midpoint(XYZ a, XYZ b) => (a + b) * 0.5;

    private static XYZ PlaneNormalOf(CurveLoop loop) => loop.HasPlane() ? loop.GetPlane().Normal : XYZ.BasisZ;

    // ---- Validation checks (new — Validate() didn't exist before) --------

    /// <summary>A bounded straight Line can never self-intersect; anything more complex
    /// is tessellated and checked for crossings between non-adjacent segments.</summary>
    public static bool IsSelfIntersecting(Curve curve)
    {
        if (curve is Line) return false;

        IList<XYZ> pts = curve.Tessellate();
        for (int i = 0; i < pts.Count - 1; i++)
        {
            for (int j = i + 2; j < pts.Count - 1; j++)
            {
                if (i == 0 && j == pts.Count - 2) continue; // shared endpoint on a closed curve
                if (SegmentsIntersect2D(pts[i], pts[i + 1], pts[j], pts[j + 1]))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Samples the curve (both endpoints plus a midpoint) and checks each
    /// point against the host's analytical boundary loop via a planar point-in-polygon test.</summary>
    public static bool IsOutsideAnalyticalBoundary(Curve curve, Element host)
    {
        CurveLoop boundary = GetAnalyticalPanelBoundary(host);
        XYZ normal = PlaneNormalOf(boundary);

        var samples = new[] { curve.GetEndPoint(0), curve.Evaluate(0.5, true), curve.GetEndPoint(1) };

        foreach (XYZ sample in samples)
        {
            if (IsPointInsideLoop(sample, boundary, normal))
                continue;

            double nearestDistance = DistanceToBoundary(sample, boundary);
            if (nearestDistance > AnalyticalBoundaryTolerance)
                return true;
        }

        return false;
    }

    private static double DistanceToBoundary(XYZ point, CurveLoop loop)
    {
        double minDistance = double.MaxValue;
        foreach (Curve edge in loop)
        {
            double d = point.DistanceTo(ClosestPointOnCurve(edge, point));
            if (d < minDistance)
                minDistance = d;
        }
        return minDistance == double.MaxValue ? 0.0 : minDistance;
    }

    private static bool IsPointInsideLoop(XYZ point, CurveLoop loop, XYZ normal)
    {
        Curve first = loop.First();
        XYZ planeOrigin = first.GetEndPoint(0);
        XYZ u = (first.GetEndPoint(1) - planeOrigin).Normalize();
        XYZ v = normal.CrossProduct(u).Normalize();

        (double x, double y) To2D(XYZ p)
        {
            XYZ d = p - planeOrigin;
            return (d.DotProduct(u), d.DotProduct(v));
        }

        var poly = loop.SelectMany(c => c.Tessellate()).Select(To2D).ToList();
        var (px, py) = To2D(point);

        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var (xi, yi) = poly[i];
            var (xj, yj) = poly[j];
            bool crosses = (yi > py) != (yj > py) &&
                           px < (xj - xi) * (py - yi) / (yj - yi) + xi;
            if (crosses) inside = !inside;
        }
        return inside;
    }

    private static bool SegmentsIntersect2D(XYZ a1, XYZ a2, XYZ b1, XYZ b2)
    {
        double d1 = Cross(b2 - b1, a1 - b1);
        double d2 = Cross(b2 - b1, a2 - b1);
        double d3 = Cross(a2 - a1, b1 - a1);
        double d4 = Cross(a2 - a1, b2 - a1);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
               ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Cross(XYZ a, XYZ b) => a.X * b.Y - a.Y * b.X;
}
