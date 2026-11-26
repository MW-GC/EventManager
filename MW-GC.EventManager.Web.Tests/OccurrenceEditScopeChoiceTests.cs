using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #237 S1 to S3, #204 stories 14 to 16: the edit dialog of an Occurrence offers only the scopes the API honours.
// S1: Update Event with nothing changed since the dialog opened (the baseline the Discard question uses, so a Filter
// counts) sends nothing and closes like Cancel. S2: an Occurrence a Series change skips (Modified, with a Winner, or not
// in the future) is saved This event only with no question, and the dialog says why. S3: the first Occurrence of its
// Series is not offered This and following events (the API refuses it and says to use scope all). The fixture's clock
// stands on 1 September 2026, before every Occurrence. Fixture: OccurrenceEditFixture.
[TestClass]
public sealed class OccurrenceEditScopeChoiceTests : OccurrenceEditFixture
{
    private const string SkipsReason = "Series changes skip played, modified and past events.";
    private static readonly string[] EveryScope = ["This event only", "This and following events", "All future events"];

    // ---- S1: nothing changed ---------------------------------------------------------------------

    [TestMethod]
    [DataRow("Weekly raid", "untouched")]
    [DataRow("Weekly raid", "renamed and named back")]
    [DataRow("Empty night", "untouched")]
    public async Task NothingChanged_SendsNoRequest_AndClosesLikeCancel(string row, string how)
    {
        // The unrolled Occurrence seeds its Series' Filters once the read is in; the seeded values are its baseline.
        raidSeries.Filters.PlayersTonight = 4;
        var page = await OpenEdit(row);
        if (row == "Empty night") page.WaitForAssertion(() => Assert.AreEqual("4", Box(page, "Players tonight")));
        if (how != "untouched")
        {
            await Rename(page, "Raid night");
            await Rename(page, row);
        }
        var reads = api.Count(HttpMethod.Get, "/api/events");

        await Update(page);

        Assert.IsFalse(page.HasFormDialog(), "the edit dialog stayed open");
        Assert.IsFalse(page.HasConfirmation(), "closing with nothing changed asked to discard");
        Assert.IsFalse(HasScopeQuestion(page), "nothing changed, yet the scope question was asked");
        Assert.IsEmpty(Sent(), "a save with nothing changed sent a request (the API would mark the Occurrence Modified)");
        Assert.AreEqual(reads, api.Count(HttpMethod.Get, "/api/events"), "the list was read again as after a save");
        Assert.IsEmpty(page.PageAlerts());
        JSInterop.VerifyInvoke("emFocus.restore", 1);
    }

    // A Filter is part of the dialog's baseline: with only a Filter changed the dialog is not clean, so Update Event
    // saves as before (the Event route, no question: the Occurrence's own fields did not change).
    [TestMethod]
    public async Task OnlyAFilterChanged_StillSaves_ThroughTheEventRoute()
    {
        var page = await OpenEdit("Weekly raid");
        await page.FormDialog().QuerySelectorAll("fluent-switch").Single(s => s.GetAttribute("aria-label") == "Themed Activities Only")
            .TriggerEventAsync("onswitchcheckedchange", new CheckboxChangeEventArgs { Checked = true });

        await Update(page);

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        Assert.IsFalse(HasScopeQuestion(page));
        CollectionAssert.AreEqual(new[] { $"PUT {EventPath(raid)}" }, Sent());
    }

    // ---- S2: an Occurrence a Series change skips -------------------------------------------------

