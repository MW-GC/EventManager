using System.Globalization;
using System.Text;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Shared.Activities;

namespace MW_GC.EventManager.Web.Shared.Events;

/// <summary>
/// The announcement (#97): ready-to-paste Discord text for an Event, built from the Event alone (no clock, no
/// Library, no API). A fixed template:
/// <code>
/// **Event name**
/// &lt;t:UNIX:F&gt;
/// - Game: Activity (players, duration)
/// - Game: Activity 🏆 Winner
/// </code>
/// The date is a Discord timestamp in the full date-and-time style, so Discord shows each reader their own time
/// zone. One line per Selection, in the Event's order, with the players and duration its snapshot carries in the
/// dialog's wording (<see cref="PlayersDurationText"/>); the Winner's line ends with <see cref="WinnerMarker"/>.
/// An Event with no Selections yet (an Occurrence not rolled) says <see cref="PicksToCome"/>. A line for a future
/// link (#9) is reserved after the last line; it is empty in this release, so the text ends on its last line.
/// Names are made safe: Discord markdown characters are backslash-escaped, every "@" is followed by a zero-width
/// space so no mention pings, and a line break in a name becomes a space.
/// </summary>
public static class AnnouncementText
{
    /// <summary>The end of the Winner's line.</summary>
    public const string WinnerMarker = " 🏆 Winner";

    /// <summary>The one line of an Event with no Selections yet.</summary>
    public const string PicksToCome = "The picks are still to come.";

    // The characters Discord reads as markdown (and the backslash that escapes them).
    private const string Markdown = "\\*_~`|><";

    // Written after every "@": Discord then sees no @everyone, @here or <@id> mention.
    private const char ZeroWidthSpace = '\u200B';

    public static string For(EventEntity e)
    {
        var lines = new List<string>
        {
            $"**{Escape(e.Name)}**",
            $"<t:{e.Date.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}:F>"
        };
        if (e.Selections.Count == 0)
            lines.Add(PicksToCome);
        foreach (var selection in e.Selections)
        {
            var line = $"- {Escape(selection.Game.Name)}: {Escape(selection.Activity.Name)}";
            var details = new[]
            {
                PlayersDurationText.Players(selection.Activity.MinPlayers, selection.Activity.MaxPlayers),
                PlayersDurationText.Duration(selection.Activity.DurationMinutes)
            }.OfType<string>().ToList();
            if (details.Count > 0)
                line += $" ({string.Join(", ", details)})";
            if (e.WinnerActivityId is { } winner && winner == selection.Activity.Id)
                line += WinnerMarker;
            lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    // A name as one safe run of text: no line break, no markdown, no mention.
    private static string Escape(string? name)
    {
        var flat = (name ?? string.Empty).Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        var text = new StringBuilder(flat.Length);
        foreach (var c in flat)
        {
            if (Markdown.Contains(c)) text.Append('\\');
            text.Append(c);
            if (c == '@') text.Append(ZeroWidthSpace);
        }
        return text.ToString();
    }
}
