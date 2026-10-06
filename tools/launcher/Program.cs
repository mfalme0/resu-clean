// resu-clean launcher.
//
//   resu-clean          production: ONE process serving the UI and the API on one port
//   resu-clean dev      development: TWO processes (dotnet watch + Vite) with hot reload
//   resu-clean run      same as the default, stated explicitly
//   resu-clean help     usage
//
// Ctrl+C, or closing the window, stops every child process it started.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ResuClean.Launcher;

internal static class Program
{
    private const string Banner = "resu-clean";

    private static readonly List<Process> Children = new();
    private static bool shuttingDown;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var mode = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant() ?? "run";

        if (mode is "help" or "h" or "--help" or "/?")
        {
            Usage();
            return 0;
        }

        var app = FindAppRoot();
        if (app is null)
        {
            Console.Error.WriteLine("Could not find resu-clean.");
            Console.Error.WriteLine("Put this exe in the published folder (the one containing ResuClean.exe and wwwroot),");
            Console.Error.WriteLine("or run it from inside the project folder where package.json and server/ live.");
            return 2;
        }

        Console.WriteLine($"{Banner}  mode: {mode}");
        Console.WriteLine($"  app:  {app.Root}");

        var port = EnvOrDefault("RESUCLEAN_PORT", "5177");