    [TestMethod]
    [DataRow("modified")]
    [DataRow("played")]
    [DataRow("past")]
    [DataRow("starting now")]
    public async Task AnOccurrenceTheSeriesSkips_SavesThisEventOnly_WithNoQuestion_AndSaysWhy(string kind)
    {
        if (kind == "modified") raid.Modified = true;
        if (kind == "played") raid.WinnerActivityId = grandPrix.Id;
        if (kind == "past") clock.Now = raid.Date.AddDays(1);
        // The API rewrites only an Occurrence that starts after now.
        if (kind == "starting now") clock.Now = raid.Date;
        var page = await OpenEdit("Weekly raid");
        Assert.AreEqual(SkipsReason, Reason(page));
        await Rename(page, "Raid night");

        await Update(page);

        Assert.IsFalse(HasScopeQuestion(page), $"a {kind} Occurrence was asked the scope question");
        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        CollectionAssert.AreEqual(new[] { $"PUT {EventPath(raid)}" }, Sent());
        Assert.AreEqual(0, api.Count(HttpMethod.Put, SeriesPath(RaidSeries)));
    }

    // A changed date or slot on such an Occurrence is saved the same way: no question with only one answer in it.
    [TestMethod]
    public async Task AModifiedOccurrencesDateChange_IsSavedWithNoQuestion()
    {
        raid.Modified = true;
        var page = await OpenEdit("Weekly raid");
        await TypeDate(page, "10/17/2026");

        await Update(page);

        Assert.IsFalse(HasScopeQuestion(page));
        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        CollectionAssert.AreEqual(new[] { $"PUT {EventPath(raid)}" }, Sent());
    }

    // A future Occurrence, not Modified and with no Winner, shows no reason and is offered every scope; so is one that
    // starts a second after now. A standalone Event shows no reason either.
    [TestMethod]
    [DataRow("Weekly raid", false)]
    [DataRow("Weekly raid", true)]
    [DataRow("Quiz night", false)]
    public async Task AnOccurrenceTheSeriesRewrites_ShowsNoReason_AndIsOfferedEveryScope(string row, bool aSecondAhead)
    {
        if (aSecondAhead) clock.Now = raid.Date.AddSeconds(-1);
        var page = await OpenEdit(row);
        Assert.IsNull(page.FormDialog().QuerySelector(".series-skips-reason"), "a reason is shown for an event the series rewrites");
        if (row == "Quiz night") return;
        await Rename(page, "Raid night");

        await Update(page);

        CollectionAssert.AreEqual(EveryScope, Labels(page));
    }

    // ---- S3: the first Occurrence -----------------------------------------------------------------

    // The Raid Series starts on the Occurrence's own key (at another offset, the same instant): This and following
    // events is left out, and All future events still saves with scope all. An Occurrence a week after the start keeps it.
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task TheFirstOccurrence_IsNotOfferedThisAndFollowing_ALaterOneIs(bool first)
    {
        var start = first ? raid.OccurrenceKey!.Value.ToUniversalTime() : raid.OccurrenceKey!.Value.AddDays(-7);
        raidSeries.Schedule = raidSeries.Schedule with { First = start.ToOffset(first ? TimeSpan.Zero : RaidOffset) };
        var page = await OpenEdit("Weekly raid");
        await Rename(page, "Raid night");

        await Update(page);

        CollectionAssert.AreEqual(first ? new[] { "This event only", "All future events" } : EveryScope, Labels(page));
        await ChooseAndConfirm(page, "All future events");
        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        CollectionAssert.AreEqual(new[] { $"PUT {SeriesPath(RaidSeries)}?scope=all" }, Sent());
    }

