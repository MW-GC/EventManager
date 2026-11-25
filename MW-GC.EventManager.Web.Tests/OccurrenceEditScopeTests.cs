using System.Text.Json;
using Bunit;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #100, #204 (Web part 2b, stories 12 to 15 and 21): Update Event on an Occurrence asks which events take the change.
// "This event only" is the Event route (PUT api/events/{id}); "This and following events" and "All future events" read
// the Series (GET api/series/{id}) and send it back with only the name, the time of day and Unique games only changed
// (PUT api/series/{id}?scope=following&occurrence={eventId} or ?scope=all), under the eTag read. A standalone Event,
// and an Occurrence with nothing changed, save as before with no question. Fixture: OccurrenceEditFixture.
[TestClass]
public sealed class OccurrenceEditScopeTests : OccurrenceEditFixture
{
    private const string Reason = "A changed date or changed activities apply to this event only.";

    [TestMethod]
    public async Task AStandaloneEventsUpdate_SendsTheEventPut_WithNoQuestion_AndNoSeriesCall()
    {
        var page = await OpenEdit("Quiz night");
        await Rename(page, "Quiz finals");

        await Update(page);

        Assert.IsFalse(HasScopeQuestion(page), "a standalone Event was asked the scope question");
        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        CollectionAssert.AreEqual(new[] { $"PUT {EventPath(quiz)}" }, Sent());
        Assert.AreEqual("Quiz finals", SentEvent(quiz).Name);
    }

    [TestMethod]
    public async Task AnOccurrencesNameChange_AsksTheScope_WithThreeOptions_NothingChosen_AndConfirmDisabled()
    {
        var page = await OpenEdit("Weekly raid");
        await Rename(page, "Raid night");

        await Update(page);

        Assert.IsTrue(HasScopeQuestion(page), "an Occurrence's change asked no scope question");
        Assert.IsTrue(page.HasFormDialog(), "the edit dialog closed under the question");
        Assert.AreEqual("Update repeating event", ScopeQuestion(page).GetAttribute("aria-label"));
        Assert.AreEqual("What to update", ScopeQuestion(page).QuerySelector("fluent-radio-group")!.GetAttribute("aria-label"));
        CollectionAssert.AreEqual(new[] { "This event only", "This and following events", "All future events" }, Labels(page));
        CollectionAssert.AreEqual(new[]
        {
            "Only this event changes. Later changes to the series will not touch it.",
            "This event and every later one that has not been played or edited by hand take the change. Earlier events stay as they are.",
            "Every future event of the series that has not been played or edited by hand takes the change."
        }, ScopeQuestion(page).QuerySelectorAll(".scope-description").Select(d => d.TextContent.Trim()).ToList());
        StringAssert.StartsWith(ScopeQuestion(page).QuerySelector(".confirm-message")!.TextContent.Trim(), "Weekly raid on ");
        Assert.IsTrue(ConfirmButton(page).HasAttribute("disabled"), "Confirm is enabled before a scope is chosen");
        Assert.IsFalse(ScopeQuestion(page).QuerySelectorAll("fluent-radio").Any(r => r.HasAttribute("checked")), "a scope is chosen already");
        Assert.IsEmpty(Sent());
    }

    [TestMethod]
    public async Task ConfirmNamesTheChosenScope_AndChangesWithTheChoice_InTheNormalAccent()
    {
        var page = await OpenEdit("Weekly raid");
        await Rename(page, "Raid night");
        await Update(page);

        foreach (var (label, text) in new[] { ("All future events", "Update all future events"), ("This event only", "Update this event"), ("This and following events", "Update this and following") })
        {
            await Choose(page, label);
            Assert.AreEqual(text, ConfirmButton(page).TextContent.Trim());
            Assert.IsFalse(ConfirmButton(page).HasAttribute("disabled"));
            Assert.IsNull(ConfirmButton(page).GetAttribute("style"), "an edit scope is painted as a destructive answer");
        }
        Assert.IsEmpty(Sent());
    }

    [TestMethod]
    public async Task ThisEventOnly_SendsExactlyTheEventPut_AndNoSeriesCall()
    {
        var page = await OpenEdit("Weekly raid");
        await Rename(page, "Raid night");
        await Update(page);

        await ChooseAndConfirm(page, "This event only");

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        CollectionAssert.AreEqual(new[] { $"PUT {EventPath(raid)}" }, Sent());
        var sent = SentEvent(raid);
        Assert.AreEqual(("Raid night", raid.Date), (sent.Name, sent.Date));
        Assert.AreEqual(0, api.Count(HttpMethod.Put, SeriesPath(RaidSeries)));
    }

