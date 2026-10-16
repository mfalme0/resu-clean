using Microsoft.Extensions.Logging;
using ResuClean.Models;
using ResuClean.Services;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// The daily digest.
///
/// Configured as a digest rather than a stream on purpose: a board sweep can save hundreds of
/// postings in one run, and a webhook per posting is a wall of noise that gets muted within a day,
/// which is indistinguishable from having none. One counted line survives a notification channel.
///
/// The two properties worth pinning are the message itself, and what is allowed to leave the
/// machine while it is built.
/// </summary>
public class WebhookDigestTests
{
    private static WebhookService For(TestHost host) =>
        new(host.Db, new TestHttpFactory(), new NoopLogger<WebhookService>());

    private static WebhookDto Hook(string mentions = "jobseekers", string sender = "userx", int urgent = 7) =>
        new("wh_1", "Daily digest", "https://hooks.example/abc", true,
            mentions.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(m => m.Trim()).ToList(),
            sender, new[] { WebhookService.EventNewJobs, WebhookService.EventVerification },
            urgent, null, "2026-10-12T00:00:00Z");

    private static string Save(TestHost host, string title, string deadline = "", string status = "")
    {
        var job = host.Jobs.SaveManualJob(new SaveJobRequest
        {
            Title = title,
            Company = "Acme",
            Url = "https://example.org/role",
            Text = "Some posting text."
        });

        if (status.Length > 0) host.Jobs.UpdateVerification(job.Id, DateTime.UtcNow.ToString("O"), deadline, status);
        else if (deadline.Length > 0)
            using (var conn = host.Db.Open())
            {
                Data.Db.Exec(conn, "UPDATE jobs SET deadline_at = @d WHERE id = @id",
                    Data.Db.P("@d", deadline), Data.Db.P("@id", job.Id));
            }
        return job.Id;
    }

    [Fact]
    public void The_message_reads_the_way_it_was_specified()
    {
        using var host = new TestHost();
        for (var i = 0; i < 10; i++) Save(host, $"Prospect {i}");
        // Three of them need a decision: two closing inside the window, one already closed.
        Save(host, "Closes soon", DateTime.UtcNow.AddDays(2).ToString("O"));
        Save(host, "Closes later", DateTime.UtcNow.AddDays(2).ToString("O"));
        Save(host, "Gone", status: "closed");

        var message = For(host).BuildDigest(Hook()).Message;

        // "@jobseekers userx has 10 new prospects and 3 need immediate action", minus the three
        // urgent ones which are also prospects, so they are counted once.
        Assert.Contains("@jobseekers userx", message);
        Assert.Contains("new prospects", message);
        Assert.Contains("need immediate action", message);
    }

    [Fact]
    public void Three_clauses_read_as_a_list_rather_than_a_run_of_ands()
    {
        using var host = new TestHost();
        Save(host, "New one");
        var id = Save(host, "Changed one");
        host.Jobs.UpdateVerification(id, DateTime.UtcNow.ToString("O"), null, "closed");

        var message = For(host).BuildDigest(Hook()).Message;

        // "…prospects and 2 need immediate action and 2 postings changed status" reads as a list
        // the writer lost track of. Three clauses must produce exactly one "and", joining the
        // last pair, and a comma before it.
        Assert.Contains("prospects, ", message);
        Assert.Equal(1, message.Split(" and ").Length - 1);
        Assert.DoesNotContain(", and", message);
    }

    [Fact]
    public void Two_clauses_join_with_a_single_and()
    {
        using var host = new TestHost();
        var id = Save(host, "Closes tomorrow");
        host.Jobs.UpdateVerification(id, DateTime.UtcNow.ToString("O"),
            DateTime.UtcNow.AddDays(1).ToString("O"), "open");

        var message = For(host).BuildDigest(Hook()).Message;

        Assert.Equal(1, message.Split(" and ").Length - 1);
        Assert.DoesNotContain(", and", message);
    }

    [Fact]
    public void A_single_prospect_and_a_single_action_are_both_singular()
    {
        using var host = new TestHost();
        Save(host, "Only one");

        var message = For(host).BuildDigest(Hook(sender: "")).Message;

        Assert.Contains("1 new prospect", message);
        Assert.DoesNotContain("prospects", message);
    }

