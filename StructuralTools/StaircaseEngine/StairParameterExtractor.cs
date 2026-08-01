using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace StructuralTools.StaircaseEngine;

/// <summary>
/// Extracts the concrete material ID, waist thickness, and run width from a
/// stair's type. Used to set <see cref="Autodesk.Revit.DB.Structure.AnalyticalPanel.MaterialId"/>,
/// the panel's thickness, and the panel's width.
/// </summary>
public static class StairParameterExtractor
{
    /// <summary>
    /// Get the waist thickness (in feet) from the stair or run type.
    /// Tries common parameter names; falls back to <see cref="EngineConfig.FallbackWaistDepth"/>.
    /// </summary>
    public static double GetWaistThickness(Document doc, Element stairOrRun)
    {
        if (stairOrRun == null) return EngineConfig.FallbackWaistDepth;

        var typeElem = doc.GetElement(stairOrRun.GetTypeId());
        if (typeElem == null) return EngineConfig.FallbackWaistDepth;

        // Common parameter names (vary by Revit template).
        string[] candidates =
        {
            "Structural Depth",
            "Waist Thickness",
            "Slab Thickness",
            "Run Thickness",
            "Minimum Waist Thickness",
            "Waist"
        };

        foreach (string name in candidates)
        {
            Parameter? p = typeElem.LookupParameter(name);
            if (p != null && p.StorageType == StorageType.Double)
            {
                double v = p.AsDouble();
                if (v > 0) return v;
            }
        }

        return EngineConfig.FallbackWaistDepth;
    }

    /// <summary>
    /// Get the run width (in feet) from the run instance or its type.
    /// Tries (in order):
    ///   1. <see cref="StairsRun.ActualRunWidth"/> (instance property)
    ///   2. Run type's "Run Width" / "Actual Run Width" / "Width" parameter
    ///   3. Returns 0 if not found (caller must fall back)
    /// </summary>
    public static double GetRunWidth(StairsRun run)
    {
        // 1. Instance property.
        double w = run.ActualRunWidth;
        if (w > 0) return w;

        // 2. Run type parameters.
        var typeElem = run.Document.GetElement(run.GetTypeId());
        if (typeElem != null)
        {
            string[] widthParams =
            {
                "Actual Run Width",
                "Run Width",
                "Width",
                "Stair Width",
                "Tread Depth"  // sometimes mislabeled
            };

            foreach (string name in widthParams)
            {
                Parameter? p = typeElem.LookupParameter(name);
                if (p != null && p.StorageType == StorageType.Double)
                {
                    double v = p.AsDouble();
                    if (v > 0) return v;
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// Diagnostic: list all parameters on the run and its type that contain
    /// "width" (case-insensitive) in their name. Written to <paramref name="log"/>.
    /// </summary>
    public static void LogWidthParameters(StairsRun run, List<string> log)
    {
        log.Add($"[DIAG]   Parameters containing 'width' on StairsRun {run.Id}:");

        // Instance parameters.
        foreach (Parameter p in run.Parameters)
        {
            if (p.Definition.Name.Contains("width", StringComparison.OrdinalIgnoreCase) ||
                p.Definition.Name.Contains("Width", StringComparison.OrdinalIgnoreCase))
            {
                string val = p.StorageType == StorageType.Double
                    ? p.AsDouble().ToString("F3")
                    : p.AsValueString();
                log.Add($"[DIAG]     instance: {p.Definition.Name} = {val}");
            }
        }

        // Type parameters.
        var typeElem = run.Document.GetElement(run.GetTypeId());
        if (typeElem != null)
        {
            log.Add($"[DIAG]   Parameters containing 'width' on type {typeElem.GetType().Name} '{typeElem.Name}':");
            foreach (Parameter p in typeElem.Parameters)
            {
                if (p.Definition.Name.Contains("width", StringComparison.OrdinalIgnoreCase) ||
                    p.Definition.Name.Contains("Width", StringComparison.OrdinalIgnoreCase))
                {
                    string val = p.StorageType == StorageType.Double
                        ? p.AsDouble().ToString("F3")
                        : p.AsValueString();
                    log.Add($"[DIAG]     type: {p.Definition.Name} = {val}");
                }
            }
        }

        // Also log ALL type parameters for debugging.
        if (typeElem != null)
        {
            log.Add($"[DIAG]   ALL type parameters:");
            foreach (Parameter p in typeElem.Parameters)
            {
                string val = p.StorageType == StorageType.Double
                    ? p.AsDouble().ToString("F3")
                    : (p.StorageType == StorageType.ElementId
                        ? p.AsElementId().ToString()
                        : p.AsValueString());
                log.Add($"[DIAG]     {p.Definition.Name} = {val}");
            }
        }
    }

    /// <summary>
    /// Find the first concrete material in the document.
    /// </summary>
    public static ElementId GetConcreteMaterial(Document doc)
    {
        try
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .FirstOrDefault(m => m.Name.Contains("Concrete", StringComparison.OrdinalIgnoreCase))
                ?.Id ?? ElementId.InvalidElementId;
        }
        catch
        {
            return ElementId.InvalidElementId;
        }
    }
}