    [TestMethod]
    public async Task ThisAndFollowing_ReadsTheSeries_ThenSendsItBackFromTheOccurrence_UnderTheETagRead()
    {
        var page = await OpenEdit("Weekly raid");
        await Rename(page, "Raid night");
        await Update(page);

        await ChooseAndConfirm(page, "This and following events");

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        CollectionAssert.AreEqual(new[] { $"GET {SeriesPath(RaidSeries)}" }, OpenReads());
        CollectionAssert.AreEqual(new[] { $"PUT {SeriesPath(RaidSeries)}?scope=following&occurrence={raid.Id:D}" }, Sent());
        CollectionAssert.AreEqual(new[] { RaidTag }, api.IfMatches(HttpMethod.Put, SeriesPath(RaidSeries)));
        // The new Series starts on the Occurrence's own day at the Series' offset, at the Series' unchanged time of day.
        AssertStoredBut(SentSeries(RaidSeries), "Raid night", new DateTimeOffset(2026, 10, 15, 19, 0, 0, RaidOffset), uniqueGames: true);
        StringAssert.Contains(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, SeriesPath(RaidSeries))), "\"first\":\"2026-10-15T19:00:00-04:00\"");
    }

    [TestMethod]
    public async Task AllFutureEvents_ReadsTheSeries_ThenSendsItBackWithScopeAll_UnderTheETagRead()
    {
        var page = await OpenEdit("Weekly raid");
        await Rename(page, "Raid night");
        await Update(page);

        await ChooseAndConfirm(page, "All future events");

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        CollectionAssert.AreEqual(new[] { $"GET {SeriesPath(RaidSeries)}" }, OpenReads());
        CollectionAssert.AreEqual(new[] { $"PUT {SeriesPath(RaidSeries)}?scope=all" }, Sent());
        CollectionAssert.AreEqual(new[] { RaidTag }, api.IfMatches(HttpMethod.Put, SeriesPath(RaidSeries)));
        AssertStoredBut(SentSeries(RaidSeries), "Raid night", raidSeries.Schedule.First, uniqueGames: true);
    }

    // Two Occurrences of two Series: the Series id, the Occurrence id and the eTag all come from the row clicked.
    [TestMethod]
    [DataRow("Weekly raid", "This and following events")]
    [DataRow("Weekly raid", "All future events")]
    [DataRow("Monthly cup", "This and following events")]
    [DataRow("Monthly cup", "All future events")]
    public async Task TheIdsAndTheETag_ComeFromTheRowClicked(string row, string option)
    {
        var (occurrence, series, tag, other) = row == "Weekly raid" ? (raid, RaidSeries, RaidTag, CupSeries) : (cup, CupSeries, CupTag, RaidSeries);
        var page = await OpenEdit(row);
        await Rename(page, "Renamed");
        await Update(page);

        await ChooseAndConfirm(page, option);

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        var query = option == "All future events" ? "scope=all" : $"scope=following&occurrence={occurrence.Id:D}";
        CollectionAssert.AreEqual(new[] { $"GET {SeriesPath(series)}" }, OpenReads());
        CollectionAssert.AreEqual(new[] { $"PUT {SeriesPath(series)}?{query}" }, Sent());
        CollectionAssert.AreEqual(new[] { tag }, api.IfMatches(HttpMethod.Put, SeriesPath(series)));
        Assert.AreEqual(series, SentSeries(series).Id);
        Assert.AreEqual(0, api.Count(HttpMethod.Put, SeriesPath(other)));
    }

    // 05:45 in the browser's Kathmandu on 16 October is 20:00 on 15 October at the Series' UTC-04:00: the Series takes
    // 20:00 at its own offset (05:45 would be the browser's time, 00:00 UTC's), on the day it already had.
    [TestMethod]
    [DataRow("All future events", "2026-09-24T20:00:00-04:00")]
    [DataRow("This and following events", "2026-10-15T20:00:00-04:00")]
    public async Task ATimeChange_IsTheSeriesTimeOfDay_AtTheSeriesOwnOffset(string option, string first)
    {
        var page = await OpenEdit("Weekly raid");
        await TypeTime(page, "05:45");
        await Update(page);
        CollectionAssert.AreEqual(new[] { "This event only", "This and following events", "All future events" }, Labels(page));

        await ChooseAndConfirm(page, option);

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        AssertStoredBut(SentSeries(RaidSeries), "Weekly raid", DateTimeOffset.Parse(first), uniqueGames: true);
        StringAssert.Contains(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, SeriesPath(RaidSeries))), $"\"first\":\"{first}\"");
    }

    [TestMethod]
    public async Task AUniqueGamesChange_OffersEveryScope_AndSendsItToTheSeries()
    {
        var page = await OpenEdit("Weekly raid");
        await SwitchUniqueGames(page, false);
        await Update(page);
        CollectionAssert.AreEqual(new[] { "This event only", "This and following events", "All future events" }, Labels(page));

        await ChooseAndConfirm(page, "All future events");

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        AssertStoredBut(SentSeries(RaidSeries), "Weekly raid", raidSeries.Schedule.First, uniqueGames: false);
    }

    // A changed day or changed activities belong to the Occurrence: only "This event only" is offered, with the reason,
    // and nothing is chosen for the admin. The Event route then saves it.
    [TestMethod]
    [DataRow("date")]
    [DataRow("removed slot")]
    [DataRow("replaced activity")]
    [DataRow("name and date")]
    public async Task ADateOrSlotChange_OffersOnlyThisEvent_WithTheReason(string change)
    {
        var page = await OpenEdit("Weekly raid");
        if (change.Contains("name")) await Rename(page, "Raid night");
        if (change.Contains("date")) await TypeDate(page, "10/17/2026");
        if (change == "removed slot") await page.FormDialog().QuerySelector("fluent-button[aria-label='Remove slot 2']")!.ClickAsync(new());
        if (change == "replaced activity") await ReplaceSecondSlotsActivity(page);

        await Update(page);

        Assert.IsTrue(HasScopeQuestion(page), $"a {change} asked no scope question");
        CollectionAssert.AreEqual(new[] { "This event only" }, Labels(page));
        Assert.AreEqual(Reason, ScopeQuestion(page).QuerySelector(".confirm-message")!.TextContent.Trim());
        Assert.IsTrue(ConfirmButton(page).HasAttribute("disabled"), "This event only is chosen already");
        await ChooseAndConfirm(page, "This event only");
        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        CollectionAssert.AreEqual(new[] { $"PUT {EventPath(raid)}" }, Sent());
    }

    [TestMethod]
    public async Task NoChange_AsksNothing_AndSavesThroughTheEventRouteAsBefore()
    {
        var page = await OpenEdit("Weekly raid");

        await Update(page);

        Assert.IsFalse(HasScopeQuestion(page), "an unchanged Occurrence asked the scope question");
        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        CollectionAssert.AreEqual(new[] { $"PUT {EventPath(raid)}" }, Sent());
    }

    // An Occurrence not rolled yet (no Selections) renamed: the question is asked, and each scope saves with no slots,
    // the Event route included (the API takes an Occurrence with none). Unchanged, it keeps the 1 to 5 message as before.
    [TestMethod]
    [DataRow("This event only")]
    [DataRow("This and following events")]
    [DataRow("All future events")]
    public async Task AnUnrolledOccurrencesRename_IsAsked_AndSavesWithNoSlots(string option)
    {
        var page = await OpenEdit("Empty night");
        await Rename(page, "Raid night");
        await Update(page);
        CollectionAssert.AreEqual(new[] { "This event only", "This and following events", "All future events" }, Labels(page));

        await ChooseAndConfirm(page, option);

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        if (option == "This event only")
        {
            CollectionAssert.AreEqual(new[] { $"PUT {EventPath(empty)}" }, Sent());
            Assert.IsEmpty(SentEvent(empty).Selections);
        }
        else CollectionAssert.AreEqual(new[] { $"PUT {SeriesPath(RaidSeries)}?{(option == "All future events" ? "scope=all" : $"scope=following&occurrence={empty.Id:D}")}" }, Sent());
        CollectionAssert.AreEqual(new[] { $"GET {SeriesPath(RaidSeries)}" }, OpenReads());
    }

    [TestMethod]
    public async Task AnUnchangedUnrolledOccurrence_KeepsTheOneToFiveMessage()
    {
        var page = await OpenEdit("Empty night");

        await Update(page);

        Assert.IsFalse(HasScopeQuestion(page));
        StringAssert.StartsWith(Assert.ContainsSingle(EditDialog(page).Alerts()), "Select 1–5 activities before saving.");
        Assert.IsEmpty(Sent());
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private EventEntity SentEvent(EventEntity e) =>
        JsonSerializer.Deserialize<EventEntity>(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, EventPath(e)))!, JsonSerializerOptions.Web)!;

    // The sent Series is the stored one but for the name, the start and Unique games only.
    private void AssertStoredBut(SeriesEntity sent, string name, DateTimeOffset first, bool uniqueGames)
    {
        Assert.AreEqual((name, uniqueGames), (sent.Name, sent.UniqueGamesOnly));
        Assert.AreEqual(first, sent.Schedule.First);
        Assert.AreEqual(RaidOffset, sent.Schedule.First.Offset, "the start left the Series' own offset");
        Assert.AreEqual(raidSeries.Schedule.Rule, sent.Schedule.Rule);
        Assert.AreEqual(raidSeries.Schedule.End, sent.Schedule.End);
        Assert.AreEqual(raidSeries.SlotCount, sent.SlotCount);
        Assert.AreEqual(JsonSerializer.Serialize(raidSeries.Filters), JsonSerializer.Serialize(sent.Filters));
        CollectionAssert.AreEqual(raidSeries.Skipped, sent.Skipped);
        Assert.AreEqual((raidSeries.SplitAt, raidSeries.SplitFrom), (sent.SplitAt, sent.SplitFrom));
    }

    // Slot 2's Activity (Brawl's Stock) is replaced with Brawl's Royale in its select: a replaced activity.
    private async Task ReplaceSecondSlotsActivity(IRenderedComponent<Events> page)
    {
        await page.FormDialog().QuerySelector("fluent-select[aria-label='Activity for slot 2']")!
            .QuerySelectorAll("fluent-option").Single(o => o.GetAttribute("value") == royale.Id.ToString()).ClickAsync(new());
        Assert.IsTrue(page.FormDialog().QuerySelector("fluent-select[aria-label='Activity for slot 2']")!.TextContent.Contains("Royale"));
    }
}