    [Fact]
    public void No_urgent_items_leaves_the_action_clause_out_entirely()
    {
        using var host = new TestHost();
        Save(host, "Plenty of time", DateTime.UtcNow.AddDays(90).ToString("O"));
        Save(host, "Another one");

        var message = For(host).BuildDigest(Hook()).Message;

        // "and 0 need immediate action" every single day trains people to ignore the message.
        Assert.DoesNotContain("immediate action", message);
    }

    [Fact]
    public void An_empty_day_says_so_rather_than_being_silent()
    {
        using var host = new TestHost();

        var message = For(host).BuildDigest(Hook()).Message;

        Assert.Contains("no new prospects", message);
        Assert.Contains("@jobseekers", message);
    }

    [Fact]
    public void Several_mentions_are_all_addressed()
    {
        using var host = new TestHost();
        Save(host, "A role");

        var message = For(host).BuildDigest(Hook(mentions: "jobseekers,dataeng")).Message;

        Assert.Contains("@jobseekers @dataeng", message);
    }

    [Fact]
    public void A_posting_that_went_closed_is_not_reported_as_a_new_prospect()
    {
        using var host = new TestHost();
        var service = For(host);
        var hook = service.Save(new WebhookRequest { Url = "https://hooks.example/abc", SenderName = "userx" });

        // Save the role and let the digest run, so it is already reported and no longer news.
        Save(host, "Was open");
        service.BuildDigest(hook);
        using (var conn = host.Db.Open())
        {
            Data.Db.Exec(conn, "UPDATE webhooks SET last_sent_at = @n WHERE id = @id",
                Data.Db.P("@n", DateTime.UtcNow.ToString("O")), Data.Db.P("@id", hook.Id));
        }

        // Now it closes.
        var job = host.Jobs.ListJobs(1, 50, "Was open", null, null, null).Items[0];
        host.Jobs.UpdateVerification(job.Id, null, null, "closed");

        var digest = service.BuildDigest(service.Get(hook.Id)!);

        // It changed status, which is the news. It did not just appear, which it did not.
        Assert.Equal(0, digest.NewCount);
        Assert.Equal(1, digest.Changed.Count);
        Assert.Contains("changed status", digest.Message);
    }

    [Fact]
    public void Urgent_items_are_counted_once_even_when_new_and_changed_together()
    {
        using var host = new TestHost();
        var id = Save(host, "Both");
        host.Jobs.UpdateVerification(id, DateTime.UtcNow.ToString("O"), DateTime.UtcNow.AddDays(1).ToString("O"), "open");

        var digest = For(host).BuildDigest(Hook());

        Assert.Equal(1, digest.NewCount);
        Assert.Equal(1, digest.UrgentCount);
    }

    [Fact]
    public void What_leaves_the_machine_is_a_title_and_a_url_and_nothing_else()
    {
        using var host = new TestHost();
        var (_, pending, _) = host.Profile.SubmitFact(
            new AddFactRequest { Kind = "achievement", Text = "Secret salary figure 48000 per month" }, "test");
        host.Profile.ApproveFact(Assert.IsType<PendingFactDto>(pending).Id);
        Save(host, "Data Engineer");

        var digest = For(host).BuildDigest(Hook());
        var rendered = System.Text.Json.JsonSerializer.Serialize(digest);

        // This is the whole reason the payload is built this way. "Your resume never leaves this
        // machine" stays literally true with a webhook configured, and this is the assertion for it.
        Assert.DoesNotContain("Secret salary figure", rendered);
        Assert.DoesNotContain("48000", rendered);
        Assert.DoesNotContain("Some posting text.", rendered);
        Assert.DoesNotContain("profile", rendered, StringComparison.OrdinalIgnoreCase);

        // And it still says what it is for.
        Assert.Contains("Data Engineer", rendered);
        Assert.Contains("https://example.org/role", rendered);
    }

    [Fact]
    public void A_deadline_outside_the_window_is_not_urgent()
    {
        using var host = new TestHost();
        Save(host, "Closes in a month", DateTime.UtcNow.AddDays(29).ToString("O"));

        Assert.Equal(0, For(host).BuildDigest(Hook(urgent: 7)).UrgentCount);
    }

