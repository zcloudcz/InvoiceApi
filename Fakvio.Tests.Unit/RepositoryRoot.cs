using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Locates the repository root from inside a test run.
///
/// Junior note: tests execute from <c>bin/Debug/net10.0</c>, so any test that reads a file
/// committed at the root of the repo (a guide, a sample config) has to walk upwards first.
/// The anchor is <c>Fakvio.sln</c> — the one file guaranteed to sit exactly at the root.
/// </summary>
internal static class RepositoryRoot
{
    private const string SolutionFileName = "Fakvio.sln";

    /// <summary>Absolute path of the folder holding <c>Fakvio.sln</c>.</summary>
    internal static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull($"{SolutionFileName} was not found in any folder above {AppContext.BaseDirectory}");
        return directory.FullName;
    }
}
