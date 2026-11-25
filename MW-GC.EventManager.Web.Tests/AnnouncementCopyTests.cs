using AngleSharp.Dom;
using Bunit;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #97: the Copy announcement button of the Event details dialog, over a fake clipboard that answers each
// navigator.clipboard.writeText call as the test says (copied, refused, gone, or held until released). Every
// expected text is written out by hand; 2026-12-04 19:00 UTC is UNIX second 1796410800.
[TestClass]
public sealed class AnnouncementCopyTests : IDisposable
{
    private const string Copied = "Announcement copied.";
    private const string Refused = "The announcement could not be copied: the browser refused the clipboard.";
    private const string GameNightText =
        "**Game night**\n" +
        "<t:1796410800:F>\n" +
        "- Kart: Grand Prix (2-6 players, 45 min)\n" +
        "- Brawl: Stock Battle 🏆 Winner\n" +
        "- Puzzle: Tiles";

    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly FakeClipboard clipboard = new();
    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly GameEntity brawl = new() { Name = "Brawl" };
    private readonly GameEntity puzzle = new() { Name = "Puzzle" };
    private readonly ActivityEntity grandPrix;
    private readonly ActivityEntity stock;
    private readonly ActivityEntity tiles;
    // Three Selections, the second the Winner, the first with players and duration in its snapshot.
    private readonly EventEntity night;
    // An Occurrence not rolled yet: no Selections.
    private readonly EventEntity weekly;

