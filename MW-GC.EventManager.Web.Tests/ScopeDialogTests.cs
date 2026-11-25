using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Web.Shared;

namespace MW_GC.EventManager.Web.Tests;

// #204 (story 21, story 25): the scope question on its own, with the three options the edit card will give it and the
// two the cancel gives it. Nothing is chosen when it opens and Confirm waits for a choice; Confirm's own text names the
// chosen scope; Cancel, Escape and a second click answer once; every control has an accessible name, and the scopes
// are one radio group, so the browser moves between them with the arrow keys.
[TestClass]
public sealed class ScopeDialogTests : IDisposable
{
    private readonly PageHarness harness = new(new StubApiHandler());
    private readonly List<string> answers = [];

    public void Dispose() => harness.Dispose();

    private IReadOnlyList<ScopeOption> Three() =>
    [
        new("This event", "Only this one changes.", "Save this event", Run("one")),
        new("This and following", "This one and every later one change.", "Save this and following", Run("following")),
        new("All events", "Every future event changes.", "Save all events", Run("all"))
    ];

    private Func<Task> Run(string name) => () => { answers.Add(name); return Task.CompletedTask; };

    private IRenderedComponent<ScopeDialog> Open(IReadOnlyList<ScopeOption>? options = null, bool destructive = false) =>
        harness.Render<ScopeDialog>(p => p
            .Add(d => d.Destructive, destructive)
            .Add(d => d.Title, "Edit repeating event")
            .Add(d => d.Message, "Choose what to change.")
            .Add(d => d.GroupLabel, "What to change")
            .Add(d => d.Options, options ?? Three())
            .Add(d => d.OnConfirm, (ScopeOption o) => o.OnConfirm())
            .Add(d => d.OnCancel, () => answers.Add("cancel")));

    [TestMethod]
    public void ItOpensWithNothingChosen_AndConfirmDisabled_ShowingEveryOptionWithItsDescription()
    {
        var dialog = Open();

        Assert.IsTrue(Confirm(dialog).HasAttribute("disabled"));
        Assert.AreEqual("Choose an option", Confirm(dialog).TextContent.Trim());
        Assert.IsFalse(dialog.FindAll("fluent-radio").Any(r => r.HasAttribute("checked")));
        CollectionAssert.AreEqual(new[] { "This event", "This and following", "All events" },
            dialog.FindAll(".scope-label").Select(l => l.TextContent.Trim()).ToList());
        CollectionAssert.AreEqual(new[] { "Only this one changes.", "This one and every later one change.", "Every future event changes." },
            dialog.FindAll(".scope-description").Select(l => l.TextContent.Trim()).ToList());
    }

    [TestMethod]
    public async Task ConfirmNamesTheChosenScope_AndFollowsTheChoice()
    {
        var dialog = Open();

        await Choose(dialog, "This and following");
        Assert.AreEqual("Save this and following", Confirm(dialog).TextContent.Trim());
        Assert.IsFalse(Confirm(dialog).HasAttribute("disabled"));

        await Choose(dialog, "All events");
        Assert.AreEqual("Save all events", Confirm(dialog).TextContent.Trim());

        await Choose(dialog, "This event");
        Assert.AreEqual("Save this event", Confirm(dialog).TextContent.Trim());
        await Confirm(dialog).ClickAsync(new());

        CollectionAssert.AreEqual(new[] { "one" }, answers);
    }

    [TestMethod]
    public async Task AClickThatReachesTheDisabledConfirm_DoesNothing_AndKeepsTheAnswer()
    {
        var dialog = Open();
        var confirm = dialog.FindComponents<FluentButton>().Single(b => b.Find("fluent-button").ClassList.Contains("scope-confirm")).Instance.OnClick;

        await dialog.InvokeAsync(() => confirm.InvokeAsync(new()));
        Assert.IsEmpty(answers);

        await Choose(dialog, "All events");
        await Confirm(dialog).ClickAsync(new());
        CollectionAssert.AreEqual(new[] { "all" }, answers);
    }

    [TestMethod]
    [DataRow("confirm")]
    [DataRow("cancel")]
    [DataRow("dismiss")]
    public async Task AFastSecondAnswer_CountsOnce(string how)
    {
        var dialog = Open();
        await Choose(dialog, "All events");
        // Both handlers taken before the first answer re-renders anything: a double click.
        var confirm = dialog.FindComponents<FluentButton>().Single(b => b.Find("fluent-button").ClassList.Contains("scope-confirm")).Instance.OnClick;
        var cancel = dialog.FindComponents<FluentButton>().Single(b => b.Find("fluent-button").TextContent.Trim() == "Cancel").Instance.OnClick;

        Task First() => how switch
        {
            "confirm" => confirm.InvokeAsync(new()),
            "cancel" => cancel.InvokeAsync(new()),
            _ => dialog.Find("fluent-dialog").TriggerEventAsync("ondialogdismiss", new DialogEventArgs { Id = dialog.Find("fluent-dialog").Id, Reason = "dismiss" })
        };
        await dialog.InvokeAsync(First);
        await dialog.InvokeAsync(() => confirm.InvokeAsync(new()));
        await dialog.InvokeAsync(() => cancel.InvokeAsync(new()));

        CollectionAssert.AreEqual(new[] { how == "confirm" ? "all" : "cancel" }, answers);
    }

