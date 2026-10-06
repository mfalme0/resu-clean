using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResuClean.Models;

// ---- Envelopes --------------------------------------------------------------

/// <summary>Every list endpoint returns this so the UI can paginate uniformly.</summary>
public sealed record Paged<T>(IReadOnlyList<T> Items, int Page, int Size, int Total)
{
    public int Pages => Size <= 0 ? 0 : (int)Math.Ceiling(Total / (double)Size);
    public bool HasMore => Page * Size < Total;
}

public sealed record ErrorBody(string Code, string Message, object? Details = null);
public sealed record ErrorResponse(ErrorBody Error);

// ---- Ops --------------------------------------------------------------------

public sealed record OpAccepted(string OpId, string Status, string Kind);
public sealed record OpStatus(
    string OpId,
    string Kind,
    string State,
    int Progress,
    string? Message,
    object? Result,
    string? Error,
    DateTime CreatedAt,
    DateTime? FinishedAt);

// ---- Resumes ----------------------------------------------------------------

public sealed record ResumeSummary(string Id, string Label, string CreatedAt, string UpdatedAt, string? CurrentVersionId, int VersionCount, string? BestFitVersionId);
public sealed record ResumeDetail(ResumeSummary Summary, IReadOnlyList<ResumeVersionDto> Versions);

public sealed record ResumeVersionDto(
    string Id,
    string ResumeId,
    string Label,
    string SourceKind,
    string? ParentId,
    bool BestFit,
    string CreatedAt,
    int CharCount,
    bool HasDocx,
    bool HasPdf);

public sealed record CreateResumeRequest
{
    public string? Label { get; init; }
    /// <summary>Plain text resume content when pasting.</summary>
    public string? Text { get; init; }
    public string? FileName { get; init; }
}

public sealed record CreateResumeResponse(string ResumeId, string VersionId, string Text, bool Extracted);

public sealed record NewVersionRequest(string Label, string Text, string? SourceKind, string? ParentVersionId);

// ---- Clean / ATS / update ---------------------------------------------------

public sealed record CleanRequest
{
    public string? Instruction { get; init; }
    /// <summary>Use a model for the rewrite. Defaults to true; falls back to the deterministic result.</summary>
    public bool? UseModel { get; init; }
}

public sealed record CleanResult(
    string VersionId,
    string Text,
    bool UsedModel,
    string? Model,
    IReadOnlyList<CleanChange> Changes,
    IReadOnlyList<string> Warnings);

public sealed record CleanChange(string Kind, string Detail, int Count);

public sealed record AtsRequest
{
    public string? JobDescription { get; init; }
    public bool? UseModel { get; init; }
}

public sealed record AtsReport(
    string VersionId,
    int Score,
    string Verdict,
    bool UsedModel,
    string? Model,
    IReadOnlyList<AtsCheck> Checks,
    KeywordMatch? Keywords,
    IReadOnlyList<string> PrioritizedFixes,
    int WordCount,
    int LineCount);

public sealed record AtsCheck(
    string Id,
    string Label,
    string Category,
    bool Passed,
    string Detail,
    string Tip,
    int Weight);

public sealed record KeywordMatch(
    int MatchPct,
    int MatchedCount,
    int MissingCount,
    IReadOnlyList<string> Matched,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Extra);

public sealed record UpdateResumeRequest
{
    public string? Instruction { get; init; }
    public string? JobDescription { get; init; }
    public string? JobId { get; init; }
    public bool? UseModel { get; init; }
}

public sealed record UpdateResumeResponse(
    string? VersionId,
    string Text,
    bool UsedModel,
    string? Model,
    IReadOnlyList<PendingFactDto> Pending,
    bool BlockedByPending,
    IReadOnlyList<string> Notes);

public sealed record ExportRequest(string VersionId, string Format);

// ---- Profile ----------------------------------------------------------------

public sealed record ProfileDto(string Voice, string Preferences, Dictionary<string, string> Contact, string UpdatedAt);
public sealed record ProfileFactDto(string Id, string Kind, string Text, Dictionary<string, string> Meta, string Origin, string VerifiedAt);
public sealed record PendingFactDto(string Id, string Kind, string Text, Dictionary<string, string> Meta, string Origin, string Context, string Status, string CreatedAt, string? DecidedAt);

public sealed record UpsertProfileRequest
{
    public string? Voice { get; init; }
    public string? Preferences { get; init; }
    public Dictionary<string, string>? Contact { get; init; }
}

public sealed record AddFactRequest
{
    public string Kind { get; init; } = "other";
    public string Text { get; init; } = "";
    public Dictionary<string, string>? Meta { get; init; }
    public string? Origin { get; init; }
}

public sealed record ApproveFactRequest
{
    public bool Approve { get; init; }
    public string? Reason { get; init; }
}

