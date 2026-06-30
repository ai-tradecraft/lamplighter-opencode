namespace ChatThroughHarness.Runner;

public enum ControllerWorkspacePlatform
{
    MacOS,
    Linux,
    Windows
}

public static class ControllerWorkspaceResolver
{
    public const string EnvironmentVariable = "LAMPLIGHTER_CONTROLLER_WORKSPACE";

    public static string ResolveFromEnvironment()
    {
        var platform = OperatingSystem.IsMacOS()
            ? ControllerWorkspacePlatform.MacOS
            : OperatingSystem.IsWindows()
                ? ControllerWorkspacePlatform.Windows
                : ControllerWorkspacePlatform.Linux;

        return Resolve(
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            platform,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
            Environment.GetEnvironmentVariable("LOCALAPPDATA"));
    }

    public static string Resolve(
        string? configured,
        ControllerWorkspacePlatform platform,
        string home,
        string? xdgDataHome,
        string? localAppData)
    {
        var candidate = string.IsNullOrWhiteSpace(configured)
            ? DefaultPath(platform, home, xdgDataHome, localAppData)
            : ExpandHome(configured.Trim(), home);
        return Path.GetFullPath(candidate);
    }

    public static void EnsureWritable(string workspace)
    {
        Directory.CreateDirectory(workspace);
        var probePath = Path.Combine(workspace, $".lamplighter-write-test-{Guid.NewGuid():N}");
        try
        {
            using var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            probe.WriteByte(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Lamplighter controller workspace is not writable: {workspace}",
                ex);
        }
        finally
        {
            File.Delete(probePath);
        }
    }

    private static string DefaultPath(
        ControllerWorkspacePlatform platform,
        string home,
        string? xdgDataHome,
        string? localAppData)
    {
        return platform switch
        {
            ControllerWorkspacePlatform.MacOS => Path.Combine(
                Required(home, "HOME"),
                "Library",
                "Application Support",
                "lamplighter",
                "workspace"),
            ControllerWorkspacePlatform.Linux => Path.Combine(
                string.IsNullOrWhiteSpace(xdgDataHome)
                    ? Path.Combine(Required(home, "HOME"), ".local", "share")
                    : xdgDataHome,
                "lamplighter",
                "workspace"),
            ControllerWorkspacePlatform.Windows => Path.Combine(
                Required(localAppData, "LOCALAPPDATA"),
                "lamplighter",
                "workspace"),
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null)
        };
    }

    private static string ExpandHome(string path, string home)
    {
        if (path == "~")
        {
            return Required(home, "HOME");
        }

        if (path.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || path.StartsWith($"~{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return Path.Combine(Required(home, "HOME"), path[2..]);
        }

        return path;
    }

    private static string Required(string? value, string name)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{name} is required to resolve the Lamplighter controller workspace.")
            : value;
    }
}
