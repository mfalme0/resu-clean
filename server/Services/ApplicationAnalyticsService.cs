using Microsoft.Data.Sqlite;
using ResuClean.Data;
using ResuClean.Models;

namespace ResuClean.Services;

/// <summary>
/// Application analytics computed from the tracker alone. Everything is local: response rates,
/// time-to-first-response, per-source breakdowns, monthly trends and the funnel. Nothing leaves
/// the machine, nothing is sent anywhere.
/// </summary>
public sealed class ApplicationAnalyticsService
{
    private readonly Db _db;

    public ApplicationAnalyticsService(Db db) => _db = db;

    public sealed record AnalyticsSummary(
        int TotalApplications,
        int Prepared,
        int Sent,
        int Replied,
        int Interview,
        int Rejected,
        int Closed,
        double ResponseRatePct,
        double InterviewRatePct,
        double RejectionRatePct,
        double AvgDaysToFirstResponse,
        int WithResponseTime);

    public sealed record SourceRow(
        string SourceName,
        int Total,
        int Sent,
        int Replied,
        int Interview,
        double ResponseRatePct);

    public sealed record MonthlyRow(
        string Month,
        int Applications,
        int Sent,
        int Replied,
        int Interview);

    public sealed record FunnelRow(
        string Stage,
        int Count,
        double ConversionPct);

    /// <summary>Overall metrics across all applications.</summary>
    public AnalyticsSummary Summary()
    {
        using var conn = _db.Open();
        var total = Count(conn, "");
        var prepared = Count(conn, "prepared");
        var sent = Count(conn, "sent");
        var replied = Count(conn, "replied");
        var interview = Count(conn, "interview");
        var rejected = Count(conn, "rejected");
        var closed = Count(conn, "closed");

        var responseDenom = sent;
        var responseRate = responseDenom == 0 ? 0 : (double)replied / responseDenom * 100;
        var interviewRate = responseDenom == 0 ? 0 : (double)interview / responseDenom * 100;
        var rejectionRate = responseDenom == 0 ? 0 : (double)rejected / responseDenom * 100;

        var avgDays = AvgDaysToFirstResponse(conn, out var withTime);

        return new AnalyticsSummary(
            total, prepared, sent, replied, interview, rejected, closed,
            responseRate, interviewRate, rejectionRate, avgDays, withTime);
    }

    /// <summary>Per-source breakdown: how each board/source performs.</summary>
    public List<SourceRow> BySource()
    {
        using var conn = _db.Open();
        var rows = Db.Query(conn,
            "SELECT COALESCE(j.source_name, '(manual)') AS source_name, " +
            "COUNT(*) AS total, " +
            "SUM(CASE WHEN a.status = 'sent' THEN 1 ELSE 0 END) AS sent, " +
            "SUM(CASE WHEN a.status = 'replied' THEN 1 ELSE 0 END) AS replied, " +
            "SUM(CASE WHEN a.status = 'interview' THEN 1 ELSE 0 END) AS interview " +
            "FROM applications a LEFT JOIN jobs j ON j.id = a.job_id " +
            "GROUP BY source_name ORDER BY total DESC",
            r => new SourceRow(
                Db.Str(r, "source_name"),
                Db.Int(r, "total"),
                Db.Int(r, "sent"),
                Db.Int(r, "replied"),
                Db.Int(r, "interview"),
                0));
        foreach (var row in rows)
        {
            var rate = row.Sent == 0 ? 0 : (double)row.Replied / row.Sent * 100;
            // SourceRow is a record, so rebuild it with the computed rate.
            rows[rows.IndexOf(row)] = row with { ResponseRatePct = rate };
        }
        return rows;
    }

    /// <summary>Monthly application trend for the last 12 months.</summary>
    public List<MonthlyRow> Monthly()
    {
        using var conn = _db.Open();
        var rows = Db.Query(conn,
            "SELECT strftime('%Y-%m', a.created_at) AS month, " +
            "COUNT(*) AS applications, " +
            "SUM(CASE WHEN a.status = 'sent' THEN 1 ELSE 0 END) AS sent, " +
            "SUM(CASE WHEN a.status = 'replied' THEN 1 ELSE 0 END) AS replied, " +
            "SUM(CASE WHEN a.status = 'interview' THEN 1 ELSE 0 END) AS interview " +
            "FROM applications a " +
            "WHERE a.created_at IS NOT NULL " +
            "GROUP BY month ORDER BY month DESC LIMIT 12",
            r => new MonthlyRow(
                Db.Str(r, "month"),
                Db.Int(r, "applications"),
                Db.Int(r, "sent"),
                Db.Int(r, "replied"),
                Db.Int(r, "interview")));
        return rows;
    }

    /// <summary>Funnel: how applications flow through stages.</summary>
    public List<FunnelRow> Funnel()
    {
        using var conn = _db.Open();
        var total = Count(conn, "");
        var prepared = Count(conn, "prepared");
        var sent = Count(conn, "sent");
        var replied = Count(conn, "replied");
        var interview = Count(conn, "interview");
        var closedOrRejected = Count(conn, "rejected") + Count(conn, "closed");

        var list = new List<FunnelRow>
        {
            new("Applied", total, 100),
            new("Sent", sent, total == 0 ? 0 : (double)sent / total * 100),
            new("Replied", replied, total == 0 ? 0 : (double)replied / total * 100),
            new("Interview", interview, total == 0 ? 0 : (double)interview / total * 100),
            new("Outcome (rejected/closed)", closedOrRejected, total == 0 ? 0 : (double)closedOrRejected / total * 100)
        };
        return list;
    }

    // ---- Helpers ---------------------------------------------------------------

    private static int Count(SqliteConnection conn, string? status)
    {
        var where = string.IsNullOrWhiteSpace(status) ? "" : " WHERE status = @s";
        var parameters = string.IsNullOrWhiteSpace(status)
            ? Array.Empty<Microsoft.Data.Sqlite.SqliteParameter>()
            : new[] { Db.P("@s", status) };
        return Convert.ToInt32(Db.Scalar(conn, $"SELECT COUNT(*) FROM applications{where}", parameters) ?? 0);
    }

    private static double AvgDaysToFirstResponse(SqliteConnection conn, out int withTime)
    {
        // Days from applied_at to updated_at for applications that moved past 'sent'.
        // updated_at moves whenever the status changes, so for an application that went
        // sent -> replied -> interview, updated_at is when it reached interview; the
        // first response is better approximated by the gap to the first change after
        // applied. We use updated_at - applied_at as a bounded estimate, which is
        // honest about being an upper bound rather than an exact first-response date.
        var rows = Db.Query(conn,
            "SELECT applied_at, updated_at FROM applications " +
            "WHERE applied_at IS NOT NULL AND updated_at IS NOT NULL AND status NOT IN ('prepared', 'sent')",
            r => new { Applied = Db.Str(r, "applied_at"), Updated = Db.Str(r, "updated_at") },
            Array.Empty<Microsoft.Data.Sqlite.SqliteParameter>());

        var days = new List<double>();
        foreach (var row in rows)
        {
            if (DateTime.TryParse(row.Applied, out var applied) && DateTime.TryParse(row.Updated, out var updated))
            {
                var d = (updated - applied).TotalDays;
                if (d >= 0) days.Add(d);
            }
        }
        withTime = days.Count;
        return days.Count == 0 ? 0 : days.Sum() / days.Count;
    }
}
