using System.Globalization;
using System.Text.RegularExpressions;

namespace ResuClean.Services;

/// <summary>
/// Reads a closing date out of a posting's own words.
///
/// Staleness compares the posted date against today, which is a proxy. It cannot tell you that a
/// posting two days old has a deadline that passed yesterday, and that combination is not rare:
/// boards rarely take a listing down, so a closed role can sit there looking fresh for months.
///
/// The Google case is the argument for doing this at all. That posting carried a deadline in the
/// tracker for several rounds of planning while the page had been saying "expired" the whole time.
/// Nothing here would have caught it except the posting stating when it ends.
///
/// Three outcomes, and the third matters most:
///   - a date was stated and is in the future: it closes on that date
///   - a date was stated and has passed: it is already closed
///   - nothing was stated: no deadline, which is information and is reported as such
///
/// Deliberately narrow. A date is only read when a closing-date phrase is immediately before it, so
/// a CV start date, a graduation year or an expiry on a visa is not mistaken for a deadline. An
/// unparsed deadline is reported as absent rather than guessed at.
/// </summary>
public static partial class DeadlineReader
{
    /// <summary>
    /// Phrases that mark the following date as a closing date. Anchored to the date, not free
    /// floating, so "applications close on 3 March" works and "3 March" alone does not.
    /// </summary>
    // One string rather than concatenated consts: the regex source generator cannot resolve a
    // const referenced from the attribute, and an attribute argument has to be a compile-time
    // constant. The line is long on purpose — a wrapped regex is a misread one.
    // A closing phrase, then a date. Three shapes are accepted after it: day-month[-year],
    // month-day[-year], and ISO or slashed. The year is optional, because "closes 3 March" is a
    // real way to write it and requiring a year would read none of those.
    [GeneratedRegex(@"(?i)\b(?:applications?|apply|submit|submission|closing date|closing|close|closes|deadline|due(?: by)?|last date|expires?|ends?|cv|resume)\b[^.\n]{0,40}?(?:on |by |before |:)?\s*(\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{4}|(?:jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\.?\s+\d{1,2}(?:st|nd|rd|th)?(?:,?\s*\d{4})?|\d{1,2}(?:st|nd|rd|th)?\s+(?:of\s+)?(?:jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\.?(?:\s*\d{4})?)", RegexOptions.None, 2000)]
    private static partial Regex ClosingDate();

    /// <summary>
    /// A phrase that says the posting is shut without a date. These are the literal words boards
    /// use once a role is filled, and they are the strongest signal there is: a page saying "no
    /// longer accepting applications" is not speculating about its own listing.
    /// </summary>
    [GeneratedRegex(@"(?i)\b(this (?:job|position|role|posting|listing|vacancy)\b[^.\n]{0,30}?\b(?:expired|closed|no longer|not available|is full)|no longer (?:accepting|taking|considering|reviewing)\b|applications? (?:are|is|were) (?:now )?closed\b|(?:position|role|job) (?:has been |have been |was |is )?filled\b|we have (?:filled|found|already filled) (?:this|the|our|another) (?:role|position|candidate)\b|(?:this|the) (?:job|position|role|posting) is no longer available\b|applications? closed\b)", RegexOptions.None, 2000)]
    private static partial Regex ClosedNotice();

    /// <summary>Strips a trailing ordinal from a day number: "30th" to "30", and nothing else.</summary>
    [GeneratedRegex(@"\b(\d{1,2})(?:st|nd|rd|th)\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex Ordinal();

    /// <summary>
    /// The "Sept" abbreviation, and only that. The word boundary is the whole point: without it this
    /// rewrites "September" to "Sepember" and every full-month date from September onward stops parsing.
    /// </summary>
    [GeneratedRegex(@"\bSept\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex MonthAbbreviation();

    /// <summary>A posting that states it is shut, whatever date it also carries.</summary>
    public static bool StatesClosed(string? description) =>
        !string.IsNullOrWhiteSpace(description) && ClosedNotice().IsMatch(description);

    /// <summary>
    /// The closing date the posting states, or null when it states none.
    ///
    /// Returned as a UTC ISO string so it can be stored and compared without re-parsing, and null
    /// rather than a guess when the text has no closing date in it.
    /// </summary>
    public static string? Read(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;

        var match = ClosingDate().Match(description);
        if (!match.Success) return null;
        return Parse(match.Groups[1].Value)?.ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Ordinals and trailing punctuation are stripped, then the text is matched against fixed
    /// formats.
    ///
    /// The ordinal has to come off the *number*, and an earlier version did it with a blind
    /// Replace("th", "") over the whole string, which turned "March" into "Marc" and every
    /// month-first date into a miss. Removing the trailing ordinal with a regex anchored to the
    /// digits is the difference between reading a date and corrupting one.
    /// </summary>
    private static DateTime? Parse(string raw)
    {
        var text = Ordinal().Replace(raw, "$1").Trim().TrimEnd(',', '.', ' ');
        if (text.Length == 0) return null;

        // An earlier version rewrote "Sept" to "Sep" with a plain Replace, which turned "September"
        // into "Sepember" and silently lost every full-month date from September on. The abbreviation
        // is normalised with a word boundary instead, and the full names are simply left alone —
        // TryParseExact knows "September" perfectly well already.
        text = MonthAbbreviation().Replace(text, "Sep");
        text = Regex.Replace(text, @"\s{2,}", " ");

        // Both the day-first and month-first orders, with and without a comma before the year,
        // because postings write "3 March 2027" and "March 3, 2027" and both are common.
        var formats = new[]
        {
            "d MMM yyyy", "d MMMM yyyy", "MMM d yyyy", "MMMM d yyyy",
            "MMM d, yyyy", "MMMM d, yyyy", "d MMM, yyyy", "d MMMM, yyyy",
            "yyyy-MM-dd", "d/M/yyyy", "MM/d/yyyy"
        };
        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exact))
            return exact;

        // A bare day and month with no year means the next occurrence, full stop. "Closes 3 March"
        // written in November is next March; written in January it is this March. Rolling forward
        // from the parsed year would need a guess about which is nearer, and guessing here would
        // report a live posting as expired, which is the expensive direction.
        if (DateTime.TryParseExact(text, new[] { "d MMM", "d MMMM", "MMM d", "MMMM d" }, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var partial))
        {
            var now = DateTime.UtcNow;
            var candidate = new DateTime(now.Year, partial.Month, partial.Day, 23, 59, 59, DateTimeKind.Utc);
            return candidate < now ? candidate.AddYears(1) : candidate;
        }

        return null;
    }
}