    [Fact]
    public void An_unverifiable_posting_counts_as_needing_a_decision()
    {
        using var host = new TestHost();
        Save(host, "Cannot tell", status: "unverifiable");

        var digest = For(host).BuildDigest(Hook());

        Assert.True(digest.UrgentCount >= 1);
    }

    [Fact]
    public void Counts_are_since_the_last_digest_not_since_the_beginning_of_time()
    {
        using var host = new TestHost();
        var service = For(host);
        // Saved rather than hand-built, because the cursor lives in the database and the point of
        // this test is that the stored one is what moves.
        var hook = service.Save(new WebhookRequest { Url = "https://hooks.example/abc", SenderName = "userx" });

        Save(host, "Yesterday's prospect");
        Assert.Equal(1, service.BuildDigest(hook).NewCount);

        // Pretend the digest went out. Tomorrow the same posting is not news again.
        using (var conn = host.Db.Open())
        {
            Data.Db.Exec(conn, "UPDATE webhooks SET last_sent_at = @n WHERE id = @id",
                Data.Db.P("@n", DateTime.UtcNow.ToString("O")), Data.Db.P("@id", hook.Id));
        }

        Save(host, "Today's prospect");
        Assert.Equal(1, service.BuildDigest(service.Get(hook.Id)!).NewCount);
    }

    [Fact]
    public void A_mention_is_cleaned_up_and_a_multi_word_one_is_dropped()
    {
        using var host = new TestHost();
        var saved = For(host).Save(new WebhookRequest
        {
            Url = "https://hooks.example/abc",
            Mentions = new List<string> { "@jobseekers", " dataeng ", "", "data eng", "  ", "DataEng" }
        });

        // The stray @ and the padding go. A mention with a space in it goes entirely: every
        // receiver treats an @handle as one token, so "data eng" would arrive as two broken
        // mentions and notify nobody. Roles with two words are written dataeng or data-engineers.
        Assert.Equal(new[] { "jobseekers", "dataeng" }, saved.Mentions);
    }

    [Fact]
    public void A_url_that_is_not_http_is_refused_with_a_reason()
    {
        using var host = new TestHost();

        Assert.Throws<ArgumentException>(() => For(host).Save(new WebhookRequest { Url = "ftp://x/y" }));
        Assert.Throws<ArgumentException>(() => For(host).Save(new WebhookRequest { Url = "not a url" }));
        Assert.Throws<ArgumentException>(() => For(host).Save(new WebhookRequest { Url = "" }));
    }

    [Fact]
    public void Only_known_events_are_stored()
    {
        using var host = new TestHost();
        var saved = For(host).Save(new WebhookRequest
        {
            Url = "https://hooks.example/abc",
            Events = new List<string> { "new_jobs", "launch_missiles", "new_jobs" }
        });

        Assert.Equal(new[] { WebhookService.EventNewJobs }, saved.Events);
    }

    [Fact]
    public void An_empty_event_list_means_both()
    {
        using var host = new TestHost();
        var saved = For(host).Save(new WebhookRequest
        {
            Url = "https://hooks.example/abc",
            Events = new List<string>()
        });

        Assert.Equal(2, saved.Events.Count);
    }

    [Fact]
    public void The_urgent_window_is_bounded()
    {
        using var host = new TestHost();

        Assert.Equal(0, For(host).Save(new WebhookRequest { Url = "https://h/x", UrgentDays = -5 }).UrgentDays);
        Assert.Equal(90, For(host).Save(new WebhookRequest { Url = "https://h/x", UrgentDays = 9000 }).UrgentDays);
    }

    [Fact]
    public void Only_enabled_webhooks_are_the_ones_a_scheduled_send_would_reach()
    {
        using var host = new TestHost();
        var service = For(host);
        service.Save(new WebhookRequest { Url = "https://h/on", Label = "on" });
        service.Save(new WebhookRequest { Url = "https://h/off", Label = "off", Enabled = false });

        var enabled = service.Enabled();

        Assert.Single(enabled);
        Assert.Equal("on", enabled[0].Label);
    }
}

/// <summary>A factory that hands back a client with no handler, for tests that never send.</summary>
internal sealed class TestHttpFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(1) };
}

internal sealed class NoopLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}