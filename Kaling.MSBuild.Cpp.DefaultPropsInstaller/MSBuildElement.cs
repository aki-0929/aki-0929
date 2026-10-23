namespace Kaling.MSBuild.Cpp.DefaultPropsInstaller;

/// <summary>
/// In-memory MSBuild element. Value and Children are mutually exclusive:
/// leaf = value (or empty); branch = children.
/// </summary>
public sealed class MSBuildElement
{
    public string Name { get; set; } = string.Empty;

    public Dictionary<string, string>? Attributes { get; set; }

    public string? Value { get; set; }

    public List<MSBuildElement>? Children { get; set; }

    public bool HasChildren => Children is { Count: > 0 };

    public bool HasValue => Value is not null;
}