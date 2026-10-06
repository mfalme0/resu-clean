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

        group.MapPost("/pending/{id}/approve", (string id, ProfileService profile) =>
            Api.Guard(() => Task.FromResult(Api.Ok(profile.ApproveFact(id)))));

        group.MapPost("/pending/{id}/reject", (string id, ApproveFactRequest body, ProfileService profile) =>
            Api.Guard(() => Task.FromResult(Api.Ok(new
            {
                rejected = profile.RejectFact(id, body.Reason),
                message = "Rejected. This information will not be added to your profile."
            }))));

        group.MapPost("/pending/approve-all", (ProfileService profile) =>
            Api.Guard(() =>
            {
                var pending = profile.ListPending("pending");
                foreach (var item in pending) profile.ApproveFact(item.Id);
                return Task.FromResult(Api.Ok(new { approved = pending.Count }));
            }));
    }
}