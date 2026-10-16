using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// Guards on the container definition.
///
/// Two things in the Dockerfile matter more than the rest, and both are easy to "tidy up" by
/// accident while fixing something else:
///
///   the volume, because /data holds the database, the resumes, the verified profile and the
///   encrypted keys, and a container without one loses all of it on the next rebuild
///
///   the loopback bind, because the app has no authentication by default. Publishing on 0.0.0.0
///   hands your profile and your model credits to the network.
///
/// Neither fails a build on its own, and neither fails any test that exercises the running app. So
/// they are asserted here, where a change to the file has to trip something.
/// </summary>
public class ContainerDefinitionTests
{
    private static readonly string Root = FindRepoRoot();

    private static string Dockerfile => Read("Dockerfile");
    private static string Compose => Read("docker-compose.yml");
    private static string Dockerignore => Read(".dockerignore");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dockerfile"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    private static string Read(string name)
    {
        var path = Path.Combine(Root, name);
        Assert.True(File.Exists(path), $"{name} is missing from the repository root.");
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_data_directory_is_a_volume()
    {
        // Without this the database lives in the container's writable layer and is gone on rebuild.
        // There is no backup: the tool does not make one.
        Assert.Contains("VOLUME [\"/data\"]", Dockerfile);
        Assert.Contains("/data", Compose);
    }

    [Fact]
    public void The_published_port_is_loopback_only()
    {
        Assert.Contains("\"127.0.0.1:5177:5177\"", Compose);

        // A bare "5177:5177" publishes on every interface, which with no API key set means anyone
        // who can reach the machine can read your profile and spend your model credits.
        Assert.DoesNotContain("- \"5177:5177\"", Compose);
    }

    [Fact]
    public void The_container_binds_to_all_interfaces_internally()
    {
        // The other half of the pair above. Docker reaches a container by its IP, so the app's own
        // 127.0.0.1 default means nothing published would ever answer.
        Assert.Contains("RESUCLEAN_HOST=0.0.0.0", Dockerfile);
    }

    [Fact]
    public void The_container_does_not_run_as_root()
    {
        Assert.Contains("USER app", Dockerfile);
        Assert.DoesNotContain("\nUSER root", "\n" + Dockerfile);
    }

    [Fact]
    public void User_data_cannot_reach_the_build_context()
    {
        // The database, the resumes and the encrypted keys all live in data/. A missing ignore
        // rule copies them into the daemon's cache, and from there into any careless layer.
        foreach (var rule in new[] { "data/", "*.db", ".env" })
        {
            Assert.Contains(rule, Dockerignore);
        }
    }

    [Fact]
    public void The_health_check_actually_calls_the_app()
    {
        // `dotnet --info` succeeds on a container whose process has already died, which reports
        // healthy for a service that is not there. Scoped to the instruction rather than the whole
        // file, because the Dockerfile explains this exact mistake in a comment above it.
        var instruction = string.Join(' ', Dockerfile
            .Split('\n')
            .SkipWhile(l => !l.TrimStart().StartsWith("HEALTHCHECK", StringComparison.Ordinal))
            .TakeWhile(l => l.TrimStart().StartsWith("HEALTHCHECK", StringComparison.Ordinal) || l.TrimStart().StartsWith("CMD"))
            .Select(l => l.Trim()));

        Assert.Contains("/api/health", instruction);
        Assert.DoesNotContain("dotnet --info", instruction);
    }

    [Fact]
    public void The_api_key_escape_hatch_is_documented_rather_than_set()
    {
        // Setting a default API key would be worse than none: every user would share one.
        Assert.DoesNotContain("RESUCLEAN_API_KEY=change-me\n", Compose.Replace("\r\n", "\n"));
    }
}