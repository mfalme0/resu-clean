using Microsoft.Extensions.Configuration;
using ResuClean.Configuration;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// Where the data directory resolves. This matters more than it looks: the SQLite database holds
/// the encrypted API keys, so if two launch modes resolve it differently, keys appear to vanish
/// when you switch. Both layouts are pinned here.
/// </summary>
public class DataDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resu-clean-tests", Guid.NewGuid().ToString("N")[..10]);

    public DataDirectoryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    [Fact]
    public void A_published_bundle_keeps_its_data_inside_the_bundle()
    {
        // The published layout: the content root IS the app root and it contains wwwroot.
        var bundle = Path.Combine(_root, "resu-clean-win-x64");
        Directory.CreateDirectory(bundle);
        Directory.CreateDirectory(Path.Combine(bundle, "wwwroot"));

        var appRoot = AppConfig.AppRootOf(bundle);

        Assert.Equal(Path.GetFullPath(bundle), appRoot);

        var config = AppConfig.Load(Empty(), bundle);
        Assert.Equal(Path.GetFullPath(Path.Combine(bundle, "data")), config.DataDir);
        // Portability: the whole app, including its database, lives in one copyable folder.
        Assert.StartsWith(Path.GetFullPath(bundle), config.DataDir);
    }

    [Fact]
    public void The_source_tree_puts_data_at_the_repository_root()
    {
        // Source layout: content root is <repo>/server, so the app root is the repository root.
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "server", "bin"));
        var serverDir = Path.Combine(repo, "server");

        var appRoot = AppConfig.AppRootOf(serverDir);

        Assert.Equal(Path.GetFullPath(repo), appRoot);

        var config = AppConfig.Load(Empty(), serverDir);
        Assert.Equal(Path.GetFullPath(Path.Combine(repo, "data")), config.DataDir);
    }

    [Fact]
    public void An_explicit_absolute_data_dir_is_the_same_folder_in_both_layouts()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "server"));
        var bundle = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(bundle);
        Directory.CreateDirectory(Path.Combine(bundle, "wwwroot"));

        // Pinning an absolute path is how a user forces the two launch modes to share one database.
        var pinned = Path.Combine(_root, "one-database");

        var fromServer = AppConfig.Load(WithEnv("DATA_DIR", pinned), Path.Combine(repo, "server"));
        var fromBundle = AppConfig.Load(WithEnv("DATA_DIR", pinned), bundle);

        Assert.Equal(Path.GetFullPath(pinned), fromServer.DataDir);
        Assert.Equal(fromServer.DataDir, fromBundle.DataDir);
    }

    [Fact]
    public void A_relative_data_dir_resolves_under_each_layouts_own_app_root()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "server"));
        var bundle = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(bundle);
        Directory.CreateDirectory(Path.Combine(bundle, "wwwroot"));

        var fromServer = AppConfig.Load(WithEnv("DATA_DIR", "custom-store"), Path.Combine(repo, "server"));
        var fromBundle = AppConfig.Load(WithEnv("DATA_DIR", "custom-store"), bundle);

        // Relative means "next to the app", which is what makes the bundle portable.
        Assert.Equal(Path.GetFullPath(Path.Combine(repo, "custom-store")), fromServer.DataDir);
        Assert.Equal(Path.GetFullPath(Path.Combine(bundle, "custom-store")), fromBundle.DataDir);
    }

    [Fact]
    public void An_absolute_data_dir_is_used_verbatim()
    {
        var absolute = Path.Combine(_root, "somewhere-else");
        var config = AppConfig.Load(WithEnv("DATA_DIR", absolute), Path.Combine(_root, "repo", "server"));

        Assert.Equal(Path.GetFullPath(absolute), config.DataDir);
    }

    [Fact]
    public void Configuration_still_reads_the_other_settings()
    {
        var config = AppConfig.Load(Config(
            ("RESUCLEAN_PORT", "6000"),
            ("RESUCLEAN_HOST", "0.0.0.0"),
            ("RESUCLEAN_API_KEY", "abc123"),
            ("RESUCLEAN_MAX_UPLOAD_MB", "25"),
            ("RESUCLEAN_MAX_CONCURRENCY", "4"),
            ("RESUCLEAN_CACHE_TTL_SECONDS", "60"),
            ("RESUCLEAN_SOURCE_MIN_INTERVAL_MS", "500"),
            ("RESUCLEAN_CORS", "http://a, http://b")), Path.Combine(_root, "repo", "server"));

        Assert.Equal(6000, config.Port);
        Assert.Equal("0.0.0.0", config.Host);
        Assert.True(config.AuthRequired);
        Assert.Equal(25L * 1024 * 1024, config.MaxUploadBytes);
        Assert.Equal(4, config.MaxConcurrency);
        Assert.Equal(60, config.CacheTtlSeconds);
        Assert.Equal(500, config.SourceMinIntervalMs);
        Assert.Equal(new[] { "http://a", "http://b" }, config.CorsOriginList);
    }

    [Fact]
    public void Auth_is_off_when_no_key_is_configured()
    {
        Assert.False(AppConfig.Load(Empty(), Path.Combine(_root, "repo", "server")).AuthRequired);
    }

    [Fact]
    public void Blank_values_fall_back_to_safe_defaults()
    {
        var config = AppConfig.Load(Config(
            ("RESUCLEAN_PORT", "not-a-number"),
            ("RESUCLEAN_MAX_CONCURRENCY", "9999"),
            ("RESUCLEAN_API_KEY", "   ")), Path.Combine(_root, "repo", "server"));

        Assert.Equal(5177, config.Port);
        Assert.Equal(8, config.MaxConcurrency);   // clamped to the maximum
        Assert.False(config.AuthRequired);
    }

    private static Microsoft.Extensions.Configuration.IConfiguration Empty() => Config();

    private static Microsoft.Extensions.Configuration.IConfiguration WithEnv(string valueName, string value) =>
        Config(("RESUCLEAN_DATA_DIR", value));

    /// <summary>Real configuration binding, so the test exercises the same code path as startup.</summary>
    private static Microsoft.Extensions.Configuration.IConfiguration Config(params (string Key, string? Value)[] values)
    {
        var builder = new Microsoft.Extensions.Configuration.ConfigurationBuilder();
        builder.AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)));
        return builder.Build();
    }
}