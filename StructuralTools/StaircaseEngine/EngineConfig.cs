namespace StructuralTools.StaircaseEngine;

/// <summary>
/// Global constants and tolerances for the staircase analytical-model engine.
/// All values are in Revit internal units (feet) unless noted.
/// </summary>
public static class EngineConfig
{
    /// <summary>ft — for shared-edge matching between adjacent panels.</summary>
    public const double EdgeSnapTolerance = 0.01;

    /// <summary>ft — clamp for curved/winder inner edge (prevents collapse to zero).</summary>
    public const double MinInnerRadius = 0.10;

    /// <summary>ft — below this inner radius, collapse quad to triangle.</summary>
    public const double MinQuadThreshold = 0.05;

    /// <summary>ft — ~200 mm fallback waist thickness.</summary>
    public const double FallbackWaistDepth = 0.656;

    /// <summary>ft — minimum edge length Revit accepts for a Line (~3.7 mm).</summary>
    public const double MinEdgeFt = 0.012;

    /// <summary>ft — default run width when ActualRunWidth and bbox both fail (~1.2 m).</summary>
    public const double FallbackRunWidth = 4.0;
}
