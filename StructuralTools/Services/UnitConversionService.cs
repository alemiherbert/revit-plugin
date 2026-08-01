using System.Globalization;
using Autodesk.Revit.DB;

namespace StructuralTools.Services;

/// <summary>
/// Pure unit-conversion helpers between Revit internal units and engineering units.
/// All methods fall back to a hard-coded constant if the Revit unit API throws,
/// so the engine keeps running even on unusual document unit configurations.
/// </summary>
public static class UnitConversionService
{
    // ---------------------------------------------------------------------
    // Hard-coded fallback constants. These are the conversions Revit itself
    // uses internally, so they only diverge from UnitUtils in pathological cases.
    // ---------------------------------------------------------------------

    /// <summary>1 ft = 0.3048 m</summary>
    private const double M_PER_FT = 0.3048;

    /// <summary>
    /// 1 kN/m³ = 0.101971621 kip/ft³ (Revit internal unit for unit weight is kip/ft³).
    /// </summary>
    private const double KIPFT3_PER_KNM3 = 0.101971621;

    /// <summary>
    /// 1 kg/m³ = 0.0624279606 lb/ft³ (Revit internal density unit is lb/ft³).
    /// </summary>
    private const double LBFT3_PER_KGM3 = 0.0624279606;

    /// <summary>
    /// 1 kN/m = 0.0685218 kip/ft (Revit internal force-per-length unit is kip/ft).
    /// </summary>
    private const double KIPFT_PER_KNM = 0.0685218;

    /// <summary>m/s² — used to convert density (kg/m³) to unit weight (kN/m³).</summary>
    private const double GRAVITY_M_S2 = 9.80665;

    /// <summary>
    /// Convert a length from Revit internal units (ft) to metres.
    /// </summary>
    public static double InternalLengthToM(double ft)
    {
        try { return UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters); }
        catch { return ft * M_PER_FT; }
    }

    /// <summary>
    /// Convert a unit weight from Revit internal units (kip/ft³) to kN/m³.
    /// </summary>
    public static double InternalUnitWeightToKnM3(double v)
    {
        try { return UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.KilonewtonsPerCubicMeter); }
        catch { return v / KIPFT3_PER_KNM3; }
    }

    /// <summary>
    /// Convert a density from Revit internal units (lb/ft³) to kg/m³.
    /// </summary>
    public static double InternalDensityToKgM3(double v)
    {
        try { return UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.KilogramsPerCubicMeter); }
        catch { return v / LBFT3_PER_KGM3; }
    }

    /// <summary>
    /// Convert a force-per-length from kN/m to Revit internal units (kip/ft).
    /// </summary>
    public static double KnPerMToInternal(double v)
    {
        try { return UnitUtils.ConvertToInternalUnits(v, UnitTypeId.KilonewtonsPerMeter); }
        catch { return v * KIPFT_PER_KNM; }
    }

    /// <summary>
    /// Convert a density in kg/m³ to a unit weight in kN/m³ (kg × g / 1000).
    /// </summary>
    public static double KgM3ToKnM3(double kgM3) => kgM3 * GRAVITY_M_S2 / 1000.0;

    /// <summary>
    /// Parse a culture-invariant numeric string (e.g. "10.5") into a double.
    /// Returns <c>false</c> on failure rather than throwing.
    /// </summary>
    public static bool TryParseInvariant(string? text, out double value)
        => double.TryParse((text ?? "").Trim(),
               NumberStyles.Any,
               CultureInfo.InvariantCulture,
               out value);
}
