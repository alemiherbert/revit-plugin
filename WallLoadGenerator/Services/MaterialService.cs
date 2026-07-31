using Autodesk.Revit.DB;

namespace WallLoadGenerator.Services;

/// <summary>
/// Service for retrieving and managing material information.
/// </summary>
public class MaterialService
{
    private readonly Document _doc;

    public MaterialService(Document doc)
    {
        _doc = doc;
    }

    /// <summary>
    /// Gets the density of a material element.
    /// Note: Revit API doesn't directly expose density property.
    /// This would need to be retrieved from material assets or extended data.
    /// </summary>
    public double? GetDensity(ElementId materialId)
    {
        if (materialId == ElementId.InvalidElementId)
            return null;
            
        var material = _doc.GetElement(materialId) as Material;
        if (material == null)
            return null;
        
        // Attempt to get density from material properties
        // This is a placeholder - actual implementation depends on how density is stored
        
        try
        {
            // Try to get from material's physical properties
            var asset = material.GetRenderingAsset();
            if (asset != null)
            {
                // Rendering assets don't typically contain density
                // This would need custom parameter or shared parameter
            }
            
            // Check for custom density parameter
            var densityParam = material.get_Parameter(
                new ElementId(BuiltInParameter.MATERIAL_DENSITY)
            );
            
            if (densityParam != null && !densityParam.IsReadOnly)
            {
                return densityParam.AsDouble();
            }
            
            // If no density found, return null
            return null;
        }
        catch (Exception ex)
        {
            LoggingService.Warning($"Failed to get density for material {material.Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gets all materials in the document.
    /// </summary>
    public List<Material> GetAllMaterials()
    {
        var collector = new FilteredElementCollector(_doc);
        collector.OfCategory(BuiltInCategory.OST_Materials)
                 .WhereElementIsNotElementType();
        
        return collector.Cast<Material>().ToList();
    }

    /// <summary>
    /// Gets default density for common construction materials.
    /// Returns density in kN/m³ (metric) - convert as needed for project units.
    /// </summary>
    public static double GetDefaultDensity(string materialName)
    {
        // Common material densities in kN/m³
        var densities = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["Concrete"] = 24.0,
            ["Reinforced Concrete"] = 25.0,
            ["Steel"] = 77.0,
            ["Aluminum"] = 27.0,
            ["Wood"] = 6.0,
            ["Timber"] = 6.0,
            ["Brick"] = 18.0,
            ["Masonry"] = 20.0,
            ["CMU"] = 21.0,
            ["Glass"] = 25.0,
            ["Gypsum"] = 12.0,
            ["Drywall"] = 8.0,
            ["Plaster"] = 13.0,
            ["Stone"] = 26.0,
            ["Granite"] = 27.0,
            ["Marble"] = 26.5,
            ["Limestone"] = 25.0,
            ["Sand"] = 16.0,
            ["Gravel"] = 18.0,
            ["Soil"] = 18.0,
            ["Clay"] = 19.0,
            ["Asphalt"] = 23.0,
            ["Insulation"] = 0.5,
            ["Foam"] = 0.3,
            ["Air"] = 0.0
        };
        
        foreach (var kvp in densities)
        {
            if (materialName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }
        
        // Default fallback
        return 24.0; // Concrete default
    }

    /// <summary>
    /// Creates a material with specified properties.
    /// </summary>
    public ElementId? CreateMaterial(string name, double? density = null)
    {
        using var transaction = new Transaction(_doc, "Create Material");
        transaction.Start();
        
        try
        {
            var materialId = Material.Create(_doc, name);
            
            if (density.HasValue)
            {
                // Set density if parameter exists
                var material = _doc.GetElement(materialId) as Material;
                if (material != null)
                {
                    var densityParam = material.get_Parameter(
                        new ElementId(BuiltInParameter.MATERIAL_DENSITY)
                    );
                    
                    if (densityParam != null && !densityParam.IsReadOnly)
                    {
                        densityParam.Set(density.Value);
                    }
                }
            }
            
            transaction.Commit();
            return materialId;
        }
        catch (Exception ex)
        {
            transaction.RollBack();
            LoggingService.Error($"Failed to create material: {ex.Message}");
            return null;
        }
    }
}