    public AnnouncementCopyTests()
    {
        grandPrix = new ActivityEntity { Name = "Grand Prix", GameId = kart.Id, MinPlayers = 2, MaxPlayers = 6, DurationMinutes = 45 };
        stock = new ActivityEntity { Name = "Stock Battle", GameId = brawl.Id };
        tiles = new ActivityEntity { Name = "Tiles", GameId = puzzle.Id };
        night = new EventEntity
        {
            Name = "Game night",
            Date = new DateTimeOffset(2026, 12, 4, 19, 0, 0, TimeSpan.Zero),
            Selections =
            [
                new Selection
                {
                    Game = new Game { Id = kart.Id, Name = "Kart" },
                    Activity = new Activity { Id = grandPrix.Id, GameId = kart.Id, Name = "Grand Prix", MinPlayers = 2, MaxPlayers = 6, DurationMinutes = 45 }
                },
                Pick(brawl, stock),
                Pick(puzzle, tiles),
            ],
            WinnerActivityId = stock.Id
        };
        weekly = new EventEntity { Name = "Weekly night", Date = new DateTimeOffset(2026, 12, 11, 20, 30, 0, TimeSpan.Zero), SeriesId = Guid.NewGuid() };
        api.Json(HttpMethod.Get, "/api/events", new[] { night, weekly })
           .Json(HttpMethod.Get, "/api/games", new[] { kart, brawl, puzzle })
           .Json(HttpMethod.Get, "/api/activities", new[] { grandPrix, stock, tiles })
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>());
        harness = new PageHarness(api);
        harness.JSInterop.AddInvocationHandler(clipboard);
    }

    public void Dispose() => harness.Dispose();

    [TestMethod]
    public async Task Pressing_CopiesTheExactText_AndSaysSo_WithNoApiCall()
    {
        var page = await OpenDetails("Game night");
        var requests = api.Requests.Count;

        await CopyButton(page).ClickAsync(new());

        Assert.AreEqual(GameNightText, Assert.ContainsSingle(clipboard.Written));
        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { Copied }, Statuses(page)));
        Assert.IsEmpty(Alerts(page));
        Assert.AreEqual(requests, api.Requests.Count, "copying asks the API nothing");
    }

    [TestMethod]
    public async Task AnEventWithNoSelections_CopiesThatThePicksAreStillToCome()
    {
        var page = await OpenDetails("Weekly night");

        await CopyButton(page).ClickAsync(new());

        Assert.AreEqual("**Weekly night**\n<t:1797021000:F>\nThe picks are still to come.", Assert.ContainsSingle(clipboard.Written));
        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { Copied }, Statuses(page)));
    }

    [TestMethod]
    [DataRow(Answer.Refused)]
    [DataRow(Answer.Gone)]
    public async Task ARefusedCopy_ShowsTheAlert_AndNoStatus(Answer answer)
    {
        var page = await OpenDetails("Game night");
        clipboard.Next = answer;

        await CopyButton(page).ClickAsync(new());

        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { Refused }, Alerts(page)));
        Assert.IsEmpty(Statuses(page));
        Assert.IsTrue(page.HasFormDialog());
    }

    // Each press clears the last message before it copies: while the new copy is still running, neither shows.
    [TestMethod]
    public async Task PressingAgain_ClearsTheOldMessage()
    {
        var page = await OpenDetails("Game night");
        clipboard.Next = Answer.Refused;
        await CopyButton(page).ClickAsync(new());
        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { Refused }, Alerts(page)));

        clipboard.Next = Answer.Held;
        var second = CopyButton(page).ClickAsync(new());
        page.WaitForAssertion(() => Assert.HasCount(2, clipboard.Written));
        page.WaitForAssertion(() => Assert.IsEmpty(Alerts(page)));
        Assert.IsEmpty(Statuses(page));

        clipboard.Release();
        await second;
        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { Copied }, Statuses(page)));
        Assert.IsEmpty(Alerts(page));

        clipboard.Next = Answer.Held;
        var third = CopyButton(page).ClickAsync(new());
        page.WaitForAssertion(() => Assert.HasCount(3, clipboard.Written));
        page.WaitForAssertion(() => Assert.IsEmpty(Statuses(page)));
        clipboard.Release();
        await third;
    }

    [TestMethod]
    [DataRow(Answer.Copied)]
    [DataRow(Answer.Refused)]
    public async Task OpeningAnotherEvent_ClearsTheMessage(Answer answer)
    {
        var page = await OpenDetails("Game night");
        clipboard.Next = answer;
        await CopyButton(page).ClickAsync(new());
        page.WaitForAssertion(() => Assert.HasCount(1, Statuses(page).Concat(Alerts(page))));

        await page.FormDialog().Button("Close").ClickAsync(new());
        await RowButton(page, "Weekly night", "Details").ClickAsync(new());

        Assert.AreEqual("Weekly night", page.DialogTitle());
        Assert.IsEmpty(Statuses(page));
        Assert.IsEmpty(Alerts(page));
        // And its button copies its own Event, not the one shown before.
        await CopyButton(page).ClickAsync(new());
        Assert.AreEqual("**Weekly night**\n<t:1797021000:F>\nThe picks are still to come.", clipboard.Written.Last());
    }

    // A copy still running when its dialog closed and another Event's opened never reports on the newer dialog.
    [TestMethod]
    [DataRow(Answer.Copied)]
    [DataRow(Answer.Refused)]
    public async Task ALateResult_IsDropped(Answer late)
    {
        var page = await OpenDetails("Game night");
        clipboard.Next = Answer.Held;
        var press = CopyButton(page).ClickAsync(new());
        page.WaitForAssertion(() => Assert.HasCount(1, clipboard.Written));

        await page.FormDialog().Button("Close").ClickAsync(new());
        await RowButton(page, "Weekly night", "Details").ClickAsync(new());
        clipboard.Release(late);
        await press;

        Assert.AreEqual("Weekly night", page.DialogTitle());
        Assert.IsEmpty(Statuses(page));
        Assert.IsEmpty(Alerts(page));
    }

    // A second press while the first copy is still running is ignored, so the two results can never show together.
    [TestMethod]
    public async Task ASecondPress_WhileTheFirstRuns_IsIgnored()
    {
        var page = await OpenDetails("Game night");
        clipboard.Next = Answer.Held;
        var first = CopyButton(page).ClickAsync(new());
        page.WaitForAssertion(() => Assert.HasCount(1, clipboard.Written));

        await CopyButton(page).ClickAsync(new());

        Assert.HasCount(1, clipboard.Written, "the second press copies nothing");
        Assert.IsEmpty(Statuses(page));
        clipboard.Release(Answer.Refused);
        await first;
        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { Refused }, Alerts(page)));
        Assert.IsEmpty(Statuses(page));

        // Once the first copy is done, the button copies again.
        await CopyButton(page).ClickAsync(new());
        Assert.HasCount(2, clipboard.Written);
        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { Copied }, Statuses(page)));
        Assert.IsEmpty(Alerts(page));
    }

    // A copy still running in a closed dialog never holds the button of the Event opened next, and when it ends it
    // does not free that dialog's own running copy for a second press.
    [TestMethod]
    public async Task APendingCopy_DoesNotHoldTheNextEventsButton()
    {
        var page = await OpenDetails("Game night");
        clipboard.Next = Answer.Held;
        var old = CopyButton(page).ClickAsync(new());
        page.WaitForAssertion(() => Assert.HasCount(1, clipboard.Written));
        await page.FormDialog().Button("Close").ClickAsync(new());
        await RowButton(page, "Weekly night", "Details").ClickAsync(new());

        clipboard.Next = Answer.Held;
        var press = CopyButton(page).ClickAsync(new());
        page.WaitForAssertion(() => Assert.HasCount(2, clipboard.Written));
        Assert.AreEqual("**Weekly night**\n<t:1797021000:F>\nThe picks are still to come.", clipboard.Written[1]);
        clipboard.Release(Answer.Refused);
        await old;
        await CopyButton(page).ClickAsync(new());

        Assert.HasCount(2, clipboard.Written, "the Weekly night copy still runs, so this press copies nothing");
        clipboard.Release();
        await press;
        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { Copied }, Statuses(page)));
        Assert.IsEmpty(Alerts(page));
    }

    // The text is the Event as the dialog holds it now: after Select Winner, the marker is on the new Winner.
    [TestMethod]
    public async Task TheText_FollowsTheDialogsEvent_AfterSelectWinner()
    {
        var page = await OpenDetails("Game night");
        await CopyButton(page).ClickAsync(new());
        Assert.AreEqual(GameNightText, Assert.ContainsSingle(clipboard.Written));
        clipboard.Written.Clear();
        var rewon = new EventEntity { Id = night.Id, Name = night.Name, Date = night.Date, Selections = night.Selections, WinnerActivityId = tiles.Id };
        api.Json(HttpMethod.Put, $"/api/events/{night.Id}", rewon)
           .Json(HttpMethod.Get, "/api/events", new[] { rewon, weekly });
        await Card(page, "Tiles").Button("Select Winner").ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsNotNull(Card(page, "Stock Battle").QuerySelector("fluent-button")));

        await CopyButton(page).ClickAsync(new());

        Assert.AreEqual(
            "**Game night**\n<t:1796410800:F>\n- Kart: Grand Prix (2-6 players, 45 min)\n- Brawl: Stock Battle\n- Puzzle: Tiles 🏆 Winner",
            Assert.ContainsSingle(clipboard.Written));
    }

    // Copying changes nothing, so a running Select Winner (Busy) does not disable it.
    [TestMethod]
    public async Task WhileBusy_TheButtonStillCopies()
    {
        var page = await OpenDetails("Game night");
        var gate = api.Hold(HttpMethod.Put, $"/api/events/{night.Id}", () => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = System.Net.Http.Json.JsonContent.Create(night)
        });
        var select = Card(page, "Tiles").Button("Select Winner").ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsTrue(page.FormDialog().Button("Close").IsBusy()));

        Assert.IsFalse(CopyButton(page).IsBusy());
        await CopyButton(page).ClickAsync(new());
        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { Copied }, Statuses(page)));
        Assert.HasCount(1, clipboard.Written);

        gate.SetResult();
        await select;
    }

    [TestMethod]
    public async Task TheButton_IsNamedInItsOwnWords()
    {
        var page = await OpenDetails("Game night");

        var button = CopyButton(page);

        Assert.AreEqual("Copy announcement", Normalize(button.TextContent));
        Assert.IsFalse(button.HasAttribute("aria-label"), "the visible words are the name");
        Assert.IsFalse(button.HasAttribute("aria-labelledby"));
        Assert.IsFalse(button.HasAttribute("title"));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<IRenderedComponent<Events>> OpenDetails(string name)
    {
        var page = harness.Render<Events>();
        page.WaitForRows(2);
        await RowButton(page, name, "Details").ClickAsync(new());
        Assert.AreEqual(name, page.DialogTitle());
        return page;
    }

    private static IElement CopyButton(IRenderedComponent<Events> page) => page.FormDialog().Button("Copy announcement");

    // The messages beside the button (the row it shares with Copy feedback link).
    private static List<string> Statuses(IRenderedComponent<Events> page) =>
        CopyButton(page).ParentElement!.QuerySelectorAll("[role=status]").Select(s => s.TextContent.Trim()).ToList();

    private static List<string> Alerts(IRenderedComponent<Events> page) =>
        CopyButton(page).ParentElement!.QuerySelectorAll("[role=alert]").Select(s => s.TextContent.Trim()).ToList();

    public enum Answer { Copied, Refused, Gone, Held }

    // navigator.clipboard.writeText as the browser answers it: records each text, then copies, refuses
    // (JSException), is gone (JSDisconnectedException), or holds the call until Release (oldest held call first).
    private sealed class FakeClipboard() : JSRuntimeInvocationHandlerBase<IJSVoidResult>(
        i => i.Identifier == "navigator.clipboard.writeText", isCatchAllHandler: false)
    {
        private readonly Queue<TaskCompletionSource<IJSVoidResult>> _held = new();

        public List<string> Written { get; } = [];

        public Answer Next { get; set; } = Answer.Copied;

        public override bool IsVoidResultHandler => true;

        public void Release(Answer answer = Answer.Copied)
        {
            var held = _held.Dequeue();
            if (answer == Answer.Copied) held.SetResult(null!);
            else held.SetException(Failure(answer));
        }

        protected override Task<IJSVoidResult> HandleAsync(JSRuntimeInvocation invocation)
        {
            Written.Add((string)invocation.Arguments[0]!);
            var answer = Next;
            Next = Answer.Copied;
            switch (answer)
            {
                case Answer.Held:
                    var held = new TaskCompletionSource<IJSVoidResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _held.Enqueue(held);
                    return held.Task;
                case Answer.Copied:
                    return Task.FromResult<IJSVoidResult>(null!);
                default:
                    return Task.FromException<IJSVoidResult>(Failure(answer));
            }
        }

        private static Exception Failure(Answer answer) => answer == Answer.Gone
            ? new JSDisconnectedException("gone")
            : new JSException("NotAllowedError: Write permission denied.");
    }
}
