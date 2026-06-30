using ChatThroughHarness.Runner;
using Xunit;

namespace ChatThroughHarness.Api.Tests;

public sealed class ControllerWorkspaceResolverTests
{
    [Fact]
    public void UsesConfiguredWorkspaceAndExpandsHome()
    {
        var home = NewRoot();

        var resolved = ControllerWorkspaceResolver.Resolve(
            "~/custom-lamplighter",
            ControllerWorkspacePlatform.MacOS,
            home,
            null,
            null);

        Assert.Equal(Path.Combine(home, "custom-lamplighter"), resolved);
    }

    [Fact]
    public void UsesMacApplicationSupportDefault()
    {
        var home = NewRoot();

        var resolved = ControllerWorkspaceResolver.Resolve(
            null,
            ControllerWorkspacePlatform.MacOS,
            home,
            null,
            null);

        Assert.Equal(
            Path.Combine(home, "Library", "Application Support", "lamplighter", "workspace"),
            resolved);
    }

    [Fact]
    public void UsesLinuxXdgDefaultWhenConfigured()
    {
        var home = NewRoot();
        var xdgDataHome = Path.Combine(home, "xdg-data");

        var resolved = ControllerWorkspaceResolver.Resolve(
            null,
            ControllerWorkspacePlatform.Linux,
            home,
            xdgDataHome,
            null);

        Assert.Equal(Path.Combine(xdgDataHome, "lamplighter", "workspace"), resolved);
    }

    [Fact]
    public void UsesLinuxLocalShareDefaultWithoutXdg()
    {
        var home = NewRoot();

        var resolved = ControllerWorkspaceResolver.Resolve(
            null,
            ControllerWorkspacePlatform.Linux,
            home,
            null,
            null);

        Assert.Equal(Path.Combine(home, ".local", "share", "lamplighter", "workspace"), resolved);
    }

    [Fact]
    public void UsesWindowsLocalAppDataDefault()
    {
        var localAppData = NewRoot();

        var resolved = ControllerWorkspaceResolver.Resolve(
            null,
            ControllerWorkspacePlatform.Windows,
            "",
            null,
            localAppData);

        Assert.Equal(Path.Combine(localAppData, "lamplighter", "workspace"), resolved);
    }

    [Fact]
    public void CreatesAndValidatesWritableWorkspace()
    {
        var workspace = Path.Combine(NewRoot(), "controller");

        ControllerWorkspaceResolver.EnsureWritable(workspace);

        Assert.True(Directory.Exists(workspace));
        Assert.Empty(Directory.EnumerateFiles(workspace));
    }

    private static string NewRoot()
    {
        return Path.Combine(Path.GetTempPath(), $"lamplighter-controller-{Guid.NewGuid():N}");
    }
}
