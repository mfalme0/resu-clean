using ResuClean.Services;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// The tracker export/import is only worth having if it is lossless. These pin the format, the
/// stage vocabulary in both directions, and the guarantee that an export of an import reproduces
/// the same file.
/// </summary>
public class TrackerTransferTests
{
    private static TrackerTransfer TransferFor(TestHost host) => new(host.Db, host.Jobs);

    /// <summary>The shape this feature was built for, with the stage values a person actually uses.</summary>
    private const string SampleFile = """
        {
          "mkopa":  { "stage": "applied", "note": "", "applied": "2026-10-11", "updated": "2026-10-11" },
          "zeno":    { "stage": "applied", "note": "asked for references", "applied": "2026-10-11", "updated": "2026-10-11" },
          "tatu_ict":{ "stage": "todo",     "note": "", "applied": "", "updated": "" },
          "bluegeko":{ "stage": "todo",     "note": "", "applied": "", "updated": "" }
        }
        """;

    [Fact]
    public void An_imported_file_becomes_tracked_applications()
    {
        using var host = new TestHost();
        var summary = TransferFor(host).ImportJson(SampleFile, overwrite: false);

        Assert.Equal(4, summary.Imported);
        Assert.Equal(4, summary.Created);
        Assert.Equal(0, summary.Updated);
        Assert.Empty(summary.Skipped);

        var tracker = host.App.ListTracker(1, 50, null);
        Assert.Equal(4, tracker.Total);
    }

    [Fact]
    public void The_two_stage_words_map_onto_what_the_tracker_stores()
    {
        using var host = new TestHost();
        TransferFor(host).ImportJson(SampleFile, overwrite: false);

        var entries = host.App.ListTracker(1, 50, null).Items;

        // "applied" means it went out, which is 'sent'. "todo" means it did not, which is 'prepared'.
        Assert.Equal(2, entries.Count(e => e.Status == "sent"));
        Assert.Equal(2, entries.Count(e => e.Status == "prepared"));
    }

    [Fact]
    public void Notes_survive_the_round_trip()
    {
        using var host = new TestHost();
        TransferFor(host).ImportJson(SampleFile, overwrite: false);

        var json = TransferFor(host).ExportJson();
        Assert.Contains("asked for references", json);
    }

    [Fact]
    public void Exporting_reproduces_the_file_it_was_given()
    {
        using var host = new TestHost();
        var transfer = TransferFor(host);
        transfer.ImportJson(SampleFile, overwrite: false);

        var exported = transfer.ExportJson();

        using var original = System.Text.Json.JsonDocument.Parse(SampleFile);
        using var round = System.Text.Json.JsonDocument.Parse(exported);

        var originalKeys = original.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(k => k);
        var roundKeys = round.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(k => k);
        Assert.Equal(originalKeys, roundKeys);

        foreach (var key in originalKeys)
        {
            var before = original.RootElement.GetProperty(key);
            var after = round.RootElement.GetProperty(key);
            Assert.Equal(before.GetProperty("stage").GetString(), after.GetProperty("stage").GetString());
            Assert.Equal(before.GetProperty("note").GetString(), after.GetProperty("note").GetString());
            Assert.Equal(before.GetProperty("applied").GetString(), after.GetProperty("applied").GetString());
            Assert.Equal(before.GetProperty("updated").GetString(), after.GetProperty("updated").GetString());
        }
    }

    [Fact]
    public void A_todo_entry_has_no_dates_and_an_applied_one_does()
    {
        using var host = new TestHost();
        var transfer = TransferFor(host);
        transfer.ImportJson(SampleFile, overwrite: false);

        using var doc = System.Text.Json.JsonDocument.Parse(transfer.ExportJson());
        Assert.Equal("", doc.RootElement.GetProperty("bluegeko").GetProperty("applied").GetString());
        Assert.Equal("", doc.RootElement.GetProperty("bluegeko").GetProperty("updated").GetString());
        Assert.Equal("2026-10-11", doc.RootElement.GetProperty("mkopa").GetProperty("applied").GetString());
    }

    [Fact]
    public void A_slug_humanises_on_the_way_in_and_slugifies_again_on_the_way_out()
    {
        using var host = new TestHost();
        var transfer = TransferFor(host);
        transfer.ImportJson("""{ "tatu_ict": { "stage": "todo", "note": "", "applied": "", "updated": "" } }""", false);

        var entries = host.App.ListTracker(1, 50, null).Items;
        Assert.Equal("Tatu Ict", Assert.Single(entries).Company);
        Assert.Contains("\"tatu_ict\"", transfer.ExportJson());
    }

    [Fact]
    public void Reimporting_does_not_duplicate_or_overwrite_by_default()
    {
        using var host = new TestHost();
        var transfer = TransferFor(host);
        transfer.ImportJson(SampleFile, overwrite: false);

        var second = transfer.ImportJson("""
            { "mkopa": { "stage": "todo", "note": "changed my mind", "applied": "", "updated": "" } }
            """, overwrite: false);

        Assert.Equal(0, second.Imported);
        Assert.Contains("mkopa: already tracked.", second.Skipped);
        Assert.Equal(4, host.App.ListTracker(1, 50, null).Total);
    }

