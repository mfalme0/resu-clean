namespace ResuClean.Services.Sources;

/// <summary>
/// Where a posting's text came from, and therefore who is authoritative about whether the role is
/// still open.
///
/// This exists because of a specific failure. On 12 Oct 2026, four Jumia roles were carried as live
/// across three sessions. They were not: the Greenhouse slug 404s, and the LinkedIn mirror 404s too,
/// because that board never held engineering roles. Nothing noticed, because nothing ever asked a
/// system that could say no.
///
/// The distinction that matters is not "where did this text come from" but "can this host tell me
/// the posting is gone". An employer's own ATS can: a deleted Greenhouse posting is a 404 from the
/// authoritative system, and that is evidence. An aggregator is republishing someone else's listing
/// and may serve it for weeks after the employer's board dropped it — a 200 there is not evidence of
/// anything. That gap is what a tracker has to be able to name.
///
/// Deliberately conservative. An unrecognised host is `unknown`, never guessed: a default that reads
/// as confidence is what produced the original problem.
/// </summary>
public static class SourceProvenance
{
    public const string EmployerAts = "employer_ats";
    public const string Aggregator = "aggregator";
    public const string Board = "board";
    public const string Unknown = "unknown";

    /// <summary>
    /// Applicant tracking systems hosted for one employer at a time. A posting here is the employer's
    /// own record of its own role, and a 404 from one of these means the posting is gone.
    /// </summary>
    private static readonly string[] AtsHosts =
    {
        // Greenhouse, in the three host forms it serves from. Per-company boards, so the slug in the
        // path is the employer and the response is authoritative for that employer.
        "boards.greenhouse.io",
        "boards-api.greenhouse.io",
        "job-boards.greenhouse.io",
        "jobs.lever.co",
        "jobs.ashbyhq.com",
        "apply.workable.com",
        "jobs.smartrecruiters.com",
        "recruiting2.ultipro.com",
        "careers.smartrecruiters.com",
        "myworkdayjobs.com",
        "wd1.myworkdayjobs.com",
        "wd2.myworkdayjobs.com",
        "wd3.myworkdayjobs.com",
        "wd4.myworkdayjobs.com",
        "wd5.myworkdayjobs.com",
        "jobs.jobvite.com",
        "hire.withgoogle.com",
        "byjobs.be",
        "teamtailor.com",
        "breezy.hr",
        "workforus.com",
    };

    /// <summary>
    /// Third-party indexes that republish listings they did not author. Useful for volume; never
    /// authoritative about a posting still existing.
    /// </summary>
    private static readonly string[] AggregatorHosts =
    {
        "indeed.com",
        "jooble.org",
        "remotive.com",
        "remoteok.com",
        "adzuna.com",
        "glassdoor.com",
        "ziprecruiter.com",
        "monster.com",
        "linkedin.com",
        "simplyhired.com",
        "jobicy.com",
        "arbeitnow.com",
        "himalayas.app",
        "justremote.co",
        "jobboard.io",
    };

    /// <summary>
    /// General listings sites. Not republishing in the aggregator sense and not an employer's own
    /// system either — a company page on one of these is still a copy.
    /// </summary>
    private static readonly string[] BoardHosts =
    {
        "weworkremotely.com",
        "remoteok.io",
        "brighterme.com",
        "otta.com",
        "wellfound.com",
        "hired.com",
        "dice.com",
        "levels.fyi",
        "flexjobs.com",
        "remote.com",
        "workingnomads.com",
    };

    /// <summary>
    /// Classifies a URL by host. Returns <see cref="Unknown"/> rather than a guess when the host is
    /// not one we recognise, or when the URL cannot be parsed at all.
    /// </summary>
    public static string For(string? url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri)) return Unknown;
        return ForHost(uri.Host);
    }

    /// <summary>Classifies by bare host, so the seed packs can stamp an entry without building a URI.</summary>
    public static string ForHost(string? host)
    {
        var h = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0) return Unknown;

        // Strip a leading "www." so a board and its www form are not two different things.
        if (h.StartsWith("www.", StringComparison.Ordinal)) h = h[4..];

        return Matches(h, AtsHosts) ? EmployerAts
            : Matches(h, AggregatorHosts) ? Aggregator
            : Matches(h, BoardHosts) ? Board
            : Unknown;
    }

    /// <summary>
    /// Host match, either exact or as a subdomain. Suffix matching on the dot is the important part:
    /// "evil-greenhouse.io" must not match "greenhouse.io".
    /// </summary>
    private static bool Matches(string host, string[] known)
    {
        foreach (var candidate in known)
        {
            var c = candidate.Trim().ToLowerInvariant();
            if (c.Length == 0 || c.Contains(' ')) continue;
            if (host == c || host.EndsWith("." + c, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>A short sentence for the UI, or null when we genuinely do not know.</summary>
    public static string Describe(string provenance) => provenance switch
    {
        EmployerAts => "The employer's own board. Only this can tell you the posting is gone.",
        Aggregator => "A third-party index. A listing here can outlive the employer's own posting, so it is not evidence the role is open.",
        Board => "A general listings site. The employer's own posting is elsewhere and may already be closed.",
        _ => "Unknown where this came from, so treat it as unverified."
    };
}