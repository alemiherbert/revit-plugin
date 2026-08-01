using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using StructuralTools.Services;

namespace StructuralTools.Models;

/// <summary>
/// Represents a wall element together with its placement transform (for linked models)
/// and the source document title. Implemented as a readonly struct because instances
/// are passed by value throughout the engine and never mutated.
/// </summary>
public readonly struct WallEntry
{
    public Wall Wall { get; }
    public Transform Transform { get; }
    public string? Source { get; }

    public WallEntry(Wall wall, Transform transform, string? source)
    {
        Wall      = wall      ?? throw new ArgumentNullException(nameof(wall));
        Transform = transform ?? Transform.Identity;
        Source    = source;
    }

    public bool IsLinked => Source != null;
}

/// <summary>
/// Result bag returned by load creation operations.
/// </summary>
public class LoadResult
{
    public List<LineLoad> Created { get; } = new();
    public List<string> Log { get; } = new();
    public int Errors { get; set; }
    public int LcFails { get; set; }

    public void LogInfo(string msg, string cat = "INFO")
        => Log.Add($"[{cat}] {msg}");
}
