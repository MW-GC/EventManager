using System.Text.RegularExpressions;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Shared.Events;

namespace MW_GC.EventManager.Web.Tests;

// #97: a name in the announcement text can neither break its layout nor ping anyone. Discord markdown characters
// (and the backslash itself) are backslash-escaped; every "@" is followed by a zero-width space, so no mention
// (@everyone, @here, <@id>, <@&id>) survives in the text. A line break in a name becomes a space. Every expected
// string is written out by hand; Zw is the zero-width space.
[TestClass]
public sealed class AnnouncementTextEscapingTests
{
    private const string Zw = "\u200B";
    private static readonly DateTimeOffset Friday = new(2026, 12, 4, 19, 0, 0, TimeSpan.Zero);

    private static string For(string eventName, string game, string activity)
    {
        var gameId = Guid.NewGuid();
        return AnnouncementText.For(new EventEntity
        {
            Name = eventName,
            Date = Friday,
            Selections = [new Selection { Game = new Game { Id = gameId, Name = game }, Activity = new Activity { GameId = gameId, Name = activity } }]
        });
    }

    [TestMethod]
    [DataRow("Star*Night", @"Star\*Night")]
    [DataRow("Snake_case", @"Snake\_case")]
    [DataRow("Wave~~", @"Wave\~\~")]
    [DataRow("Code`s", @"Code\`s")]
    [DataRow("Spoiler||s", @"Spoiler\|\|s")]
    [DataRow("> Quote", @"\> Quote")]
    [DataRow("Less<More", @"Less\<More")]
    [DataRow(@"Back\slash\", @"Back\\slash\\")]
    public void EachMarkdownCharacter_IsEscaped_InEveryName(string name, string escaped)
    {
        Assert.AreEqual(
            $"**{escaped}**\n<t:1796410800:F>\n- {escaped}: {escaped}",
            For(name, name, name));
    }

    // A name that is all markdown keeps the bold title closed: the title still ends with its own "**".
    [TestMethod]
    public void ANameOfStars_DoesNotCloseTheTitleEarly()
    {
        var text = For("**", "Kart", "Grand Prix");

        Assert.AreEqual(@"**\*\***" + "\n<t:1796410800:F>\n- Kart: Grand Prix", text);
    }

    [TestMethod]
    [DataRow("@everyone night", "@" + Zw + "everyone night")]
    [DataRow("Ask @here", "Ask @" + Zw + "here")]
    [DataRow("<@123>", @"\<@" + Zw + @"123\>")]
    [DataRow("<@&456> role", @"\<@" + Zw + @"&456\> role")]
    [DataRow("<@!789>", @"\<@" + Zw + @"!789\>")]
    public void Mentions_AreInert_InEveryName(string name, string inert)
    {
        var text = For(name, name, name);

        Assert.AreEqual($"**{inert}**\n<t:1796410800:F>\n- {inert}: {inert}", text);
        Assert.DoesNotContain("@everyone", text);
        Assert.DoesNotContain("@here", text);
        // Every "@" is followed by the zero-width space, so no "<@" mention and no "@name" survives.
        Assert.IsFalse(Regex.IsMatch(text, "@(?!\u200B)"), text);
    }

    // A line break in a name cannot add a line: the text keeps its title, date and one line per Selection.
    [TestMethod]
    public void ALineBreakInAName_BecomesASpace()
    {
        var text = For("Game\nnight", "Kart\r\n- Fake", "Grand\rPrix");

        Assert.AreEqual("**Game night**\n<t:1796410800:F>\n- Kart - Fake: Grand Prix", text);
        Assert.HasCount(3, text.Split('\n'));
    }

    // The seeded run-step name: both the bold and the mention are made safe at once.
    [TestMethod]
    public void AStarAndEveryone_InOneName()
    {
        Assert.AreEqual(
            @"**\*Party\* @" + Zw + "everyone**\n<t:1796410800:F>\n- Kart: Grand Prix",
            For("*Party* @everyone", "Kart", "Grand Prix"));
    }
}
