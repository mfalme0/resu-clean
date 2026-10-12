using ResuClean;
using ResuClean.Models;
using ResuClean.Services;

namespace ResuClean.Endpoints;

public static class ProfileEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/profile").WithTags("Profile");

        group.MapGet("/", (ProfileService profile) => Api.Guard(() => Task.FromResult(Api.Ok(profile.GetProfile()))));

        group.MapPut("/", (UpsertProfileRequest body, ProfileService profile) =>
            Api.Guard(async () => Api.Ok(await Task.FromResult(profile.UpdateProfile(body)))));

        group.MapGet("/facts", (ProfileService profile, HttpRequest request) =>
            Api.Guard(() => Task.FromResult(Api.Ok(profile.ListFacts(Api.Param(request, "kind"))))));

        // Submitting a fact queues it for approval. It never becomes verified silently.
        group.MapPost("/facts", (AddFactRequest body, ProfileService profile) =>
            Api.Guard(() =>
            {
                var (fact, pending, duplicate) = profile.SubmitFact(body, "profile_editor");
                if (duplicate)
                    return Task.FromResult(Api.Ok(new
                    {
                        added = false,
                        duplicate = true,
                        fact,
                        pending = (PendingFactDto?)null,
                        message = "This fact is already in your verified profile, so nothing was added."
                    }));
                return Task.FromResult(Api.Ok(new
                {
                    added = true,
                    duplicate = false,
                    fact = (ProfileFactDto?)null,
                    pending,
                    message = "Queued for your approval. It joins your profile only after you approve it."
                }));
            }));

        group.MapDelete("/facts/{id}", (string id, ProfileService profile) =>
            Api.Guard(() => Task.FromResult(profile.DeleteFact(id)
                ? Api.Ok(new { deleted = true })
                : Api.Error(StatusCodes.Status404NotFound, "not_found", "That fact is not in your profile."))));

        group.MapGet("/pending", (ProfileService profile, HttpRequest request) =>
            Api.Guard(() => Task.FromResult(Api.Ok(profile.ListPending(Api.Param(request, "status") ?? "pending")))));

        group.MapGet("/pending/count", (ProfileService profile) =>
            Api.Guard(() => Task.FromResult(Api.Ok(new { count = profile.PendingCount() }))));

        group.MapPost("/pending/{id}/approve", (string id, ProfileService profile, SkillDictionaryService dictionary) =>
            Api.Guard(() => Task.FromResult(Api.Ok(ApproveOne(id, profile, dictionary)))));

        group.MapPost("/pending/{id}/reject", (string id, ApproveFactRequest body, ProfileService profile) =>
            Api.Guard(() => Task.FromResult(Api.Ok(new
            {
                rejected = profile.RejectFact(id, body.Reason),
                message = "Rejected. This information will not be added to your profile."
            }))));

        group.MapPost("/pending/approve-all", (ProfileService profile, SkillDictionaryService dictionary) =>
            Api.Guard(() =>
            {
                var pending = profile.ListPending("pending");
                foreach (var item in pending) ApproveOne(item.Id, profile, dictionary);
                return Task.FromResult(Api.Ok(new { approved = pending.Count }));
            }));

        // ---- Skill dictionary -------------------------------------------------

        group.MapGet("/dictionary", (SkillDictionaryService dictionary) => Api.Ok(dictionary.List()));

        group.MapDelete("/dictionary/{id}", (string id, SkillDictionaryService dictionary) =>
            Api.Guard(() => Task.FromResult(dictionary.Remove(id)
                ? Api.Ok(new { removed = true })
                : Api.Error(StatusCodes.Status404NotFound, "not_found", "That term is not in the dictionary."))));

        /// <summary>
        /// Looks through your resumes for tools the dictionary does not know and queues them for
        /// approval. Nothing joins the dictionary until you approve it, because the dictionary
        /// changes every score.
        /// </summary>
        group.MapPost("/dictionary/propose", (ProposeDictionaryRequest body, SkillDictionaryService dictionary) =>
            Api.Guard(async () =>
            {
                var queued = await dictionary.ProposeAsync(body.UseModel ?? true).ConfigureAwait(false);
                return Api.Ok(new ProposeDictionaryResponse(queued, queued.Count));
            }));
    }

    /// <summary>
    /// Approves one pending item. A dictionary proposal is both a term and a skill you use, so
    /// approving it does two things through one decision: it joins the dictionary that scoring
    /// reads, and it becomes a verified skill fact. Two queues and a second approval for the same
    /// statement would be asking the same question twice.
    /// </summary>
    private static ProfileFactDto ApproveOne(string id, ProfileService profile, SkillDictionaryService dictionary)
    {
        var pending = profile.ListPending("pending").FirstOrDefault(p => p.Id == id)
            ?? throw new KeyNotFoundException("That item is no longer waiting for approval.");

        var fact = profile.ApproveFact(id);
        if (pending.Origin == "dictionary") dictionary.Add(pending.Text, origin: "dictionary");
        return fact;
    }
}