    [Fact]
    public void Overwriting_is_available_and_updates_in_place()
    {
        using var host = new TestHost();
        var transfer = TransferFor(host);
        transfer.ImportJson(SampleFile, overwrite: false);

        var second = transfer.ImportJson("""
            { "mkopa": { "stage": "interview", "note": "panel on Friday", "applied": "2026-10-11", "updated": "2026-10-14" } }
            """, overwrite: true);

        Assert.Equal(1, second.Updated);
        Assert.Equal(0, second.Created);
        Assert.Equal(4, host.App.ListTracker(1, 50, null).Total);
        Assert.Contains("interview", transfer.ExportJson());
    }

    [Theory]
    [InlineData("todo", "prepared")]
    [InlineData("applied", "sent")]
    [InlineData("sent", "sent")]
    [InlineData("replied", "replied")]
    [InlineData("interview", "interview")]
    [InlineData("rejected", "rejected")]
    public void Every_stage_word_maps_to_itself_once_round_tripped(string stage, string expected)
    {
        using var host = new TestHost();
        var transfer = TransferFor(host);
        transfer.ImportJson($$"""{ "acme": { "stage": "{{stage}}", "note": "", "applied": "2026-01-02", "updated": "2026-01-02" } }""", true);

        Assert.Equal(expected, Assert.Single(host.App.ListTracker(1, 50, null).Items).Status);

        using var doc = System.Text.Json.JsonDocument.Parse(transfer.ExportJson());
        var back = doc.RootElement.GetProperty("acme").GetProperty("stage").GetString();
        // The file format has its own word for each stored stage: prepared has none of its own, and
        // sent reads as applied because that is what happened to it.
        var expectedWord = expected switch { "prepared" => "todo", "sent" => "applied", _ => expected };
        Assert.Equal(expectedWord, back);
    }

    [Fact]
    public void Every_stage_this_tool_stores_has_a_word_in_the_file_format()
    {
        using var host = new TestHost();
        var transfer = TransferFor(host);
        // prepared, sent, replied, interview and rejected are the whole of it.
        foreach (var status in new[] { "prepared", "sent", "replied", "interview", "rejected" })
        {
            host.App.AddTrackerEntry(NewEntry(host, status));
        }

        using var doc = System.Text.Json.JsonDocument.Parse(transfer.ExportJson());
        var stages = doc.RootElement.EnumerateObject()
            .Select(p => p.Value.GetProperty("stage").GetString()).ToHashSet();
        Assert.DoesNotContain(stages, stage => stage is null or "");
    }

    [Fact]
    public void Malformed_json_is_refused_with_a_reason_rather_than_half_imported()
    {
        using var host = new TestHost();
        var error = Assert.Throws<ArgumentException>(() => TransferFor(host).ImportJson("{ not json", false));
        Assert.Contains("not valid JSON", error.Message);
        Assert.Equal(0, host.App.ListTracker(1, 50, null).Total);
    }

    [Fact]
    public void An_empty_file_is_refused_rather_than_imported_as_nothing()
    {
        using var host = new TestHost();
        Assert.Throws<ArgumentException>(() => TransferFor(host).ImportJson("{}", false));
    }

    [Fact]
    public void A_non_object_entry_is_skipped_and_says_so()
    {
        using var host = new TestHost();
        var summary = TransferFor(host).ImportJson("""{ "mkopa": "just a string", "zeno": { "stage": "applied", "note": "", "applied": "2026-10-11", "updated": "2026-10-11" } }""", false);

        Assert.Equal(1, summary.Imported);
        Assert.Contains(summary.Skipped, s => s.Contains("mkopa"));
    }

    [Fact]
    public void Company_names_with_punctuation_slug_the_same_way_twice()
    {
        using var host = new TestHost();
        var transfer = TransferFor(host);
        transfer.ImportJson("""{ "blue_geko": { "stage": "todo", "note": "", "applied": "", "updated": "" } }""", false);
        host.Execute("UPDATE jobs SET company = 'Blue & Geko Ltd'");

        // The slug changes because the company name changed, which is correct: the key is derived
        // from what is stored, not from what was imported.
        Assert.Contains("blue_geko_ltd", transfer.ExportJson());
    }

    [Fact]
    public void A_tracker_with_no_entries_exports_an_empty_object_rather_than_failing()
    {
        using var host = new TestHost();
        Assert.Equal("{}", TransferFor(host).ExportJson().Trim());
    }

    private static ResuClean.Models.CreateTrackerRequest NewEntry(TestHost host, string status)
    {
        // The jobs table dedupes on (url, title, company), so each entry needs its own role.
        var job = host.Jobs.SaveManualJob(new ResuClean.Models.SaveJobRequest
        {
            Title = $"Analyst ({status})",
            Company = "Acme",
            Text = "Role description"
        });
        return new ResuClean.Models.CreateTrackerRequest { JobId = job.Id, Status = status };
    }
}