using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Shared.Requests;

namespace MW_GC.EventManager.Api.Tests;

[TestClass]
public class EventGeneratorTests
{
    private readonly EventGenerator generator = new();
    private readonly Game first = new() { Id = Guid.NewGuid(), Name = "Test" };
    private readonly Game second = new() { Id = Guid.NewGuid(), Name = "Test" };
    private Activity Activity(Game game) => new() { Id = Guid.NewGuid(), Name = "Test", GameId = game.Id };

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void NormalGenerationReturnsRequestedDistinctActivities(bool unique)
    {
        var activities = new[] { Activity(first), Activity(second) };
        for (var i = 0; i < 100; i++)
        {
            var result = generator.Generate([first, second], activities, new() { Count = 2, UniqueGamesOnly = unique });
            Assert.IsNotNull(result);
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual(2, result.Select(s => s.Activity.Id).Distinct().Count());
            foreach (var s in result) Assert.AreEqual(s.Game.Id, s.Activity.GameId);
        }
    }

    [TestMethod]
    public void RepeatedGamesExhaustPoolWithoutRepeatingIds()
    {
        var activities = Enumerable.Range(0, 5).Select(_ => Activity(first)).ToList();
        activities.Add(activities[0]); // Duplicate records do not add capacity.
        for (var i = 0; i < 100; i++)
        {
            var result = generator.Generate([first], activities, new() { Count = 5, UniqueGamesOnly = false });
            Assert.IsNotNull(result);
            Assert.AreEqual(5, result.Select(s => s.Activity.Id).Distinct().Count());
        }
        Assert.IsNull(generator.Generate([first], activities, new() { Count = 6, UniqueGamesOnly = false }));
        Assert.IsNull(generator.Generate([first], activities, new() { Count = 2, UniqueGamesOnly = true }));
    }

    [TestMethod]
    public void SkewedPoolAlwaysFillsEverySlot()
    {
        var games = Enumerable.Range(0, 200).Select(_ => new Game { Id = Guid.NewGuid(), Name = "Test" }).ToList();
        var activities = games.Select(Activity).Concat(Enumerable.Range(0, 200).Select(_ => Activity(games[0]))).ToList();
        var result = generator.Generate(games, activities, new() { Count = activities.Count, UniqueGamesOnly = false });
        Assert.IsNotNull(result);
        Assert.AreEqual(activities.Count, result.Count);
        Assert.AreEqual(result.Count, result.Select(s => s.Activity.Id).Distinct().Count());
    }

    [TestMethod]
    public void FiltersIntersectAndOrphansCannotFillRequest()
    {
        var theme = Guid.NewGuid();
        var holiday = Guid.NewGuid();
        var eligible = Activity(first);
        eligible.ThemeIds.Add(theme);
        eligible.HolidayIds.Add(holiday);
        var request = new GenerateEventRequest { Count = 1, UniqueGamesOnly = false, ThemedOnly = true,
            SelectedGameIds = [first.Id], SelectedThemeIds = [theme], SelectedHolidayIds = [holiday] };
        var activities = new[] { eligible, Activity(first), Activity(second), Activity(new Game { Id = Guid.NewGuid(), Name = "Test" }) };
        Assert.AreEqual(eligible.Id, Assert.ContainsSingle(generator.Generate([first, second], activities, request)!).Activity.Id);
        request = request with { Count = 2 };
        Assert.IsNull(generator.Generate([first, second], activities, request));
        Assert.IsNull(generator.Generate([], activities, new() { Count = 1, UniqueGamesOnly = false }));
        eligible.ThemeIds.Clear();
        request = request with { Count = 1 };
        Assert.IsNull(generator.Generate([first, second], activities, request));
        request.SelectedThemeIds.Clear();
        Assert.IsNotNull(generator.Generate([first, second], activities, request)); // Holiday-only is themed.
    }
}
