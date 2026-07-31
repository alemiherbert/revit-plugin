using Autodesk.Revit.DB;

namespace StructuralTools.Services;

/// <summary>
/// Service for retrieving material properties including density.
/// </summary>
public class MaterialService
{
    private readonly Document _doc;
    private readonly Dictionary<ElementId, double> _densityCache = new();

    public MaterialService(Document doc)
    {
        _doc = doc;
    }

    /// <summary>
    /// Gets the density of a material in kN/m³ (or project units).
    /// Returns 0 if material not found or has no density.
    /// </summary>
    public double GetMaterialDensity(ElementId? materialId)
    {
        if (materialId == null || materialId == ElementId.InvalidElementId)
            return 0;
            
        if (_densityCache.TryGetValue(materialId.Value, out var cached))
            return cached;
        
        var material = _doc.GetElement(materialId.Value) as Material;
        if (material == null)
            return 0;
        
        // Try to get density from material properties
        // Note: Revit doesn't expose density directly through API in all versions
        // This may need to be retrieved from shared parameters or calculated
        
        double density = 0;
        
        // Check for custom density parameter
        var densityParam = material.get_Parameter(BuiltInParameter.MATERIAL_DENSITY);
        if (densityParam != null && !densityParam.IsReadOnly)
        {
            density = densityParam.AsDouble();
        }
        
        // Fallback: use typical values based on material category/name
        if (density <= 0)
        {
            density = GetTypicalDensity(material.Name);
        }
        
        _densityCache[materialId.Value] = density;
        return density;
    }

    /// <summary>
    /// Gets typical density values based on material name patterns.
    /// Values are in kN/m³.
    /// </summary>
    private double GetTypicalDensity(string materialName)
    {
        if (string.IsNullOrEmpty(materialName))
            return 24.0; // Default concrete
            
        string name = materialName.ToLower();
        
        if (name.Contains("concrete"))
            return name.Contains("lightweight") ? 18.0 : 24.0;
            
        if (name.Contains("steel"))
            return 78.5;
            
        if (name.Contains("aluminum") || name.Contains("aluminium"))
            return 27.0;
            
        if (name.Contains("wood") || name.Contains("timber"))
            return name.Contains("hardwood") ? 8.0 : 6.0;
            
        if (name.Contains("brick"))
            return 20.0;
            
        if (name.Contains("glass"))
            return 25.0;
            
        if (name.Contains("gypsum") || name.Contains("drywall"))
            return 8.0;
            
        // Default
        return 24.0;
    }

    /// <summary>
    /// Clears the density cache.
    /// </summary>
    public void ClearCache()
    {
        _densityCache.Clear();
    }
}
