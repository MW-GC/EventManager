using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// Event Selections under Table Storage's limits (#46). A string property holds at most 64 KiB of
/// UTF-16, so 32,768 characters; TableStore splits a long serialised value into chunk properties
/// and joins them on read. Rows written before chunking (one property) still read back.
/// </summary>
[TestClass]
public class PropertyLimitTests
{
    /// <summary>Table Storage's per-property string limit in UTF-16 characters (64 KiB).</summary>
    private const int MaxStringProperty = 32 * 1024;

    // The JSON TableStore wrote before #46: camelCase with the default (escaping) encoder.
    private static readonly JsonSerializerOptions OldJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions Compare = new(JsonSerializerDefaults.Web);

    private readonly LibraryHarness h = new();

    // Non-ASCII text from the Basic Multilingual Plane, as people type it.
    private const string Sample = "Ünïcødé 日本語 — café ";

    private static string Text(int length) => string.Concat(Enumerable.Repeat(Sample, length / Sample.Length + 1))[..length];

    /// <summary>Five Selections with every #45 cap at its limit: names 100, URLs 2048, text 2000.</summary>
    private static EventEntity MaximumEvent() => BuildEvent(EventEntity.MaximumSelections, text: 2000, url: 2048);

    private static EventEntity BuildEvent(int count, int text, int url)
    {
        var selections = Enumerable.Range(0, count).Select(i =>
        {
            var game = new Game
            {
                Id = Guid.NewGuid(), Name = Text(100),
                ImageUrl = "https://img.test/" + Text(url - 17), Website = "https://web.test/" + Text(url - 17), IconUrl = "https://ico.test/" + Text(url - 17),
            };
            var activity = new Activity
            {
                Id = Guid.NewGuid(), GameId = game.Id, Name = Text(100),
                Description = Text(text), Rules = Text(text), SetupRequirements = Text(text), Comments = Text(text),
                ThemeIds = [Guid.NewGuid(), Guid.NewGuid()], HolidayIds = [Guid.NewGuid()],
            };
            return new Selection { Game = game, Activity = activity };
        }).ToList();
        return new EventEntity
        {
            Name = Text(100), Date = new DateTimeOffset(2026, 10, 31, 19, 0, 0, TimeSpan.Zero),
            Selections = selections, UniqueGamesOnly = true, WinnerActivityId = selections[count - 1].Activity.Id,
        };
    }

    private static string Snapshot(EventEntity evt) => JsonSerializer.Serialize(new
    {
        evt.Name, evt.Date, evt.Selections, evt.UniqueGamesOnly, evt.WinnerActivityId,
    }, Compare);

    private TableEntity Written() => h.EventTable.Invocations
        .Where(i => i.Method.Name is nameof(TableClient.AddEntityAsync) or nameof(TableClient.UpdateEntityAsync))
        .Select(i => (TableEntity)i.Arguments[0]!)
        .Last();

    private static void AssertWithinPropertyLimit(TableEntity row)
    {
        foreach (var (name, value) in row)
            if (value is string text)
                Assert.IsTrue(text.Length <= MaxStringProperty, $"{name} holds {text.Length} characters.");
    }

    [TestMethod]
    public async Task FiveMaximumLengthSelectionsAreChunkedUnderThePropertyLimitAndReassemble()
    {
        var input = MaximumEvent();

        var created = await h.EventApi.SaveCustomized(TestRequests.Json(input), default);

        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(created));
        var saved = ApiResults.Value<EventEntity>(created);
        var row = Written();
        AssertWithinPropertyLimit(row);

        // About 70K characters of compact JSON: three chunks, in order.
        var chunks = row.Keys.Where(k => k == "Selections" || k.StartsWith("Selections__", StringComparison.Ordinal)).ToList();
        Assert.AreSequenceEqual(new[] { "Selections", "Selections__1", "Selections__2" }, chunks.Order(StringComparer.Ordinal).ToArray());
        var json = string.Concat(chunks.Select(row.GetString));
        Assert.IsTrue(json.Length > 2 * TableStore<EventEntity>.ChunkLength, $"{json.Length} characters");
        Assert.IsTrue(row.GetString("Selections")!.Length <= TableStore<EventEntity>.ChunkLength);

