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
        public Wall Wall;
        public Transform Transform;
        public string Source;
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
