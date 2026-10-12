using ResuClean.Services;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// A deadline is only worth reading if it is read correctly, and a wrong deadline is worse than none:
/// it closes a posting you could still apply to, or leaves a dead one at the top of the list.
///
/// The case that shaped this is real. A posting carried a deadline in the tracker for several rounds
/// of planning while its own page had been saying "expired" the whole time. Staleness compares the
/// posted date to today and cannot see that. So these tests are mostly about what the reader must
/// NOT do.
/// </summary>
public class DeadlineReaderTests
{
    [Theory]
    [InlineData("Applications close on 3 March 2027.", 2027, 3, 3)]
    [InlineData("Apply by 15 January 2027", 2027, 1, 15)]
    [InlineData("Deadline: 2027-02-28", 2027, 2, 28)]
    [InlineData("Applications are due by 30th June 2027", 2027, 6, 30)]
    [InlineData("Closing date 1 September 2027", 2027, 9, 1)]
    [InlineData("Submit your application by March 3, 2027.", 2027, 3, 3)]
    public void A_stated_closing_date_is_read(string description, int year, int month, int day)
    {
        var read = DeadlineReader.Read(description);

        Assert.NotNull(read);
        var when = DateTime.Parse(read!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(year, when.Year);
        Assert.Equal(month, when.Month);
        Assert.Equal(day, when.Day);
    }

    [Theory]
    [InlineData("We were founded in 2019 and are hiring.")]
    [InlineData("You must have 5 years of experience.")]
    [InlineData("Degree from 2014 to 2018, valid work permit required.")]
    [InlineData("Contact us at careers@example.org before applying.")]
    [InlineData("")]
    public void Text_without_a_closing_date_yields_none(string description)
    {
        // The important one: an absent deadline is information, and guessing at one would invent a
        // closing date nobody stated.
        Assert.Null(DeadlineReader.Read(description));
    }

    [Fact]
    public void A_deadline_is_not_invented_from_an_unrelated_date()
    {
        // "starts" and "from" are not closing phrases. Reading 2027 out of a start date would close
        // a live posting on a date the employer never mentioned.
        Assert.Null(DeadlineReader.Read("Role starts 1 April 2027. Reporting line is to the Head of Data."));
    }

    [Theory]
    [InlineData("This job listing has expired.")]
    [InlineData("No longer accepting applications.")]
    [InlineData("Applications are now closed.")]
    [InlineData("This position has been filled.")]
    [InlineData("We have filled this role.")]
    public void A_posting_that_says_it_is_shut_is_recognised(string description)
    {
        // The strongest signal there is: the board is telling you about its own listing.
        Assert.True(DeadlineReader.StatesClosed(description));
    }

    [Theory]
    [InlineData("We are looking for a data engineer to join the team.")]
    [InlineData("Applications are welcome from all backgrounds.")]
    [InlineData("The pipeline is closed for maintenance on Sundays.")]
    public void Ordinary_text_is_not_read_as_a_closure_notice(string description)
    {
        Assert.False(DeadlineReader.StatesClosed(description));
    }

    [Fact]
    public void A_past_deadline_reads_back_as_in_the_past()
    {
        var read = DeadlineReader.Read("Applications close on 1 January 2020.");

        Assert.NotNull(read);
        Assert.True(DateTime.Parse(read!, System.Globalization.CultureInfo.InvariantCulture) < DateTime.UtcNow);
    }

    [Fact]
    public void A_bare_day_and_month_lands_on_the_next_occurrence_not_the_last()
    {
        // "closes 3 March" written in November means next March. Reading it as this March would
        // report a live posting as four months out of date.
        var read = DeadlineReader.Read("Applications close on 3 March.");

        Assert.NotNull(read);
        var when = DateTime.Parse(read!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(when > DateTime.UtcNow.AddMonths(-1),
            $"expected a future date, got {when:yyyy-MM-dd}");
    }
}