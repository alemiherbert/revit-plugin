using Autodesk.Revit.DB;

namespace WallLoadGenerator.Services;

/// <summary>
/// Service for unit conversions between Revit internal units and display units.
/// Revit stores all measurements in feet (for length) internally.
/// </summary>
public static class UnitConversionService
{
    // Conversion constants
    private const double FeetToMeters = 0.3048;
    private const double MetersToFeet = 1.0 / 0.3048;
    private const double FeetToMm = 304.8;
    private const double MmToFeet = 1.0 / 304.8;
    
    /// <summary>
    /// Converts from Revit internal units (feet) to millimeters.
    /// </summary>
    public static double FeetToMm(double feet) => feet * FeetToMm;
    
    /// <summary>
    /// Converts from millimeters to Revit internal units (feet).
    /// </summary>
    public static double MmToFeet(double mm) => mm * MmToFeet;
    
    /// <summary>
    /// Converts from Revit internal units (feet) to meters.
    /// </summary>
    public static double FeetToMeters(double feet) => feet * FeetToMeters;
    
    /// <summary>
    /// Converts from meters to Revit internal units (feet).
    /// </summary>
    public static double MetersToFeet(double meters) => meters * MetersToFeet;
    
    /// <summary>
    /// Converts from Revit internal units (feet) to a formatted string with units.
    /// </summary>
    public static string FormatLength(double feet, DisplayUnitType unitType = DisplayUnitType.DUT_METERS)
    {
        return UnitFormatUtils.Format(
            new FormatOptions(unitType),
            feet,
            true // show units
        );
    }
    
    /// <summary>
    /// Converts from Revit internal units (feet) to a formatted string with units.
    /// </summary>
    public static string FormatForce(double pounds, DisplayUnitType unitType = DisplayUnitType.DUT_KILOWATTS)
    {
        return UnitFormatUtils.Format(
            new FormatOptions(unitType),
            pounds,
            true // show units
        );
    }
    
    /// <summary>
    /// Gets the project's length display unit type.
    /// </summary>
    public static DisplayUnitType GetProjectLengthUnits(Document doc)
    {
        var formatOptions = doc.GetUnits().GetFormatOptions(SpecTypeId.Length);
        return formatOptions.DisplayUnitType;
    }
    
    /// <summary>
    /// Converts density from kN/m³ to lbf/ft³ (or vice versa based on project units).
    /// </summary>
    public static double ConvertDensity(double value, bool toImperial)
    {
        // 1 kN/m³ = 6.36588 lbf/ft³
        const double conversionFactor = 6.36588;
        
        return toImperial ? value * conversionFactor : value / conversionFactor;
    }
    
    /// <summary>
    /// Converts force per unit length between metric and imperial.
    /// </summary>
    public static double ConvertLineLoad(double value, bool toImperial)
    {
        // 1 kN/m = 68.5218 lbf/ft
        const double conversionFactor = 68.5218;
        
        return toImperial ? value * conversionFactor : value / conversionFactor;
    }
}
