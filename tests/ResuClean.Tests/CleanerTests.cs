using ResuClean.Services;
using Xunit;

namespace ResuClean.Tests;

public class CleanerTests
{
    [Fact]
    public void Removes_icons_and_private_use_glyphs()
    {
        var input = "Jane Doe \uE806 jane@example.com \uF0E7 Nairobi";
        var result = Cleaner.Run(input);
        Assert.DoesNotContain('\uE806', result.Text);
        Assert.DoesNotContain('\uF0E7', result.Text);
        Assert.Contains("jane@example.com", result.Text);
    }

    [Fact]
    public void Normalises_bullet_characters_to_hyphen()
    {
        var input = "\u2022 Led the migration\n\u2022 Reduced errors";
        var result = Cleaner.Run(input);
        Assert.StartsWith("- Led the migration", result.Text);
        Assert.DoesNotContain('\u2022', result.Text);
    }

    [Fact]
    public void Collapses_blank_lines_to_one()
    {
        var input = "Summary\n\n\n\nExperience\n\n\n\nEducation";
        var result = Cleaner.Run(input);
        Assert.DoesNotContain("\n\n\n", result.Text);
    }

    [Fact]
    public void Strips_indentation_and_trailing_whitespace()
    {
        var input = "    Jane Doe   \n\tAnalyst\t";
        var result = Cleaner.Run(input);
        Assert.Equal("Jane Doe\nAnalyst\n", result.Text);
    }

    [Fact]
    public void Removes_zero_width_characters()
    {
        var input = "Ja\u200Bne\u00AD Doe";
        var result = Cleaner.Run(input);
        Assert.Equal("Jane Doe\n", result.Text);
    }

    [Fact]
    public void Tightens_thousands_separators()
    {
        var result = Cleaner.Run("Processed 1 ,200 transactions for KES 400 ,000.");
        Assert.Contains("1,200", result.Text);
        Assert.Contains("400,000", result.Text);
    }

    [Fact]
    public void Removes_comma_inside_month_year_dates()
    {
        var result = Cleaner.Run("Mar 2021, - Present");
        Assert.Contains("Mar 2021 - Present", result.Text);
    }

    [Fact]
    public void Strips_trailing_punctuation_from_headings()
    {
        var result = Cleaner.Run("EXPERIENCE.\n- Led a team");
        Assert.Contains("EXPERIENCE\n", result.Text);
        Assert.DoesNotContain("EXPERIENCE.", result.Text);
    }

    [Fact]
    public void Converts_crlf_to_lf()
    {
        var result = Cleaner.Run("Line one\r\nLine two\rLine three");
        Assert.DoesNotContain('\r', result.Text);
    }

    [Fact]
    public void Reports_no_changes_for_clean_text()
    {
        var result = Cleaner.Run(TestHost.SampleResume);
        Assert.Contains(result.Changes, c => c.Kind == "none");
    }

    [Fact]
    public void Empty_input_returns_a_warning_not_an_exception()
    {
        var result = Cleaner.Run("   ");
        Assert.Equal(string.Empty, result.Text);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Repeated_words_are_removed()
    {
        var result = Cleaner.Run("I led led the team.");
        Assert.Contains("I led the team", result.Text);
    }
}

public class KeywordsTests
{
    [Fact]
    public void Extracts_multiword_skills_before_single_tokens()
    {
        var keywords = Keywords.Extract("We use machine learning and power bi daily", 10);
        Assert.Contains("machine learning", keywords);
        Assert.Contains("power bi", keywords);
    }

    [Fact]
    public void Drops_stop_words()
    {
        var keywords = Keywords.Extract("the team and for with a of", 10);
        Assert.DoesNotContain("the", keywords);
        Assert.DoesNotContain("team", keywords);
    }

    [Fact]
    public void Compare_reports_matched_and_missing()
    {
        var resume = "SQL, Power BI, Python, stakeholder management";
        var posting = "Looking for a SQL analyst with Power BI and dbt experience.";
        var result = Keywords.Compare(resume, posting);
        Assert.Contains("sql", result.Matched);
        Assert.Contains("power bi", result.Matched);
        Assert.Contains("dbt", result.Missing);
        Assert.InRange(result.MatchPct, 0, 100);
    }

    [Fact]
    public void ScoreAgainst_is_zero_for_empty_resume()
    {
        Assert.Equal(0, Keywords.ScoreAgainst("SQL Power BI analyst role", ""));
    }

    [Fact]
    public void ScoreAgainst_rewards_overlap()
    {
        const string posting = "Senior data analyst with SQL, Power BI and dbt.";
        var strong = Keywords.ScoreAgainst(posting, "data analyst SQL Power BI dbt Python");
        var weak = Keywords.ScoreAgainst(posting, "chef pastry bakery");
        Assert.True(strong > weak, $"expected {strong} > {weak}");
    }
}