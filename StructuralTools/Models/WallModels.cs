using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using System.Collections.Generic;

namespace StructuralTools.Models
{
    /// <summary>
    /// Represents a wall element with its transform (for linked models) and source document.
    /// </summary>
    public struct WallEntry
    {
        public Wall? Wall;
        public Transform Transform;
        public string? Source;
    }

    /// <summary>
    /// Represents a line load to be created on a host element.
    /// </summary>
    public class WallLoad
    {
        public Curve? Curve { get; set; }
        public XYZ? ForceVector { get; set; }
        public double Magnitude { get; set; } // kN/m
        public ElementId? HostAnalyticalId { get; set; }
        public Autodesk.Revit.DB.Structure.LoadCase? LoadCase { get; set; }
        public LineLoadType? LoadType { get; set; }
        public string? WallId { get; set; }
        public double T0 { get; set; }
        public double T1 { get; set; }
    }

    /// <summary>
    /// Result bag returned by load creation operations.
    /// </summary>
    public class LoadResult
    {
        public List<LineLoad> Created { get; set; } = new List<LineLoad>();
        public List<string> Log { get; set; } = new List<string>();
        public int Errors { get; set; } = 0;
        public int LcFails { get; set; } = 0;
    }

}
