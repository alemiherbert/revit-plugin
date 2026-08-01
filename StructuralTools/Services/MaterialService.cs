using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using StructuralTools.Services;

namespace StructuralTools.Services;

/// <summary>
/// Resolves a wall's self-weight per unit area (kN/m²) from its compound structure layers,
/// with per-material caching to avoid recomputing densities for shared wall types.
/// </summary>
public sealed class MaterialService
{
    private readonly Dictionary<ElementId, double> _weightCache = new();

    /// <summary>
    /// Compute the wall's area-weight (kN/m²) — i.e. the line load per metre of wall
    /// per metre of height. Walks each compound-structure layer; falls back to a
    /// single-bulk density if the wall has no compound structure.
    /// </summary>
    public double CalcWallAreaWeight(Wall wall, double fallbackGammaKnM3, List<string>? log = null)
    {
        if (wall == null) return 0.0;

        var wt = wall.WallType;
        CompoundStructure? cs = null;
        try { cs = wt.GetCompoundStructure(); }
        catch (Exception ex) { log?.Add($"[DEBUG] WallType {wt.Name}: GetCompoundStructure threw {ex.GetType().Name}"); }

        if (cs != null)
        {
            double total = 0.0;
            foreach (var layer in cs.GetLayers())
            {
                double tM = UnitConversionService.InternalLengthToM(layer.Width);
                Material? mat = (layer.MaterialId != ElementId.InvalidElementId)
                    ? wall.Document.GetElement(layer.MaterialId) as Material
                    : null;
                total += tM * GetMaterialUnitWeightCached(mat, fallbackGammaKnM3);
            }
            if (total > 0) return total;
        }

        return UnitConversionService.InternalLengthToM(wt.Width) * fallbackGammaKnM3;
    }

    /// <summary>
    /// Look up a material's unit weight (kN/m³), preferring the structural asset's
    /// density, then the PHY_MATERIAL_PARAM_UNIT_WEIGHT parameter, then the fallback.
    /// Cached per <see cref="Material.Id"/>.
    /// </summary>
    public double GetMaterialUnitWeightCached(Material? mat, double fallbackGammaKnM3)
    {
        if (mat == null) return fallbackGammaKnM3;

        if (_weightCache.TryGetValue(mat.Id, out double cached))
            return cached;

        double weight = ComputeMaterialUnitWeight(mat, fallbackGammaKnM3);
        _weightCache[mat.Id] = weight;
        return weight;
    }

    private static double ComputeMaterialUnitWeight(Material mat, double fallbackGammaKnM3)
    {
        // 1. Structural asset density (preferred).
        if (mat.StructuralAssetId != ElementId.InvalidElementId)
        {
            var pse = mat.Document.GetElement(mat.StructuralAssetId) as PropertySetElement;
            if (pse != null)
            {
                var sa = pse.GetStructuralAsset();
                if (sa != null && sa.Density > 0)
                {
                    double kgM3 = UnitConversionService.InternalDensityToKgM3(sa.Density);
                    return UnitConversionService.KgM3ToKnM3(kgM3);
                }
            }
        }

        // 2. Legacy PHY_MATERIAL_PARAM_UNIT_WEIGHT parameter.
        var p = mat.get_Parameter(BuiltInParameter.PHY_MATERIAL_PARAM_UNIT_WEIGHT);
        if (p != null && p.HasValue)
        {
            double uw = p.AsDouble();
            if (uw > 0) return UnitConversionService.InternalUnitWeightToKnM3(uw);
        }

        // 3. Caller-supplied fallback.
        return fallbackGammaKnM3;
    }
}
