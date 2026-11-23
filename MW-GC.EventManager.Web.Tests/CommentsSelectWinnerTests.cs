using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using static MW_GC.EventManager.Web.Tests.CommentsPanelView;

namespace MW_GC.EventManager.Web.Tests;

// #132, #186 item 8, #195 item 5: Select Winner in the Event details dialog and the Winner's Comments panel. The
// panel follows the Winner; Select Winner waits while a request runs, asks before dropping unsaved Comment text,
// and its Discard waits for a running request instead of doing nothing.
[TestClass]
public sealed class CommentsSelectWinnerTests : CommentsFromEventDetailsFixture
{
    [TestMethod]
    public async Task SelectWinner_OnANoWinnerEvent_ShowsTheEntry_AndLoadsThatActivitysComments()
    {
        comments[timeTrial.Id].Add(Comment("Crowd favourite", null, TagB));
        var page = await OpenEventDetails("Open night");
        var won = Rewon(open, timeTrial);
        api.Json(HttpMethod.Put, $"/api/events/{open.Id}", won)
           .Json(HttpMethod.Get, "/api/events", new[] { night, won });

        await Card(page, "Time Trial").Button("Select Winner").ClickAsync(new());

        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "Crowd favourite" }, Texts(page)));
        var panel = Assert.ContainsSingle(Panels(page));
        Assert.AreSame(Card(page, "Time Trial"), panel.Closest("fluent-card"));
        Assert.AreEqual(1, api.Count(HttpMethod.Get, ListPath(timeTrial.Id)));
        Assert.AreEqual(0, api.Count(HttpMethod.Get, ListPath(grandPrix.Id)));
    }

    [TestMethod]
    public async Task ChangingTheWinner_SwapsTheComments()
    {
        comments[grandPrix.Id].Add(Comment("Ran long", null, TagA));
        comments[timeTrial.Id].Add(Comment("Crowd favourite", null, TagB));
        var page = await OpenEventDetails("Game night");
        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "Ran long" }, Texts(page)));
        var rewon = Rewon(night, timeTrial);
        api.Json(HttpMethod.Put, $"/api/events/{night.Id}", rewon)
           .Json(HttpMethod.Get, "/api/events", new[] { rewon, open });

        await Card(page, "Time Trial").Button("Select Winner").ClickAsync(new());

        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "Crowd favourite" }, Texts(page)));
        var panel = Assert.ContainsSingle(Panels(page));
        Assert.AreSame(Card(page, "Time Trial"), panel.Closest("fluent-card"));
        Assert.DoesNotContain("Ran long", page.FormDialog().TextContent);
        Assert.AreEqual(1, api.Count(HttpMethod.Get, ListPath(timeTrial.Id)));
    }

    // Select Winner swaps the panel, so it waits while a Comment request runs: its buttons are off, and the
    // winner is unchanged until the request ends.
    [TestMethod]
    public async Task SelectWinner_WhileACommentRequestRuns_DoesNothing()
    {
        var page = await OpenEventDetails("Game night");
        var path = ListPath(grandPrix.Id);
        var gate = api.Hold(HttpMethod.Post, path, () => Comment("New", night.Id, TagC).Response(grandPrix.Id, HttpStatusCode.Created));
        var rewon = Rewon(night, timeTrial);
        api.Json(HttpMethod.Put, $"/api/events/{night.Id}", rewon)
           .Json(HttpMethod.Get, "/api/events", new[] { rewon, open });
        await TypeNew(page, "New");

        var add = page.FormDialog().Button("Add Comment").ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsTrue(Card(page, "Time Trial").Button("Select Winner").IsBusy(), "Select Winner while the add runs"));
        await Card(page, "Time Trial").Button("Select Winner").ClickAsync(new());
        Assert.AreEqual(0, api.Count(HttpMethod.Put, $"/api/events/{night.Id}"));
        Assert.IsFalse(page.HasConfirmation());

        gate.SetResult();
        await add;
        page.WaitForAssertion(() => Assert.IsFalse(Card(page, "Time Trial").Button("Select Winner").IsBusy()));
        Assert.AreEqual(0, api.Count(HttpMethod.Put, $"/api/events/{night.Id}"));
        CollectionAssert.AreEqual(new[] { "New" }, Texts(page));
        Assert.AreSame(Card(page, "Grand Prix"), Assert.ContainsSingle(Panels(page)).Closest("fluent-card"));

        // Once the request is done, Select Winner works again.
        await Card(page, "Time Trial").Button("Select Winner").ClickAsync(new());
        Assert.AreEqual(1, api.Count(HttpMethod.Put, $"/api/events/{night.Id}"));
        page.WaitForAssertion(() => Assert.AreSame(Card(page, "Time Trial"), Assert.ContainsSingle(Panels(page)).Closest("fluent-card")));
    }

    // #195 item 5: the Discard question of a Select Winner stays open while a request it did not wait for runs (a
    // Comment add or a metrics save started under the open question). Its Discard button is disabled meanwhile and
    // a click that still reaches it does nothing; Keep editing stays usable and the text stays as is. Once the
    // request ends, Discard saves the new Winner.
    [TestMethod]
    [DataRow("comment add")]
    [DataRow("metrics save")]
    public async Task SelectWinner_DiscardWaitsWhileARequestRuns_ThenSavesTheWinner(string request)
    {
        var eventPath = $"/api/events/{night.Id}";
        var metricsPath = $"{eventPath}/metrics";
        api.Json(HttpMethod.Get, metricsPath, new { id = night.Id });
        var page = await OpenEventDetails("Game night");
        page.WaitForAssertion(() => Assert.IsNotNull(page.FormDialog().QuerySelector("#metrics-attendees")));
        var rewon = Rewon(night, timeTrial);
        api.Json(HttpMethod.Put, eventPath, rewon)
           .Json(HttpMethod.Get, "/api/events", new[] { rewon, open });
        var gate = request == "comment add"
            ? api.Hold(HttpMethod.Post, ListPath(grandPrix.Id), () => Comment("Half a thought", night.Id, TagC).Response(grandPrix.Id, HttpStatusCode.Created))
            : api.Hold(HttpMethod.Put, metricsPath, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = night.Id, attendees = 12 }) });
        await TypeNew(page, "Half a thought");
        await page.FormDialog().QuerySelector("#metrics-attendees")!.InputAsync(new ChangeEventArgs { Value = "12" });
        await Card(page, "Time Trial").Button("Select Winner").ClickAsync(new());
        Assert.AreEqual("Discard changes?", page.Confirmation().QuerySelector("h4")!.TextContent.Trim());
        Assert.IsFalse(page.Confirmation().Button("Discard").IsBusy(), "nothing runs yet");

        var running = page.FormDialog().Button(request == "comment add" ? "Add Comment" : "Save metrics").ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsTrue(page.FormDialog().Button("Close").IsBusy(), $"the {request} is running"));

        Assert.IsTrue(page.Confirmation().Button("Discard").IsBusy(), "Discard waits while the request runs");
        Assert.AreEqual("Discard", page.Confirmation().Button("Discard").TextContent.Trim());
        Assert.IsFalse(page.Confirmation().Button("Keep editing").IsBusy());
        // A click that still reaches Discard's handler is held: the question stays and nothing is sent.
        await page.InvokeAsync(() => PageActions.ClickHandler(page, "Discard").InvokeAsync(new()));
        Assert.IsTrue(page.HasConfirmation(), "the question stays open");
        Assert.AreEqual(0, api.Count(HttpMethod.Put, eventPath));

        gate.SetResult();
        await running;
        page.WaitForAssertion(() => Assert.IsFalse(page.Confirmation().Button("Discard").IsBusy(), "Discard is usable once the request ends"));
        Assert.AreEqual(0, api.Count(HttpMethod.Put, eventPath));
        await page.ConfirmAsync("Discard");

        page.WaitForAssertion(() => Assert.AreEqual(1, api.Count(HttpMethod.Put, eventPath)));
        Assert.Contains($"\"winnerActivityId\":\"{timeTrial.Id}\"", Assert.ContainsSingle(api.Bodies(HttpMethod.Put, eventPath))!);
        page.WaitForAssertion(() => Assert.AreSame(Card(page, "Time Trial"), Assert.ContainsSingle(Panels(page)).Closest("fluent-card")));
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsTrue(page.HasFormDialog());
    }

    // Text typed in the add box, or an edit that differs, would be dropped with the panel: Select Winner asks
    // first. Keep editing keeps the text and the Winner; Discard saves the new Winner.
    [TestMethod]
    [DataRow("add")]
    [DataRow("edit")]
    public async Task SelectWinner_WithUnsavedCommentText_AsksToDiscard(string kind)
    {
        comments[grandPrix.Id].Add(Comment("Ran long", null, TagA));
        comments[timeTrial.Id].Add(Comment("Crowd favourite", null, TagB));
        var page = await OpenEventDetails("Game night");
        page.WaitForAssertion(() => Assert.HasCount(1, Items(page)));
        var rewon = Rewon(night, timeTrial);
        api.Json(HttpMethod.Put, $"/api/events/{night.Id}", rewon)
           .Json(HttpMethod.Get, "/api/events", new[] { rewon, open });
        Func<string?> typed;
        if (kind == "add")
        {
            await TypeNew(page, "Half a thought");
            typed = () => NewBox(page)!.GetAttribute("value");
        }
        else
        {
            await Click(Items(page)[0].Button("Edit"), page);
            await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = "Ran longer" });
            typed = () => EditBox(page)!.GetAttribute("value");
        }

        await Card(page, "Time Trial").Button("Select Winner").ClickAsync(new());

        Assert.AreEqual("Discard changes?", page.Confirmation().QuerySelector("h4")!.TextContent.Trim());
        Assert.AreEqual(0, api.Count(HttpMethod.Put, $"/api/events/{night.Id}"));
        await page.Confirmation().Button("Keep editing").ClickAsync(new());
        Assert.IsFalse(page.HasConfirmation());
        Assert.AreEqual(kind == "add" ? "Half a thought" : "Ran longer", typed());
        Assert.AreEqual(0, api.Count(HttpMethod.Put, $"/api/events/{night.Id}"));
        Assert.AreSame(Card(page, "Grand Prix"), Assert.ContainsSingle(Panels(page)).Closest("fluent-card"));

        await Card(page, "Time Trial").Button("Select Winner").ClickAsync(new());
        await page.ConfirmAsync("Discard");

        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "Crowd favourite" }, Texts(page)));
        Assert.AreEqual(1, api.Count(HttpMethod.Put, $"/api/events/{night.Id}"));
        Assert.Contains($"\"winnerActivityId\":\"{timeTrial.Id}\"", Assert.ContainsSingle(api.Bodies(HttpMethod.Put, $"/api/events/{night.Id}"))!);
        Assert.AreSame(Card(page, "Time Trial"), Assert.ContainsSingle(Panels(page)).Closest("fluent-card"));
        Assert.IsTrue(page.HasFormDialog());
        // Nothing typed was sent as a Comment.
        Assert.IsEmpty(api.Requests.Where(r => r.Path.Contains("/comments") && r.Method != HttpMethod.Get).ToList());
    }
}
