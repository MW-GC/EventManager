using System.Linq.Expressions;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MW_GC.EventManager.API.Functions;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Shared.Requests;
using Xunit;

namespace MW_GC.EventManager.Tests;

public class SingleActivityApiTests
{
    private readonly Dictionary<string, TableEntity> rows = [];
    private readonly List<GameEntity> gameRows = [];
    private readonly List<TableEntity> activityRows = [];
    private readonly EventFunctions functions;
    private readonly Game game = new() { Id = Guid.NewGuid(), Name = "Game" };
    private readonly Activity activity;

    public SingleActivityApiTests()
    {
        activity = new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Activity" };
        var table = new Mock<TableClient>();
        table.Setup(t => t.UpsertEntityAsync(It.IsAny<TableEntity>(), TableUpdateMode.Replace, It.IsAny<CancellationToken>()))
            .Callback<TableEntity, TableUpdateMode, CancellationToken>((row, _, _) => rows[row.RowKey] = new TableEntity(row))
            .ReturnsAsync(Mock.Of<Response>());
        table.Setup(t => t.GetEntityAsync<TableEntity>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string id, IEnumerable<string> _, CancellationToken _) => Response.FromValue(rows[id], Mock.Of<Response>()));
        table.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<TableEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(() => AsyncPageable<TableEntity>.FromPages([Page<TableEntity>.FromValues(rows.Values.ToList(), null, Mock.Of<Response>())]));
        var service = new Mock<TableServiceClient>();
        service.Setup(s => s.GetTableClient(It.IsAny<string>())).Returns(table.Object);
        var games = new Mock<TableClient>();
        gameRows.Add(new GameEntity { Id = game.Id });
        games.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<GameEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(() => AsyncPageable<GameEntity>.FromPages([Page<GameEntity>.FromValues(gameRows, null, Mock.Of<Response>())]));
        var activities = new Mock<TableClient>();
        activityRows.Add(new TableEntity("activities", activity.Id.ToString()) { ["GameId"] = game.Id });
        activities.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<TableEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(() => AsyncPageable<TableEntity>.FromPages([Page<TableEntity>.FromValues(activityRows, null, Mock.Of<Response>())]));
        service.Setup(s => s.GetTableClient("games")).Returns(games.Object);
        service.Setup(s => s.GetTableClient("activities")).Returns(activities.Object);
        functions = new EventFunctions(new(service.Object, "events", "events"), new(service.Object, "games", "games"), new(service.Object, "activities", "activities"), new());
    }

    private EventEntity Event(params Activity[] activities) => new()
    {
        Name = "Single roll", Date = new DateTimeOffset(2026, 9, 21, 18, 0, 0, TimeSpan.Zero), UniqueGamesOnly = false,
        Selections = activities.Select(a => new Selection { Game = game, Activity = a }).ToList()
    };

    private static HttpRequest Request<T>(T value)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddOptions().BuildServiceProvider();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(value));
        return context.Request;
    }

    private async Task<EventEntity> Read(Guid id) => Assert.IsType<EventEntity>(Assert.IsType<OkObjectResult>(await functions.Get(Request(new { }), id, default)).Value);

    private async Task AssertPersisted(EventEntity expected)
    {
        var row = rows[expected.Id.ToString()];
        Assert.Equal(expected.WinnerActivityId, row.TryGetValue("WinnerActivityId", out var winner) ? winner : null);
        var detail = await Read(expected.Id);
        var listed = Assert.IsType<List<EventEntity>>(Assert.IsType<OkObjectResult>(await functions.GetAll(Request(new { }), default)).Value);
        foreach (var actual in new[] { detail, Assert.Single(listed) })
        {
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.Date, actual.Date);
            Assert.Equal(expected.UniqueGamesOnly, actual.UniqueGamesOnly);
            Assert.Equal(expected.Selections.Select(s => s.Activity.Id), actual.Selections.Select(s => s.Activity.Id));
            Assert.Equal(expected.WinnerActivityId, actual.WinnerActivityId);
        }
    }

    private async Task<EventEntity> Update(EventEntity input)
    {
        // A rejected edit must fail here, not masquerade as a winner-storage regression.
        var result = Assert.IsType<OkObjectResult>(await functions.Update(Request(input), input.Id, default));
        var updated = Assert.IsType<EventEntity>(result.Value);
        await AssertPersisted(updated);
        return updated;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GenerateOnePersistsWinner(bool unique)
    {
        var result = Assert.IsType<CreatedResult>(await functions.Generate(Request(new GenerateEventRequest { Count = 1, UniqueGamesOnly = unique }), default));
        var saved = Assert.IsType<EventEntity>(result.Value);
        Assert.Single(saved.Selections);
        Assert.Equal(unique, saved.UniqueGamesOnly);
        Assert.Equal(activity.Id, saved.WinnerActivityId);
        await AssertPersisted(saved);
        Assert.Equal(activity.Id, (await Read(saved.Id)).WinnerActivityId);
        Assert.Single((await Read(saved.Id)).Selections);
        var listed = Assert.IsType<List<EventEntity>>(Assert.IsType<OkObjectResult>(await functions.GetAll(Request(new { }), default)).Value);
        Assert.Equal(activity.Id, Assert.Single(listed).WinnerActivityId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CustomizedSaveOverridesStaleWinnerAndRoundTripsStorage(bool unique)
    {
        var input = Event(activity);
        input.UniqueGamesOnly = unique;
        input.WinnerActivityId = Guid.NewGuid();
        var saved = Assert.IsType<EventEntity>(Assert.IsType<CreatedResult>(await functions.SaveCustomized(Request(input), default)).Value);
        Assert.Equal(activity.Id, saved.WinnerActivityId);
        await AssertPersisted(saved);
        Assert.NotEqual(Guid.Empty, saved.Id);
        Assert.Equal(activity.Id, rows[saved.Id.ToString()]["WinnerActivityId"]);
        var read = await Read(saved.Id);
        Assert.Equal(activity.Id, read.WinnerActivityId);
        Assert.Equal(activity.Id, Assert.Single(read.Selections).Activity.Id);
    }

    [Fact]
    public async Task EditingReplacesSingleWinnerPreservesValidMultiWinnerAndClearsRemovedWinner()
    {
        var saved = Assert.IsType<EventEntity>(Assert.IsType<CreatedResult>(await functions.SaveCustomized(Request(Event(activity)), default)).Value);
        var replacement = new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Other" };
        saved.Selections = Event(replacement).Selections;
        saved = await Update(saved);
        saved = await Read(saved.Id);
        Assert.Equal(replacement.Id, saved.WinnerActivityId);
        saved.Selections.AddRange(Event(activity).Selections);
        saved = await Update(saved);
        Assert.Equal(replacement.Id, (await Read(saved.Id)).WinnerActivityId);
        saved.Selections.RemoveAt(0);
        saved.Selections.AddRange(Event(new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Other" }).Selections);
        saved = await Update(saved);
        Assert.Null((await Read(saved.Id)).WinnerActivityId);
    }

    [Fact]
    public async Task MultipleSelectionsDoNotAutomaticallyChooseWinner()
    {
        var input = Event(activity, new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Other" });
        var saved = Assert.IsType<EventEntity>(Assert.IsType<CreatedResult>(await functions.SaveCustomized(Request(input), default)).Value);
        Assert.Null((await Read(saved.Id)).WinnerActivityId);
    }

    [Fact]
    public async Task SelectWinnerChoosesAndPersistsOneOfMultipleSelections()
    {
        var second = new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Other" };
        var input = Event(activity, second);
        var saved = Assert.IsType<EventEntity>(Assert.IsType<CreatedResult>(await functions.SaveCustomized(Request(input), default)).Value);

        var selected = Assert.IsType<OkObjectResult>(await functions.SelectWinner(Request(new { }), saved.Id, default));
        var result = Assert.IsType<EventEntity>(selected.Value);
        Assert.NotNull(result.WinnerActivityId);
        Assert.Contains(result.WinnerActivityId.Value, result.Selections.Select(s => s.Activity.Id));
        Assert.Equal(result.WinnerActivityId, rows[saved.Id.ToString()]["WinnerActivityId"]);
        await AssertPersisted(result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReducingMultipleSelectionsToOneReplacesRemovedOrMissingWinner(bool hadWinner)
    {
        var removed = new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Removed" };
        var input = Event(activity, removed);
        input.WinnerActivityId = hadWinner ? removed.Id : null;
        var saved = Assert.IsType<EventEntity>(Assert.IsType<CreatedResult>(await functions.SaveCustomized(Request(input), default)).Value);
        saved.Selections.RemoveAt(1);
        saved.Name = "Reduced event";
        saved = await Update(saved);
        Assert.Single(saved.Selections);
        Assert.Equal(activity.Id, saved.WinnerActivityId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OneSlotRespectsAllFiltersAndEmptyPools(bool unique)
    {
        var theme = Guid.NewGuid();
        var holiday = Guid.NewGuid();
        activity.ThemeIds.Add(theme);
        activity.HolidayIds.Add(holiday);
        var request = new GenerateEventRequest { Count = 1, UniqueGamesOnly = unique, ThemedOnly = true, SelectedGameIds = [game.Id], SelectedThemeIds = [theme], SelectedHolidayIds = [holiday] };
        var generator = new EventGenerator();
        Assert.Equal(activity.Id, Assert.Single(generator.Generate([game], [activity], request)!).Activity.Id);
        request.SelectedGameIds[0] = Guid.NewGuid();
        Assert.Null(generator.Generate([game], [activity], request));
        request.SelectedGameIds[0] = game.Id;
        request.SelectedThemeIds[0] = Guid.NewGuid();
        Assert.Null(generator.Generate([game], [activity], request));
        request.SelectedThemeIds[0] = theme;
        request.SelectedHolidayIds[0] = Guid.NewGuid();
        Assert.Null(generator.Generate([game], [activity], request));
        request.SelectedHolidayIds.Clear();
        request.SelectedThemeIds.Clear();
        activity.ThemeIds.Clear();
        activity.HolidayIds.Clear();
        Assert.Null(generator.Generate([game], [activity], request));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(int.MaxValue)]
    public async Task InvalidCountsAreRejectedWithoutWriting(int count)
    {
        var request = new GenerateEventRequest { Count = count };
        Assert.IsType<BadRequestObjectResult>(await functions.Generate(Request(request), default));
        Assert.Null(new EventGenerator().Generate([game], [activity], request));
        Assert.Empty(rows);
    }

    [Fact]
    public async Task OverLimitCountIsRejectedEvenWithEnoughEligibleInventory()
    {
        var games = Enumerable.Range(0, 6).Select(_ => new GameEntity { Id = Guid.NewGuid() }).ToList();
        var activities = games.Select(g => new TableEntity("activities", Guid.NewGuid().ToString()) { ["GameId"] = g.Id }).ToList();
        gameRows.AddRange(games);
        activityRows.AddRange(activities);

        var result = Assert.IsType<BadRequestObjectResult>(await functions.Generate(Request(new GenerateEventRequest { Count = 6 }), default));
        Assert.Contains("between 1 and 5", result.Value?.ToString());
        Assert.Empty(rows);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"count\":\"one\"}")]
    [InlineData("{\"selectedGameIds\":null}")]
    [InlineData("{\"selectedThemeIds\":null}")]
    [InlineData("{\"selectedHolidayIds\":null}")]
    public async Task InvalidGenerationBodiesAreBadRequests(string json)
    {
        var request = Request(new { });
        request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var result = Assert.IsAssignableFrom<Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult>(await functions.Generate(request, default));
        Assert.Equal(400, result.StatusCode);
        Assert.Empty(rows);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("tooMany")]
    [InlineData("nullSelections")]
    [InlineData("nullSelection")]
    [InlineData("nullActivity")]
    [InlineData("nullGame")]
    [InlineData("emptyActivityId")]
    [InlineData("emptyGameId")]
    [InlineData("mismatchedGame")]
    [InlineData("duplicateActivity")]
    [InlineData("duplicateGame")]
    public async Task InvalidCustomizedCreateAndUpdateDoNotWrite(string invalid)
    {
        var saved = Assert.IsType<EventEntity>(Assert.IsType<CreatedResult>(await functions.SaveCustomized(Request(Event(activity)), default)).Value);
        var original = rows[saved.Id.ToString()];
        var input = Event(activity);
        switch (invalid)
        {
            case "empty": input.Selections.Clear(); break;
            case "tooMany": input.Selections = Enumerable.Range(0, 6).Select(_ => new Selection { Game = game, Activity = new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Other" } }).ToList(); break;
            case "nullSelections": input.Selections = null!; break;
            case "nullSelection": input.Selections = [null!]; break;
            case "nullActivity": input.Selections = [new Selection { Game = game, Activity = null! }]; break;
            case "nullGame": input.Selections = [new Selection { Game = null!, Activity = activity }]; break;
            case "emptyActivityId": input.Selections = Event(activity with { Id = Guid.Empty }).Selections; break;
            case "emptyGameId": input.Selections = [new Selection { Game = game with { Id = Guid.Empty }, Activity = activity with { GameId = Guid.Empty } }]; break;
            case "mismatchedGame": input.Selections = Event(activity with { GameId = Guid.NewGuid() }).Selections; break;
            case "duplicateActivity": input.Selections.Add(input.Selections[0]); break;
            case "duplicateGame": input.UniqueGamesOnly = true; input.Selections.AddRange(Event(new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Other" }).Selections); break;
        }
        Assert.IsType<BadRequestObjectResult>(await functions.SaveCustomized(Request(input), default));
        Assert.IsType<BadRequestObjectResult>(await functions.Update(Request(input), saved.Id, default));
        Assert.Single(rows);
        Assert.Same(original, rows[saved.Id.ToString()]);
        Assert.Equal(activity.Id, (await Read(saved.Id)).WinnerActivityId);
    }

    [Fact]
    public async Task FiveSelectionsAndExplicitWinnerRoundTripThroughBothReads()
    {
        var input = Event(Enumerable.Range(0, 5).Select(_ => new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Other" }).ToArray());
        input.WinnerActivityId = input.Selections[2].Activity.Id;
        var saved = Assert.IsType<EventEntity>(Assert.IsType<CreatedResult>(await functions.SaveCustomized(Request(input), default)).Value);
        Assert.Equal(input.WinnerActivityId, (await Read(saved.Id)).WinnerActivityId);
        var list = Assert.IsType<List<EventEntity>>(Assert.IsType<OkObjectResult>(await functions.GetAll(Request(new { }), default)).Value);
        Assert.Equal(input.WinnerActivityId, Assert.Single(list).WinnerActivityId);
        saved.WinnerActivityId = saved.Selections[4].Activity.Id;
        var explicitWinner = saved.WinnerActivityId;
        saved = await Update(saved);
        Assert.Equal(explicitWinner, saved.WinnerActivityId);
        saved.WinnerActivityId = null;
        saved = await Update(saved);
        Assert.Null((await Read(saved.Id)).WinnerActivityId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GeneratorAcceptsUpperBoundaryWithoutRepeatingActivities(bool unique)
    {
        var games = Enumerable.Range(0, 5).Select(i => new Game { Id = Guid.NewGuid(), Name = $"Game {i}" }).ToList();
        // With repeated games allowed, use one game so random retry exhaustion cannot flake.
        if (!unique) games = [games[0]];
        var activities = Enumerable.Range(0, 5).Select(i => new Activity { Id = Guid.NewGuid(), GameId = games[unique ? i : 0].Id, Name = "Activity" }).ToList();
        var selections = new EventGenerator().Generate(games, activities, new GenerateEventRequest { Count = 5, UniqueGamesOnly = unique });
        Assert.NotNull(selections);
        Assert.Equal(5, selections.Count);
        Assert.Equal(5, selections.Select(s => s.Activity.Id).Distinct().Count());
        if (unique) Assert.Equal(5, selections.Select(s => s.Game.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    public async Task InvalidCustomizedBodiesDoNotOverwriteSavedEvent(string json)
    {
        var saved = Assert.IsType<EventEntity>(Assert.IsType<CreatedResult>(await functions.SaveCustomized(Request(Event(activity)), default)).Value);
        HttpRequest Body()
        {
            var request = Request(new { });
            request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            return request;
        }
        Assert.IsType<BadRequestResult>(await functions.SaveCustomized(Body(), default));
        Assert.IsType<BadRequestResult>(await functions.Update(Body(), saved.Id, default));
        Assert.Single(rows);
        Assert.Equal(activity.Id, (await Read(saved.Id)).WinnerActivityId);
    }

    [Fact]
    public void MultiSlotUniqueGamesAndDuplicateActivityConstraintsRemain()
    {
        var other = new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Other" };
        var generator = new EventGenerator();
        var request = new GenerateEventRequest { Count = 2, UniqueGamesOnly = true };
        Assert.Null(generator.Generate([game], [activity, other], request));
        request = new GenerateEventRequest { Count = 2, UniqueGamesOnly = false };
        var selections = generator.Generate([game], [activity, other], request)!;
        Assert.Equal(2, selections.Select(s => s.Activity.Id).Distinct().Count());
        Assert.Null(generator.Generate([game], [activity], request));
    }
}
