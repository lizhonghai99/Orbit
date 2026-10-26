namespace Orbit.Contracts;

/// <summary>Single source for product version strings exposed by /health and docs defaults.</summary>
public static class OrbitProductVersion
{
    /// <summary>Must match Directory.Build.props <c>Version</c>.</summary>
    public const string Current = "0.0.1";
}