// ---- Sources ----------------------------------------------------------------

public sealed record SourceDto(
    string Id,
    string Name,
    string Type,
    string? Url,
    string? UrlTemplate,
    IReadOnlyList<string> Regions,
    bool Enabled,
    int RateLimitMs,
    IReadOnlyDictionary<string, string> FieldMap,
    IReadOnlyDictionary<string, string> Selectors,
    string RequiresKey,
    string Notes,
    string CreatedAt)
{
    public SourceDto WithFieldMap(Dictionary<string, string> map) => this with { FieldMap = map };
    public SourceDto WithSelectors(Dictionary<string, string> map) => this with { Selectors = map };
}

public sealed record UpsertSourceRequest
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }
    public string? Url { get; init; }
    public string? UrlTemplate { get; init; }
    public List<string>? Regions { get; init; }
    public bool? Enabled { get; init; }
    public int? RateLimitMs { get; init; }
    public Dictionary<string, string>? FieldMap { get; init; }
    public Dictionary<string, string>? Selectors { get; init; }
    public string? RequiresKey { get; init; }
    public string? Notes { get; init; }
}

public sealed record DetectSourceRequest(string Url, string? Query, string? Location);

public sealed record DetectSourceResponse(
    string Url,
    string SuggestedType,
    string Confidence,
    string? FeedUrl,
    IReadOnlyList<string> Candidates,
    IReadOnlyList<SourcePreviewItem> Preview,
    IReadOnlyList<string> Notes,
    bool Ok,
    string? Error);

public sealed record SourcePreviewItem(string Title, string Company, string Location, string Url, string PostedAt, string Description, int MatchPct);

public sealed record TestSourceRequest
{
    public string? Id { get; init; }
    public string? Url { get; init; }
    public string? Type { get; init; }
    public Dictionary<string, string>? FieldMap { get; init; }
    public Dictionary<string, string>? Selectors { get; init; }
    public string? Query { get; init; }
    public string? Location { get; init; }
    public bool BypassCache { get; init; }
}

public sealed record TestSourceResponse(bool Ok, string Type, int Count, IReadOnlyList<SourcePreviewItem> Items, long ElapsedMs, bool FromCache, string? Error, string? Robots);

public sealed record SourcePackDto(string Id, string Name, string Description, bool Installed, IReadOnlyList<PackEntry> Entries);

public sealed record PackEntry(
    string Key,
    string Name,
    string Type,
    string? Url,
    string? UrlTemplate,
    IReadOnlyList<string> Regions,
    bool EnabledByDefault,
    int RateLimitMs,
    IReadOnlyDictionary<string, string> FieldMap,
    IReadOnlyDictionary<string, string> Selectors,
    string RequiresKey,
    string Notes);

// ---- Jobs -------------------------------------------------------------------

public sealed record JobSearchRequest
{
    public string Query { get; init; } = "";
    public string Location { get; init; } = "";
    public bool RemoteOnly { get; init; }
    public List<string>? SourceIds { get; init; }
    public List<string>? Regions { get; init; }
    public string? ResumeVersionId { get; init; }
    public bool SaveResults { get; init; } = true;
    public int Limit { get; init; } = 50;
}

public sealed record JobDto(
    string Id,
    string Title,
    string Company,
    string Location,
    string Url,
    string? PostedAt,
    string Description,
    string SourceId,
    string SourceName,
    int MatchPct,
    bool Remote,
    bool IsManual,
    string? BestVersionId,
    string CreatedAt);

public sealed record SearchSummary(
    string RunId,
    string Query,
    string Location,
    bool RemoteOnly,
    int Fetched,
    int AfterDedupe,
    int Saved,
    IReadOnlyList<string> SourcesUsed,
    IReadOnlyList<string> SourcesSkipped,
    IReadOnlyList<SourceError> Errors,
    string? ResumeVersionId);

public sealed record SourceError(string Source, string Message);

public sealed record SaveJobRequest
{
    public string? Url { get; init; }
    public string? Title { get; init; }
    public string? Company { get; init; }
    public string? Location { get; init; }
    /// <summary>Pasted posting text, used for keyword matching and kit generation.</summary>
    public string? Text { get; init; }
    public string? SourceName { get; init; }
    public string? PostedAt { get; init; }
    public bool Remote { get; init; }
}

public sealed record SearchUrlResponse(string Url);

// ---- Kits & tracker ---------------------------------------------------------

public sealed record CreateKitRequest
{
    public string JobId { get; init; } = "";
    public string? ResumeVersionId { get; init; }
    public bool CoverLetter { get; init; }
    public bool UseModel { get; init; } = true;
}

