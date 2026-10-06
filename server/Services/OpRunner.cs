using System.Text.Json;
using Microsoft.Data.Sqlite;
using ResuClean.Data;
using ResuClean.Models;

namespace ResuClean.Services;

/// <summary>
/// Long operations (clean, model rewrite, ATS fix list, job search, kit generation) run here and
/// are polled by the UI. A bounded semaphore caps concurrency so memory stays predictable and
/// nothing runs in the background after a request completes.
/// </summary>
public sealed class OpRunner
{
    private readonly Db _db;
    private readonly AppConfigAccessorHolder _app;

    public OpRunner(Db db, Configuration.AppConfig app)
    {
        _db = db;
        _app = new AppConfigAccessorHolder(app);
    }

    private sealed class AppConfigAccessorHolder
    {
        public Configuration.AppConfig Value { get; }
        public AppConfigAccessorHolder(Configuration.AppConfig value) => Value = value;
    }

    public int ActiveCount { get; private set; }

    private static string Now() => DateTime.UtcNow.ToString("O");

    /// <summary>Queues work and returns immediately with an id the client polls.</summary>
    public OpAccepted Enqueue<T>(string kind, T payload, Func<T, CancellationToken, Task<object?>> work)
    {
        var id = ProfileService.NewId("op");
        using (var conn = _db.Open())
        {
            Db.Exec(conn, "INSERT INTO ops (id, kind, state, payload, result, error, created_at) VALUES (@id,@k,'queued',@p,'','',@c)",
                Db.P("@id", id), Db.P("@k", kind), Db.P("@p", JsonSerializer.Serialize(payload, Api.Json)), Db.P("@c", Now()));
        }

        _ = Task.Run(async () =>
        {
            var gate = Gates.For(_app.Value.MaxConcurrency);
            var entered = false;
            try
            {
                await gate.WaitAsync().ConfigureAwait(false);
                entered = true;
                ActiveCount++;
                SetState(id, "running", null, null, null);

                var result = await work(payload, CancellationToken.None).ConfigureAwait(false);
                SetState(id, "done", null, JsonSerializer.Serialize(result, Api.Json), null);
            }
            catch (OperationCanceledException)
            {
                SetState(id, "failed", null, null, "Cancelled.");
            }
            catch (Exception ex)
            {
                SetState(id, "failed", null, null, ex.Message);
            }
            finally
            {
                if (entered)
                {
                    ActiveCount--;
                    gate.Release();
                }
            }
        });

        return new OpAccepted(id, "queued", kind);
    }

    private void SetState(string id, string state, string? progress, string? result, string? error)
    {
        try
        {
            using var conn = _db.Open();
            Db.Exec(conn,
                "UPDATE ops SET state=@s, result=COALESCE(@r, result), error=COALESCE(@e, error), finished_at=@f WHERE id=@id",
                Db.P("@s", state), Db.P("@r", result), Db.P("@e", error),
                Db.P("@f", state is "done" or "failed" ? Now() : null), Db.P("@id", id));
        }
        catch
        {
            // Never let bookkeeping break the operation.
        }
    }

    public OpStatus? Get(string id)
    {
        using var conn = _db.Open();
        return Db.QueryOne(conn, "SELECT id, kind, state, result, error, created_at, finished_at FROM ops WHERE id = @id", r =>
        {
            var resultRaw = Db.Str(r, "result");
            object? result = null;
            if (resultRaw.Length > 0)
            {
                try { result = JsonSerializer.Deserialize<object>(resultRaw, Api.Json); }
                catch { result = resultRaw; }
            }
            return new OpStatus(
                Db.Str(r, "id"), Db.Str(r, "kind"), Db.Str(r, "state"),
                Db.Str(r, "state") switch { "done" => 100, "failed" => 100, _ => 10 },
                Db.StrOrNull(r, "error"), result, Db.StrOrNull(r, "error"),
                Parse(Db.Str(r, "created_at")), ParseOrNull(Db.StrOrNull(r, "finished_at")));
        }, Db.P("@id", id));
    }

    public Paged<OpStatus> List(int page, int size)
    {
        page = Math.Max(1, page);
        size = Math.Clamp(size <= 0 ? 25 : size, 1, 100);
        using var conn = _db.Open();
        var total = Convert.ToInt32(Db.Scalar(conn, "SELECT COUNT(*) FROM ops") ?? 0);
        var items = Db.Query(conn,
            "SELECT id, kind, state, result, error, created_at, finished_at FROM ops ORDER BY created_at DESC LIMIT @l OFFSET @o",
            r => new OpStatus(Db.Str(r, "id"), Db.Str(r, "kind"), Db.Str(r, "state"),
                Db.Str(r, "state") switch { "done" => 100, "failed" => 100, _ => 10 },
                null, null, Db.StrOrNull(r, "error"), Parse(Db.Str(r, "created_at")), ParseOrNull(Db.StrOrNull(r, "finished_at"))),
            Db.P("@l", size), Db.P("@o", (page - 1) * size));
        return new Paged<OpStatus>(items, page, size, total);
    }

    /// <summary>Drops finished ops older than a day so the table stays tiny.</summary>
    public int Prune()
    {
        using var conn = _db.Open();
        var cutoff = DateTime.UtcNow.AddDays(-1).ToString("O");
        return Db.Execute(conn, "DELETE FROM ops WHERE created_at < @c AND state IN ('done','failed')", Db.P("@c", cutoff));
    }

    private static DateTime Parse(string value) =>
        DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : DateTime.UtcNow;

    private static DateTime? ParseOrNull(string? value) =>
        value is not null && DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;

    private static class Gates
    {
        private static readonly Dictionary<int, SemaphoreSlim> Map = new();

        public static SemaphoreSlim For(int size)
        {
            lock (Map)
            {
                if (!Map.TryGetValue(size, out var gate))
                {
                    gate = new SemaphoreSlim(size, size);
                    Map[size] = gate;
                }
                return gate;
            }
        }
    }
}