        try
        {
            return mode switch
            {
                "dev" => RunDev(app.Root, port),
                "run" => RunProduction(app, port),
                _ => UnknownMode(mode)
            };
        }
        catch (StartupException ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Cannot start: {ex.Message}");
            Console.Error.WriteLine();
            foreach (var step in ex.Steps) Console.Error.WriteLine($"  {step}");
            Console.Error.WriteLine();
            return 3;
        }
    }

    /// <summary>Where the application lives, and whether it can run without .NET installed.</summary>
    private sealed record AppRoot(string Root, string? SelfContainedExe, bool IsPublished);

    // ---- Production: one process, both UI and API --------------------------

    private static int RunProduction(AppRoot app, string port)
    {
        var url = $"http://127.0.0.1:{port}";
        // The data folder holds the database, and on it the encrypted API keys. Different launch
        // modes can resolve it differently, so always say which one is in use.
        var dataDir = DataDirFor(app);

        Console.WriteLine($"  starting on {url}  (UI and API on the same port)");
        Console.WriteLine($"  data:   {dataDir}   <- database and stored API keys live here");
        if (app.SelfContainedExe is not null) Console.WriteLine("  no .NET install needed on this machine");
        Console.WriteLine("  press Ctrl+C to stop");
        Console.WriteLine();

        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            TryOpenBrowser(url);
        });

        ProcessStartInfo info;
        if (app.SelfContainedExe is not null)
        {
            // The published single-file build: run it directly. This is the portable path.
            info = new ProcessStartInfo(app.SelfContainedExe) { WorkingDirectory = app.Root };
        }
        else
        {
            var dll = Path.Combine(app.Root, "server", "bin", "Release", "net8.0", "ResuClean.dll");
            if (!File.Exists(dll))
            {
                throw new StartupException("the backend has not been built yet", new[]
                {
                    "Run `npm run build` once, then start it again.",
                    "Or use `resu-clean dev` for the development mode."
                });
            }

            if (!File.Exists(Path.Combine(app.Root, "server", "wwwroot", "index.html")))
            {
                throw new StartupException("the web interface has not been built yet", new[]
                {
                    "Run `npm run build` once, then start it again."
                });
            }

            Require("dotnet", "dotnet", new[] { "--version" }, "Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0");
            info = new ProcessStartInfo("dotnet") { FileName = dll, WorkingDirectory = Path.Combine(app.Root, "server") };
        }

        info.Environment["RESUCLEAN_HOST"] = "127.0.0.1";
        info.Environment["RESUCLEAN_PORT"] = port;

        var process = Start(info);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Shutdown(0);
        };

        return WaitFor(process);
    }

    // ---- Development: two processes ----------------------------------------

    /// <summary>
    /// Mirrors AppConfig.AppRootOf exactly, so the path the launcher prints is the path the
    /// backend will actually use. If these two ever disagree the launcher is lying about where
    /// the stored API keys live, which is worse than printing nothing.
    /// </summary>
    private static string DataDirFor(AppRoot app)
    {
        var configured = Environment.GetEnvironmentVariable("RESUCLEAN_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.IsPathRooted(configured)
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(Path.Combine(AppRootOf(app.Root), configured));
        }

        return Path.GetFullPath(Path.Combine(AppRootOf(app.Root), "data"));
    }

    private static string AppRootOf(string root)
    {
        if (Directory.Exists(Path.Combine(root, "wwwroot"))) return Path.GetFullPath(root);
        var parent = Directory.GetParent(Path.GetFullPath(root));
        return parent?.FullName ?? Path.GetFullPath(root);
    }

    private static int RunDev(string root, string port)
    {
        var project = Path.Combine(root, "server", "ResuClean.csproj");
        if (!File.Exists(project)) throw new StartupException($"server project not found at {project}", Array.Empty<string>());

        Require("dotnet", "dotnet", new[] { "--version" }, "Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0");
        Require("node", "node", new[] { "--version" }, "Install Node.js 20 or newer: https://nodejs.org");

        var webDir = Path.Combine(root, "web");
        if (!Directory.Exists(Path.Combine(webDir, "node_modules")))
        {
            Console.WriteLine("  first run: installing web dependencies, this takes a minute...");
            var install = Start(new ProcessStartInfo("npm")
            {
                FileName = "npm",
                Arguments = "install",
                WorkingDirectory = webDir
            });
            if (WaitFor(install) != 0)
                throw new StartupException("installing the web dependencies failed", new[] { "Fix the npm or network error above and try again." });
        }

        Console.WriteLine($"  backend  dotnet watch  -> http://127.0.0.1:{port}");
        Console.WriteLine("  frontend vite         -> http://127.0.0.1:5173  (open this one)");
        Console.WriteLine($"  data: {DataDirFor(new AppRoot(root, null, false))}   <- database and stored API keys live here");
        Console.WriteLine("  press Ctrl+C to stop both");
        Console.WriteLine();

        var backend = Start(new ProcessStartInfo("dotnet")
        {
            FileName = "dotnet",
            Arguments = $"watch --project \"{project}\" --no-launch-profile",
            WorkingDirectory = Path.Combine(root, "server"),
            Environment = { ["RESUCLEAN_HOST"] = "127.0.0.1", ["RESUCLEAN_PORT"] = port }
        });

        var frontend = Start(new ProcessStartInfo("npm")
        {
            FileName = "npm",
            Arguments = "run dev",
            WorkingDirectory = webDir
        });

        _ = Task.Run(async () =>
        {
            await Task.Delay(4000);
            TryOpenBrowser("http://127.0.0.1:5173");
        });

        // If either dies, tear the other one down rather than leaving a half-dead pair.
        _ = Task.Run(async () =>
        {
            await Task.WhenAny(
                WaitForExitAsync(backend),
                WaitForExitAsync(frontend));
            Shutdown(0);
        });

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Shutdown(0);
        };

        while (!shuttingDown)
        {
            var alive = Children.Where(c => !c.HasExited).ToList();
            if (alive.Count == 0) break;
            Thread.Sleep(300);
        }

        Shutdown(0);
        return 0;
    }

    private static int UnknownMode(string mode)
    {
        Console.Error.WriteLine($"Unknown mode '{mode}'.");
        Usage();
        return 64;
    }

    private static void Usage()
    {
        Console.WriteLine();
        Console.WriteLine($"{Banner} — self-hosted resume toolkit");
        Console.WriteLine();
        Console.WriteLine("  resu-clean          production: one process serving the UI and the API (lowest RAM)");
        Console.WriteLine("  resu-clean run      the same, stated explicitly");
        Console.WriteLine("  resu-clean dev      development: dotnet watch + vite with hot reload");
        Console.WriteLine("  resu-clean help     this text");
        Console.WriteLine();
        Console.WriteLine("Environment:");
        Console.WriteLine("  RESUCLEAN_PORT      port for production mode (default 5177)");
        Console.WriteLine("  RESUCLEAN_API_KEY   if set, the UI asks for an API key on first load");
        Console.WriteLine();
    }

    // ---- Process plumbing ---------------------------------------------------

    private static Process Start(ProcessStartInfo info)
    {
        info.UseShellExecute = false;
        info.RedirectStandardOutput = false;
        info.RedirectStandardError = false;

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        lock (Children) Children.Add(process);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new StartupException($"could not start '{info.FileName}'", new[] { ex.Message });
        }

        return process;
    }

    private static int WaitFor(Process process)
    {
        try
        {
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private static Task WaitForExitAsync(Process process)
    {
        var completion = new TaskCompletionSource();
        process.Exited += (_, _) => completion.TrySetResult();
        return completion.Task;
    }

    private static void Shutdown(int code)
    {
        if (shuttingDown) return;
        shuttingDown = true;

        lock (Children)
        {
            foreach (var child in Children)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        // entireProcessTree matters: dotnet watch spawns the built app as a child.
                        child.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Already gone, or we are not allowed to kill it. Nothing to do.
                }
            }
        }

        Environment.Exit(code);
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start("xdg-open", url);
            }
        }
        catch
        {
            Console.WriteLine($"  open {url} in your browser");
        }
    }

    private static void Require(string label, string command, string[] probe, string remedy)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(command)
            {
                FileName = command,
                Arguments = string.Join(' ', probe),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) throw new StartupException($"{label} was not found on PATH", new[] { remedy });
            process.WaitForExit(8000);
            if (process.ExitCode != 0) throw new StartupException($"{label} was not found on PATH", new[] { remedy });
        }
        catch (StartupException)
        {
            throw;
        }
        catch
        {
            throw new StartupException($"{label} was not found on PATH", new[] { remedy });
        }
    }

    private static string EnvOrDefault(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    /// <summary>
    /// Locates the application. A published folder is preferred over the source tree, so
    /// double-clicking the exe in artifacts\... uses that copy rather than the dev build.
    /// </summary>
    private static AppRoot? FindAppRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                // Published bundle: the self-contained exe next to wwwroot.
                foreach (var exeName in new[] { "ResuClean.exe", "resu-clean.exe" })
                {
                    var candidate = Path.Combine(dir.FullName, exeName);
                    if (File.Exists(candidate) &&
                        Directory.Exists(Path.Combine(dir.FullName, "wwwroot")) &&
                        !string.Equals(candidate, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return new AppRoot(dir.FullName, candidate, true);
                    }
                }

                // Source tree.
                if (File.Exists(Path.Combine(dir.FullName, "package.json")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "server")))
                {
                    return new AppRoot(dir.FullName, null, false);
                }

                dir = dir.Parent;
            }
        }

        return null;
    }
}

internal sealed class StartupException : Exception
{
    public StartupException(string message, string[] steps) : base(message) => Steps = steps;
    public string[] Steps { get; }
}