/// <summary>
/// A generated kit. File paths are server-side only: the client receives download URLs built from
/// the id, never a raw path.
/// </summary>
public sealed class KitDto
{
    public string Id { get; init; } = "";
    public string JobId { get; init; } = "";
    public string JobTitle { get; init; } = "";
    public string Company { get; init; } = "";
    public string VersionId { get; init; } = "";
    public string VersionLabel { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Body { get; init; } = "";
    public string CoverLetter { get; init; } = "";
    public string ToAddress { get; init; } = "";
    public string ApplyUrl { get; init; } = "";
    public string GeneratedBy { get; init; } = "template";
    public string CreatedAt { get; init; } = "";
    public int WordCount { get; init; }
    public IReadOnlyList<string> FactsUsed { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Attachments { get; init; } = Array.Empty<string>();
    public string? MailtoLink { get; init; } = "";

    /// <summary>True when every file the user needs is on disk and ready to attach.</summary>
    public bool ReadyToSend { get; init; }
}

public sealed record CreateTrackerRequest
{
    public string JobId { get; init; } = "";
    public string? KitId { get; init; }
    public string? ResumeVersionId { get; init; }
    public string? Status { get; init; }
    public string? Notes { get; init; }
}

public sealed record TrackerDto(
    string Id,
    string JobId,
    string JobTitle,
    string Company,
    string KitId,
    string ResumeVersionId,
    string ResumeLabel,
    string Status,
    string Notes,
    string CreatedAt,
    string UpdatedAt);

public sealed record PatchTrackerRequest
{
    public string? Status { get; init; }
    public string? Notes { get; init; }
    public string? KitId { get; init; }
    public string? ResumeVersionId { get; init; }
}

// ---- Models / routing -------------------------------------------------------

public sealed record ProviderDto(
    string Id,
    string Name,
    string Type,
    string BaseUrl,
    string EnvVar,
    bool HasKey,
    string KeySource,
    IReadOnlyList<string> Models,
    bool Enabled,
    string CreatedAt,
    bool Reachable);

public sealed record UpsertProviderRequest
{
    public string? Id { get; init; }
    /// <summary>Built-in template id (openai, anthropic, openrouter, groq, gemini, nvidia, cleanapis, ollama, lmstudio).</summary>
    public string? PresetId { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }
    public string? BaseUrl { get; init; }
    public string? EnvVar { get; init; }
    /// <summary>Stored encrypted at rest and never returned by any endpoint.</summary>
    public string? ApiKey { get; init; }
    public List<string>? Models { get; init; }
    public bool? Enabled { get; init; }
}

public sealed record TestProviderRequest(string? Model);
public sealed record TestProviderResponse(bool Ok, string Message, IReadOnlyList<string> Models, long ElapsedMs, string? Error);

/// <summary>A built-in provider template. Selecting one only fills in the form.</summary>
public sealed record ProviderPresetDto(
    string Id,
    string Name,
    string Type,
    string BaseUrl,
    string EnvVar,
    bool Keyless,
    string KeyHint,
    string DocsUrl,
    string Notes,
    IReadOnlyList<string> SuggestedModels);

/// <summary>Discover the models a provider offers, either for a saved provider or an unsaved draft.</summary>
public sealed record DiscoverModelsRequest
{
    public string? Type { get; init; }
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? EnvVar { get; init; }
}

public sealed record DiscoverModelsResponse(
    bool Ok,
    IReadOnlyList<string> Models,
    string Source,
    long ElapsedMs,
    string? Error,
    string? Hint);

public sealed record RouteEntryDto(int Ordinal, string ProviderId, string ProviderName, string Model);
public sealed record RouteDto(string Workflow, IReadOnlyList<RouteEntryDto> Entries, string? ActiveModel, string? ActiveProvider);

public sealed record SetRouteRequest(List<RouteEntryRequest> Entries);
public sealed record RouteEntryRequest(string ProviderId, string Model);

public sealed record RoutePreviewResponse(bool Ok, string Message, long ElapsedMs, string? Error);

// ---- System -----------------------------------------------------------------

public sealed record CapabilitiesResponse(
    bool AnyModelConfigured,
    IReadOnlyDictionary<string, bool> NeedsModel,
    IReadOnlyList<string> AvailableWorkflows,
    string AuthMode,
    int MaxUploadMb,
    string Version);

public sealed record MemMetricsResponse(
    double RssMb,
    double ManagedHeapMb,
    double PeakRssMb,
    long UptimeSeconds,
    long Requests,
    int OpsActive,
    int Threads);

public sealed record HealthResponse(string Status, string Version, long UptimeSeconds);

public sealed record ConfigResponse(
    bool AuthRequired,
    int MaxUploadMb,
    int MaxConcurrency,
    int CacheTtlSeconds,
    string UserAgent,
    bool Swagger,
    string Version);