    [TestMethod]
    public void EveryControlHasAnAccessibleName_AndTheScopesAreOneRadioGroup()
    {
        var dialog = Open();

        Assert.AreEqual("Edit repeating event", dialog.Find("fluent-dialog").GetAttribute("aria-label"));
        var group = dialog.Find("fluent-radio-group");
        Assert.AreEqual("What to change", group.GetAttribute("aria-label"));
        Assert.AreEqual("vertical", group.GetAttribute("orientation"));
        var radios = group.QuerySelectorAll("fluent-radio").ToList();
        Assert.HasCount(3, radios);
        CollectionAssert.AreEqual(new[] { "This event", "This and following", "All events" }, radios.Select(r => r.GetAttribute("aria-label")).ToList());
        // One name for the group and its radios: the browser's arrow keys move between them, and Tab enters and leaves the group once.
        Assert.AreEqual(1, radios.Select(r => r.GetAttribute("name")).Append(group.GetAttribute("name")).Distinct().Count());
        foreach (var radio in radios)
        {
            var described = dialog.Find($"#{radio.GetAttribute("aria-describedby")}");
            Assert.IsTrue(radio.Contains(described), "a radio's description is another radio's");
        }
        Assert.AreEqual("Cancel", dialog.FindAll("fluent-button").First().TextContent.Trim());
        // The question shows on the page and tells focus.js so it takes focus, and lets go of it once gone.
        harness.JSInterop.VerifyInvoke("emFocus.question", 1);
    }

    [TestMethod]
    public async Task AnArrowKeyChoiceMovesTheChoice_AndEnterOnConfirmAnswersIt()
    {
        var dialog = Open(Two(), destructive: true);
        Assert.IsNull(ConfirmColour(dialog), "the disabled Confirm is painted as a destructive answer before a choice");

        // The radio group reports the choice the arrow key moved it to as a change of the group's value.
        await Choose(dialog, "This and all future");
        Assert.AreEqual("Cancel the series", Confirm(dialog).TextContent.Trim());
        await Choose(dialog, "This event only");
        Assert.AreEqual("Delete this event", Confirm(dialog).TextContent.Trim());
        // Enter on a focused fluent-button is the browser's click.
        await Confirm(dialog).ClickAsync(new());

        CollectionAssert.AreEqual(new[] { "one" }, answers);
        Assert.AreEqual("var(--error)", ConfirmColour(dialog), "a destructive scope is not painted in the error colour");
    }

    [TestMethod]
    public void TheCancelQuestion_OffersTheTwoCancelScopes_WithTheirTexts()
    {
        var request = ScopeRequest.CancelOccurrence("Weekly raid", new DateTimeOffset(2026, 10, 2, 19, 0, 0, TimeSpan.Zero), Run("one"), Run("series"));

        Assert.AreEqual("Delete repeating event", request.Title);
        Assert.AreEqual("What to delete", request.GroupLabel);
        Assert.IsTrue(request.Destructive);
        CollectionAssert.AreEqual(new[] { "This event only", "This and all future events" }, request.Options.Select(o => o.Label).ToList());
        CollectionAssert.AreEqual(new[] { "Delete this event", "Cancel the series" }, request.Options.Select(o => o.ConfirmText).ToList());
        CollectionAssert.AreEqual(new[]
        {
            "It stays cancelled: the series will not make it again.",
            "Stops the series and removes every future event of it that has not been played. Past and played events stay."
        }, request.Options.Select(o => o.Description).ToList());
        StringAssert.StartsWith(request.Message, "Weekly raid on ");
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private IReadOnlyList<ScopeOption> Two() =>
    [
        new("This event only", "Only this one goes.", "Delete this event", Run("one")),
        new("This and all future", "The series stops.", "Cancel the series", Run("series"))
    ];

    private static IElement Confirm(IRenderedComponent<ScopeDialog> dialog) => dialog.Find("fluent-button.scope-confirm");

    // The confirm button's background colour, as the dialog hands it to Fluent UI.
    private static string? ConfirmColour(IRenderedComponent<ScopeDialog> dialog) =>
        dialog.FindComponents<FluentButton>().Single(b => b.Find("fluent-button").ClassList.Contains("scope-confirm")).Instance.BackgroundColor;

    private static Task Choose(IRenderedComponent<ScopeDialog> dialog, string label)
    {
        var radio = dialog.FindAll("fluent-radio").Single(r => r.QuerySelector(".scope-label")!.TextContent.Trim() == label);
        return dialog.Find("fluent-radio-group").TriggerEventAsync("onradiogroupchange", new ChangeEventArgs { Value = radio.GetAttribute("current-value") });
    }
}