    // Only the Series read can tell the first Occurrence, so a question asked before it is in waits for it.
    [TestMethod]
    public async Task AQuestionAskedBeforeTheSeriesReadIsIn_WaitsForIt_AndLeavesOutThisAndFollowingForTheFirst()
    {
        raidSeries.Schedule = raidSeries.Schedule with { First = raid.OccurrenceKey!.Value };
        var gate = api.Hold(HttpMethod.Get, SeriesPath(RaidSeries), () => Stored(raidSeries, RaidTag));
        var page = await OpenEdit("Weekly raid");
        await Rename(page, "Raid night");

        var update = Update(page);
        Assert.IsFalse(HasScopeQuestion(page), "the question opened before the Series read was in");
        gate.SetResult();
        await update;

        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "This event only", "All future events" }, Labels(page)));
        Assert.IsEmpty(Sent());
    }

    // #251: Update Event pressed again while the first press waits for the Series read does nothing. The question opens
    // once (one emFocus.question, none answered: a second question would replace the first) and one save is sent.
    [TestMethod]
    public async Task UpdatePressedAgainWhileTheSeriesReadIsPending_AsksOnce_AndSavesOnce()
    {
        var gate = api.Hold(HttpMethod.Get, SeriesPath(RaidSeries), () => Stored(raidSeries, RaidTag));
        var page = await OpenEdit("Weekly raid");
        await Rename(page, "Raid night");

        var first = Update(page);
        var second = page.InvokeAsync(() => Update(page));
        gate.SetResult();
        await first;
        await second;

        page.WaitForAssertion(() => CollectionAssert.AreEqual(EveryScope, Labels(page)));
        JSInterop.VerifyInvoke("emFocus.question", 1);
        JSInterop.VerifyNotInvoke("emFocus.answered");
        Assert.IsEmpty(Sent());
        await ChooseAndConfirm(page, "All future events");
        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        CollectionAssert.AreEqual(new[] { $"PUT {SeriesPath(RaidSeries)}?scope=all" }, Sent());
    }

    // #251: a dialog closed (Cancel), closed and opened again, or showing the Discard question (Escape) while Update
    // Event waits for the Series read gets no scope question once the read is in, and nothing is saved (opened again
    // reads the Series once more for its own session). The Discard question is the one question focus.js is told of.
    [TestMethod]
    [DataRow("closed")]
    [DataRow("opened again")]
    [DataRow("discard question")]
    public async Task ADialogClosedOrAskingToDiscard_WhileTheSeriesReadIsPending_GetsNoScopeQuestion(string how)
    {
        var gate = api.Hold(HttpMethod.Get, SeriesPath(RaidSeries), () => Stored(raidSeries, RaidTag));
        var page = await OpenEdit("Weekly raid");
        await Rename(page, "Raid night");
        var update = Update(page);

        // Each element is found and acted on in one turn of the renderer: the pending save may draw the dialog again
        // between a lookup and its event otherwise.
        if (how == "discard question") await page.InvokeAsync(() => Dismiss(page));
        else await page.InvokeAsync(() => page.FormDialog().Button("Cancel").ClickAsync(new()));
        if (how == "opened again") await page.InvokeAsync(() => RowButton(page, "Weekly raid", "Edit").ClickAsync(new()));
        gate.SetResult();
        await update;
        await Task.Run(() => page.InvokeAsync(() => { }));

        Assert.IsFalse(HasScopeQuestion(page), $"a scope question opened over a dialog {how} while the Series read ran");
        Assert.AreEqual(how != "closed", page.HasFormDialog());
        Assert.AreEqual(how == "discard question", page.HasConfirmation());
        if (how == "discard question") JSInterop.VerifyInvoke("emFocus.question", 1);
        else JSInterop.VerifyNotInvoke("emFocus.question");
        CollectionAssert.AreEqual(how == "opened again" ? new[] { $"GET {SeriesPath(RaidSeries)}" } : Array.Empty<string>(), Sent());
    }

    // A failed read cannot tell the first Occurrence: every scope is offered, and a series scope then says the read failed.
    [TestMethod]
    public async Task AFailedSeriesRead_OffersEveryScope()
    {
        api.Status(HttpMethod.Get, SeriesPath(RaidSeries), HttpStatusCode.InternalServerError);
        var page = await OpenEdit("Weekly raid");
        await Rename(page, "Raid night");

        await Update(page);

        CollectionAssert.AreEqual(EveryScope, Labels(page));
    }

    private static string? Reason(IRenderedComponent<Events> page) =>
        page.FormDialog().QuerySelector(".series-skips-reason")?.TextContent.Trim();
}
