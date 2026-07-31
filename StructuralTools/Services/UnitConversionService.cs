using Autodesk.Revit.DB;

namespace StructuralTools.Services;

/// <summary>
/// Service for unit conversions between Revit internal units and display units.
/// </summary>
public class UnitConversionService
{
    private readonly Document _doc;
    private readonly bool _isMetric;

    public UnitConversionService(Document doc)
    {
        _doc = doc;
        
        // Detect project units
        var options = doc.GetUnits();
        var format = options.GetFormatSpec(UnitType.UT_Length);
        _isMetric = format.DisplayUnit == DisplayUnitType.DUT_METERS || 
                    format.DisplayUnit == DisplayUnitType.DUT_MILLIMETERS;
    }

    /// <summary>
    /// Converts a value from internal units (feet) to meters if metric.
    /// </summary>
    public double ToLength(double internalValue)
    {
        return _isMetric ? UnitUtils.ConvertFromInternalUnits(internalValue, UnitType.UT_Length) : internalValue;
    }

    /// <summary>
    /// Converts a force per length value to appropriate display units.
    /// </summary>
    public double ToForcePerLength(double internalValue)
    {
        // Internal value is in k/ft (kips per foot) or similar
        // Convert based on project units
        
        if (_isMetric)
        {
            // Convert to kN/m
            // 1 k/ft ≈ 14.59 kN/m
            return internalValue * 14.5939;
        }
        
        // Keep as k/ft for imperial
        return internalValue;
    }

    /// <summary>
    /// Converts density from internal units to kN/m³ or k/ft³.
    /// </summary>
    public double ToDensity(double internalValue)
    {
        if (_isMetric)
        {
            // Convert to kN/m³
            return internalValue * 16.0185; // kg/m³ to lb/ft³ conversion factor adjusted
        }
        
        return internalValue;
    }

    /// <summary>
    /// Gets the appropriate unit symbol for force per length.
    /// </summary>
    public string GetForcePerLengthUnitSymbol()
    {
        return _isMetric ? "kN/m" : "k/ft";
    }

    /// <summary>
    /// Gets the appropriate unit symbol for density.
    /// </summary>
    public string GetDensityUnitSymbol()
    {
        return _isMetric ? "kN/m³" : "k/ft³";
    }

    /// <summary>
    /// Formats a value with appropriate units.
    /// </summary>
    public string FormatForcePerLength(double value)
    {
        return $"{value:F2} {GetForcePerLengthUnitSymbol()}";
    }

    /// <summary>
    /// Formats a density value with appropriate units.
    /// </summary>
    public string FormatDensity(double value)
    {
        return $"{value:F1} {GetDensityUnitSymbol()}";
    }
}
