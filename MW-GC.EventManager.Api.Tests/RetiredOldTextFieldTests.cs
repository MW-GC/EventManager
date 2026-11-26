using Microsoft.AspNetCore.Http;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>The Activity's retired single text field (#133, #188) through the real Activity routes.</summary>
[TestClass]
public class RetiredOldTextFieldTests
{
    private readonly LibraryHarness h = new();

    // The old single text field is gone (#133): an old text over its old 2000-character cap, sent as `comments`,
    // which used to be a 400, is now ignored on create and update and stored nowhere.
    [TestMethod]
    public async Task AnOldTextValueOfAnyLengthIsIgnoredOnCreateAndUpdate()
    {
        var game = h.SeedGame();
        var overOldCap = new string('c', 2001);

        var create = await h.ActivityApi.Create(TestRequests.Json(new { gameId = game.Id, name = "Act", comments = overOldCap }), default);
        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(create));
        var id = ApiResults.Value<ActivityEntity>(create).Id;
        var update = await h.ActivityApi.Update(TestRequests.Json(new { gameId = game.Id, name = "Renamed", comments = overOldCap }), id, default);

        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(update));
        var row = h.Activities[id.ToString("D")];
        Assert.AreEqual("Renamed", row.GetString("Name"));
        Assert.IsFalse(row.Keys.Any(k => k.Equals(OldActivityRows.OldTextColumn, StringComparison.OrdinalIgnoreCase)), "No old-text column is stored.");
        Assert.IsFalse(row.Select(p => p.Value).OfType<string>().Any(v => v.Contains(overOldCap)), "The value is stored nowhere on the row.");
        Assert.IsEmpty(h.StoredComments(), "And it does not become a Comment.");
    }
}
