using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// Selection snapshots no longer carry an Activity's Comments (#133, ADR 0001). An Event stored
/// earlier still has the Activity's old single text as <c>comments</c> inside its Selections JSON:
/// it must still load with every other field, and the next save writes the snapshot without it.
/// </summary>
[TestClass]
public class OldSelectionSnapshotTests
{
    private readonly LibraryHarness h = new();

    private static readonly Guid GameId = Guid.Parse("6a0c3c55-1b7e-4f43-9a51-0d1f4c2b8e11");
    private static readonly Guid ActivityId = Guid.Parse("3f2d9b84-7c61-4e0a-b5d2-8a9e6c1f0b27");
    private static readonly Guid ThemeId = Guid.Parse("c1d2e3f4-a5b6-4c7d-8e9f-0a1b2c3d4e5f");
    private static readonly Guid HolidayId = Guid.Parse("9e8d7c6b-5a49-4382-a1b0-c9d8e7f6a5b4");
    private static readonly DateTimeOffset Date = new(2026, 10, 9, 19, 0, 0, TimeSpan.Zero);

    // The Selections column exactly as TableStore wrote it before #133: camelCase JSON, the
    // Activity snapshot carrying the old single text as "comments".
    private static readonly string OldSelections =
        "[{\"game\":{\"id\":\"" + GameId + "\",\"name\":\"Kart\",\"imageUrl\":null,\"website\":\"https://kart.test\",\"iconUrl\":\"https://kart.test/i.png\"}," +
        "\"activity\":{\"id\":\"" + ActivityId + "\",\"gameId\":\"" + GameId + "\",\"name\":\"Grand Prix\",\"description\":\"Four races\"," +
        "\"rules\":\"150cc\",\"themeIds\":[\"" + ThemeId + "\"],\"holidayIds\":[\"" + HolidayId + "\"],\"setupRequirements\":\"Four controllers\"," +
        "\"comments\":\"Ran long, cut the second round\"}}]";

    private Guid StoreOldEvent()
    {
        var id = Guid.NewGuid();
        h.Events[id.ToString("D")] = new TableEntity("Event", id.ToString("D"))
        {
            ["Name"] = "Game night", ["Date"] = Date, ["Selections"] = OldSelections,
            ["WinnerActivityId"] = ActivityId, ["UniqueGamesOnly"] = true,
        };
        return id;
    }

    [TestMethod]
    public async Task AnEventWhoseStoredSnapshotCarriesCommentsStillLoadsWithEveryOtherField()
    {
        var id = StoreOldEvent();

        var one = await h.EventApi.Get(TestRequests.Empty(), id, default);
        var all = await h.EventApi.GetAll(TestRequests.Empty(), default);

        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(one));
        foreach (var read in new[] { ApiResults.Value<EventEntity>(one), Assert.ContainsSingle(ApiResults.Value<List<EventEntity>>(all)) })
        {
            Assert.AreEqual("Game night", read.Name);
            Assert.AreEqual(Date, read.Date);
            Assert.AreEqual(ActivityId, read.WinnerActivityId);
            Assert.IsTrue(read.UniqueGamesOnly);
            var selection = Assert.ContainsSingle(read.Selections);
            Assert.AreEqual(GameId, selection.Game.Id);
            Assert.AreEqual("Kart", selection.Game.Name);
            Assert.IsNull(selection.Game.ImageUrl);
            Assert.AreEqual("https://kart.test", selection.Game.Website);
            Assert.AreEqual("https://kart.test/i.png", selection.Game.IconUrl);
            Assert.AreEqual(ActivityId, selection.Activity.Id);
            Assert.AreEqual(GameId, selection.Activity.GameId);
            Assert.AreEqual("Grand Prix", selection.Activity.Name);
            Assert.AreEqual("Four races", selection.Activity.Description);
            Assert.AreEqual("150cc", selection.Activity.Rules);
            Assert.AreEqual(ThemeId, Assert.ContainsSingle(selection.Activity.ThemeIds));
            Assert.AreEqual(HolidayId, Assert.ContainsSingle(selection.Activity.HolidayIds));
            Assert.AreEqual("Four controllers", selection.Activity.SetupRequirements);
        }

        // The response the browser reads carries no comments either.
        var json = JsonSerializer.Serialize(ApiResults.Value<EventEntity>(one), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("comments", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Four controllers", json);
    }

    [TestMethod]
    public async Task SavingThatEventWritesTheSnapshotWithoutComments()
    {
        var id = StoreOldEvent();
        // The save the Edit dialog sends: the Event as read, with a new name and the untouched snapshot.
        // An older browser that still held the old text in its copy sends it too; neither is stored.
        var read = ApiResults.Value<EventEntity>(await h.EventApi.Get(TestRequests.Empty(), id, default));
        var body = JsonSerializer.SerializeToNode(read, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        body["name"] = "Renamed night";
        body["selections"]![0]!["activity"]!["comments"] = "Sent by an older browser";

        var saved = await h.EventApi.Update(TestRequests.Raw(body.ToJsonString()), id, default);

        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(saved));
        var row = h.Events[id.ToString("D")];
        var stored = row.GetString("Selections")!;
        Assert.DoesNotContain("comments", stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ran long", stored);
        Assert.DoesNotContain("Sent by an older browser", stored);
        Assert.AreEqual("Renamed night", row.GetString("Name"));
        // Every other snapshot field is written back as it was.
        foreach (var kept in new[] { "\"name\":\"Grand Prix\"", "\"description\":\"Four races\"", "\"rules\":\"150cc\"",
                     "\"setupRequirements\":\"Four controllers\"", "\"website\":\"https://kart.test\"", ThemeId.ToString(), HolidayId.ToString() })
            Assert.Contains(kept, stored);
        Assert.AreEqual(ActivityId, row.GetGuid("WinnerActivityId"));
    }
}
