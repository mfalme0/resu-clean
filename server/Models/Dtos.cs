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
    IReadOnlyList<string> Warnings,
    /// <summary>How the rewrite was judged, and what the rejected alternative was. Null when no model ran.</summary>
    RewriteOutcome? Rewrite = null);

/// <summary>
/// The measured outcome of a model rewrite. The rejected text is carried back rather than discarded,
/// so a user can overrule the gate after reading why it fired.
/// </summary>
public sealed record RewriteOutcome(
    bool Accepted,
    int BaselineScore,
    int CandidateScore,
    int BaselineMissing,
    int CandidateMissing,
    string Reason,
    /// <summary>Present only when the rewrite was rejected: the text that was not taken.</summary>
    string? RejectedText = null);

public sealed record RewriteEvaluationDto(
    string Id,
    string Workflow,
    string Model,
    int BaselineScore,
    int CandidateScore,
    int BaselineMissing,
    int CandidateMissing,
    bool Accepted,
    string Reason,
    string CreatedAt);

public sealed record RewriteLedgerResponse(
    IReadOnlyList<RewriteLedgerRowDto> Rows,
    IReadOnlyList<RewriteEvaluationDto> Recent);

public sealed record RewriteLedgerRowDto(
    string Workflow,
    string Model,
    int Total,
    int Accepted,
    int AvgDelta);

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
    IReadOnlyList<string> Notes,
    /// <summary>How the rewrite was judged, and what the rejected alternative was.</summary>
    RewriteOutcome? Rewrite = null);

public sealed record ExportRequest(string VersionId, string Format);

public sealed record AcceptTextRequest(string Text, string? Label = null);

// ---- Grounding ----------------------------------------------------------

public sealed record FactClaimDto(
    string Text,
    string Category,
    IReadOnlyList<string> FactIds,
    bool Grounded,
    string Note);

public sealed record FactCheckReportDto(
    string VersionId,
    int Checked,
    int Grounded,
    int Ungrounded,
    IReadOnlyList<FactClaimDto> Claims,
    bool UsedModel,
    string? Model,
    string Method,
    IReadOnlyList<PendingFactDto> Queued);

public sealed record FactCheckDto(
    string Id,
    string VersionId,
    string Model,
    string Method,
    int Checked,
    int Ungrounded,
    string CreatedAt);

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
    string CreatedAt,
    /// <summary>
    /// employer_ats | aggregator | board | unknown. Where the posting text came from, and therefore
    /// who is authoritative about whether the role is still open. An aggregator is a convenience; it
    /// is not evidence, and only the employer's own system can return a 404.
    /// </summary>
    string Provenance = "unknown")
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
    /// <summary>employer_ats | aggregator | board. Anything else is stored as unknown.</summary>
    public string? Provenance { get; init; }
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
    string? Error,
    /// <summary>employer_ats | aggregator | board | unknown, from the host.</summary>
    string Provenance = "unknown");

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
    string CreatedAt,
    /// <summary>Blockers in the posting that nothing in your profile covers. 0 when none, or when unchecked.</summary>
    int UnmetBlockers = 0,
    int UnmetRequired = 0,
    /// <summary>False until the posting has been classified. Unchecked is not the same as clear.</summary>
    bool RequirementsChecked = false,
    /// <summary>When this posting was last confirmed open. Null means never confirmed.</summary>
    string? LastVerifiedAt = null,
    /// <summary>A closing date read out of the posting. Null means the posting states none.</summary>
    string? DeadlineAt = null,
    /// <summary>open | closed | unverifiable, from a check against the employer's own system.</summary>
    string Status = "",
    /// <summary>What a user can act on today: open, closed, expired or unknown.</summary>
    string EffectiveStatus = "unknown");

public sealed record JobRequirementDto(
    string Text,
    /// <summary>blocker | required | preferred | implicit</summary>
    string Class,
    bool Met,
    IReadOnlyList<string> Missing,
    string? Evidence);