        // Non-ASCII text is stored as the character, not as a \uXXXX escape.
        Assert.Contains("日本語", json);
        Assert.Contains("Ünïcødé", json);
        Assert.DoesNotContain("\\u", json);
        Assert.IsTrue(row.Count < 255);

        // Both reads reassemble the identical Event.
        var read = ApiResults.Value<EventEntity>(await h.EventApi.Get(TestRequests.Empty(), saved.Id, default));
        Assert.AreEqual(Snapshot(saved), Snapshot(read));
        Assert.AreEqual(Snapshot(input), Snapshot(read));
        var listed = Assert.ContainsSingle(ApiResults.Value<List<EventEntity>>(await h.EventApi.GetAll(TestRequests.Empty(), default)));
        Assert.AreEqual(Snapshot(saved), Snapshot(listed));
    }

    [TestMethod]
    public async Task UpdatingAMaximumEventStaysChunkedAndShrinkingDropsTheChunks()
    {
        var saved = ApiResults.Value<EventEntity>(await h.EventApi.SaveCustomized(TestRequests.Json(MaximumEvent()), default));

        var bigger = MaximumEvent();
        var put = await h.EventApi.Update(TestRequests.Json(bigger), saved.Id, default);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(put));
        AssertWithinPropertyLimit(Written());
        var read = ApiResults.Value<EventEntity>(await h.EventApi.Get(TestRequests.Empty(), saved.Id, default));
        Assert.AreEqual(Snapshot(bigger), Snapshot(read));

        var small = MaximumEvent();
        small.Selections = [small.Selections[0] with { Activity = small.Selections[0].Activity with { Description = "short" } }];
        small.Selections[0] = small.Selections[0] with { Game = small.Selections[0].Game with { ImageUrl = null, Website = null, IconUrl = null } };
        small.Selections[0] = small.Selections[0] with { Activity = small.Selections[0].Activity with { Rules = "", SetupRequirements = "", Comments = null } };
        small.NormalizeWinner(); // one Selection is its own Winner
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(await h.EventApi.Update(TestRequests.Json(small), saved.Id, default)));
        var row = h.Events[saved.Id.ToString("D")];
        Assert.IsFalse(row.ContainsKey("Selections__1"), "A replaced row must not keep stale chunks.");
        read = ApiResults.Value<EventEntity>(await h.EventApi.Get(TestRequests.Empty(), saved.Id, default));
        Assert.AreEqual(Snapshot(small), Snapshot(read));
    }

    [TestMethod]
    public async Task AnEventRowInTheOldSingleSelectionsShapeReadsBackIdentically()
    {
        // Written by the code before #46: one Selections property, non-ASCII escaped as \uXXXX.
        // (Before #46 a row only stored if that one property fit, so the fixture is sized to fit.)
        var old = BuildEvent(2, text: 600, url: 300);
        old.Id = Guid.NewGuid();
        var oldJson = JsonSerializer.Serialize(old.Selections, OldJson);
        Assert.Contains("\\u", oldJson);
        Assert.IsTrue(oldJson.Length < MaxStringProperty);
        var key = old.Id.ToString("D");
        h.Events[key] = new TableEntity("Event", key)
        {
            ["Name"] = old.Name, ["Date"] = old.Date, ["Selections"] = oldJson, ["UniqueGamesOnly"] = old.UniqueGamesOnly,
        };
        old.WinnerActivityId = null;

        var read = ApiResults.Value<EventEntity>(await h.EventApi.Get(TestRequests.Empty(), old.Id, default));
        var listed = Assert.ContainsSingle(ApiResults.Value<List<EventEntity>>(await h.EventApi.GetAll(TestRequests.Empty(), default)));

        Assert.AreEqual(Snapshot(old), Snapshot(read));
        Assert.AreEqual(Snapshot(old), Snapshot(listed));
        Assert.AreEqual("日本語", read.Selections[1].Activity.Description.Substring(Sample.IndexOf('日'), 3));
    }

    [TestMethod]
    public async Task AnActivityRowInTheOldShapeStillReadsBack()
    {
        var game = h.SeedGame();
        var id = Guid.NewGuid();
        var theme = Guid.NewGuid();
        h.Activities[id.ToString("D")] = new TableEntity("Activity", id.ToString("D"))
        {
            ["GameId"] = game.Id, ["Name"] = "Act", ["Description"] = "Café",
            ["ThemeIds"] = JsonSerializer.Serialize(new List<Guid> { theme }, OldJson), ["HolidayIds"] = "[]",
        };

        var read = ApiResults.Value<ActivityEntity>(await h.ActivityApi.Get(TestRequests.Empty(), id, default));

        Assert.AreSequenceEqual(new[] { theme }, read.ThemeIds);
        Assert.IsEmpty(read.HolidayIds);
        Assert.AreEqual("Café", read.Description);
    }

    [TestMethod]
    public async Task SelectionsOverTheRowLimitAre413NamingTheFieldAndWriteNothing()
    {
        // Selection snapshots are not capped by a validator; past 1 MiB the row cannot be stored at all.
        var huge = MaximumEvent();
        huge.Selections = huge.Selections
            .Select(s => s with { Activity = s.Activity with { Description = new string('x', 120_000) } })
            .ToList();

        var create = await h.EventApi.SaveCustomized(TestRequests.Json(huge), default);

        Assert.AreEqual(StatusCodes.Status413PayloadTooLarge, ApiResults.Status(create));
        Assert.AreEqual("Selections is too large to store.", ApiResults.Message(create));
        Assert.IsEmpty(h.Events);
        Assert.AreEqual(0, h.Writes());
        var entry = Assert.ContainsSingle(h.EventLog.Entries);
        Assert.AreEqual(LogLevel.Warning, entry.Level);

        // An update over the limit is refused the same way and leaves the stored row alone.
        var saved = ApiResults.Value<EventEntity>(await h.EventApi.SaveCustomized(TestRequests.Json(MaximumEvent()), default));
        var before = Snapshot(ApiResults.Value<EventEntity>(await h.EventApi.Get(TestRequests.Empty(), saved.Id, default)));
        var writes = h.Writes();

        var update = await h.EventApi.Update(TestRequests.Json(huge), saved.Id, default);

        Assert.AreEqual(StatusCodes.Status413PayloadTooLarge, ApiResults.Status(update));
        Assert.AreEqual("Selections is too large to store.", ApiResults.Message(update));
        Assert.AreEqual(writes, h.Writes());
        Assert.AreEqual(before, Snapshot(ApiResults.Value<EventEntity>(await h.EventApi.Get(TestRequests.Empty(), saved.Id, default))));
    }

    [TestMethod]
    public void TheRowEstimateMatchesTheDocumentedSizes()
    {
        // 4 + 2 * (PK + RK) + per property 8 + 2 * name + value (string: 4 + 2 * length).
        var row = new TableEntity("Event", "k") { ["Name"] = "ab", ["UniqueGamesOnly"] = true };
        Assert.AreEqual(4 + 2 * 6 + (8 + 8 + 4 + 4) + (8 + 30 + 1), TableStore<EventEntity>.EstimatedBytes(row));
    }

    [TestMethod]
    public async Task StoragePreconditionFailedMapsToTheSame409()
    {
        var error = new RequestFailedException(412, "SECRET-INTERNAL-DETAIL", "UpdateConditionNotSatisfied", null);
        h.GameTable.Setup(t => t.UpsertEntityAsync(It.IsAny<GameEntity>(), TableUpdateMode.Replace, It.IsAny<CancellationToken>()))
            .ThrowsAsync(error);

        var result = await h.GameApi.Create(TestRequests.Json(new { name = "Alpha" }), default);

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(result));
        Assert.AreEqual(RouteFailures.ConflictMessage, ApiResults.Message(result));
        var entry = Assert.ContainsSingle(h.GameLog.Entries);
        Assert.AreSame(error, entry.Exception);
        Assert.AreEqual(LogLevel.Warning, entry.Level);
    }
}