public sealed record JobRequirementsDto(
    string JobId,
    IReadOnlyList<JobRequirementDto> Requirements,
    int UnmetBlockers,
    int UnmetRequired,
    bool UsedModel,
    string? Model,
    string Method);

public sealed record SkillTermDto(
    string Id,
    string Term,
    string Normalized,
    int Occurrences,
    string Origin,
    string CreatedAt);

public sealed record ProposeDictionaryRequest(bool? UseModel);

public sealed record ProposeDictionaryResponse(
    IReadOnlyList<PendingFactDto> Queued,
    int Proposed);

// ---- Job quality --------------------------------------------------------

public sealed record JobFlagDto(string Kind, string Severity, string Detail, string? Quote);

public sealed record JobQualityDto(
    string JobId,
    IReadOnlyList<JobFlagDto> Flags,
    bool HasWarnings,
    bool UsedModel,
    string Method);

// ---- Gap ramp -----------------------------------------------------------

public sealed record GapSuggestionDto(
    string Text,
    string BuildsOnFactId,
    string BuildsOnFactText,
    string Why);

public sealed record GapRampRequest
{
    public string? Requirement { get; init; }
    public string? RequirementClass { get; init; }
    public List<string>? Missing { get; init; }
}

public sealed record GapRampDto(
    string Requirement,
    string Class,
    IReadOnlyList<string> Missing,
    IReadOnlyList<GapSuggestionDto> Suggestions,
    string? NearestFact,
    /// <summary>False for an unmet requirement this week cannot close, such as a years requirement.</summary>
    bool Closable,
    bool UsedModel,
    string Method);

// ---- Interview ----------------------------------------------------------

public sealed record InterviewQuestionDto(
    string Text,
    string Probes,
    string GroundedIn,
    bool Risk,
    string RiskNote);

public sealed record InterviewPrepDto(
    string JobId,
    string VersionId,
    IReadOnlyList<InterviewQuestionDto> Questions,
    bool UsedModel,
    string? Model,
    string Method,
    IReadOnlyList<string> Notes);

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

public sealed record ImportTrackerRequest
{
    /// <summary>The pasted file contents.</summary>
    public string? Json { get; init; }
    /// <summary>Overwrite entries that are already tracked. Off by default so an old file cannot undo newer work.</summary>
    public bool? Overwrite { get; init; }
}

// ---- Importing positions tracked elsewhere -------------------------------------

public sealed record ImportPositionsRequest
{
    /// <summary>The pasted file contents: one key per position.</summary>
    public string? Json { get; init; }
    /// <summary>Update positions already imported under the same key. Off by default.</summary>
    public bool? Overwrite { get; init; }
}

/// <summary>What one position would become, read out of its note where possible.</summary>
/// <param name="Missing">Fields the note did not carry, named so they can be filled in by hand.</param>
public sealed record ParsedPositionDto(
    string Key,
    string Stage,
    string? Company,
    string? Title,
    string? Location,
    string? Url,
    string? DeadlineAt,
    string? Verified,
    string Applied,
    string Updated,
    string Note,
    IReadOnlyList<string> Missing);

public sealed record PositionPreviewDto(
    int Total,
    IReadOnlyList<ParsedPositionDto> Positions,
    IReadOnlyList<string> Warnings);

public sealed record PositionImportResultDto(
    int Imported,
    int Created,
    int Updated,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Warnings);

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

public sealed record ModelUsageDto(
    string Id,
    string Workflow,
    string Provider,
    string Model,
    int PromptTokens,
    int CompletionTokens,
    bool Cached,
    long ElapsedMs,
    string CreatedAt);

public sealed record ModelUsageSummaryDto(
    string Workflow,
    int Calls,
    int CachedCalls,
    int Tokens,
    long AvgMs);

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

public sealed record ModelUsageResponse(
    IReadOnlyList<ModelUsageSummaryDto> ByWorkflow,
    IReadOnlyList<ModelUsageDto> Recent);

public sealed record ConfigResponse(
    bool AuthRequired,
    int MaxUploadMb,
    int MaxConcurrency,
    int CacheTtlSeconds,
    string UserAgent,
    bool Swagger,
    string Version);