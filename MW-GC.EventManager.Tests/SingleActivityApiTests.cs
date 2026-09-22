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
        var service = new Mock<TableServiceClient>();
        service.Setup(s => s.GetTableClient(It.IsAny<string>())).Returns(table.Object);
        var games = new Mock<TableClient>();
        games.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<GameEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncPageable<GameEntity>.FromPages([Page<GameEntity>.FromValues([new GameEntity { Id = game.Id }], null, Mock.Of<Response>())]));
        var activities = new Mock<TableClient>();
        activities.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<TableEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncPageable<TableEntity>.FromPages([Page<TableEntity>.FromValues([new TableEntity("activities", activity.Id.ToString()) { ["GameId"] = game.Id }], null, Mock.Of<Response>())]));
        service.Setup(s => s.GetTableClient("games")).Returns(games.Object);
        service.Setup(s => s.GetTableClient("activities")).Returns(activities.Object);
        functions = new EventFunctions(new(service.Object, "events", "events"), new(service.Object, "games", "games"), new(service.Object, "activities", "activities"), new());
    }

    private EventEntity Event(params Activity[] activities) => new()
    {
        Name = "Single roll", Date = DateTimeOffset.UtcNow,
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GenerateOnePersistsWinner(bool unique)
    {
        var result = Assert.IsType<CreatedResult>(await functions.Generate(Request(new GenerateEventRequest { Count = 1, UniqueGamesOnly = unique }), default));
        var saved = Assert.IsType<EventEntity>(result.Value);
        Assert.Equal(activity.Id, saved.WinnerActivityId);
        Assert.Equal(activity.Id, (await Read(saved.Id)).WinnerActivityId);
        Assert.Single((await Read(saved.Id)).Selections);
    }

    [Fact]
    public async Task CustomizedSaveOverridesStaleWinnerAndRoundTripsStorage()
    {
        var input = Event(activity);
        input.WinnerActivityId = Guid.NewGuid();
        var saved = Assert.IsType<EventEntity>(Assert.IsType<CreatedResult>(await functions.SaveCustomized(Request(input), default)).Value);
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
        await functions.Update(Request(saved), saved.Id, default);
        saved = await Read(saved.Id);
        Assert.Equal(replacement.Id, saved.WinnerActivityId);
        saved.Selections.AddRange(Event(activity).Selections);
        await functions.Update(Request(saved), saved.Id, default);
        Assert.Equal(replacement.Id, (await Read(saved.Id)).WinnerActivityId);
        saved.Selections.RemoveAt(0);
        saved.Selections.AddRange(Event(new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Other" }).Selections);
        await functions.Update(Request(saved), saved.Id, default);
        Assert.Null((await Read(saved.Id)).WinnerActivityId);
    }

    [Fact]
    public async Task MultipleSelectionsDoNotAutomaticallyChooseWinner()
    {
        var input = Event(activity, new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Other" });
        var saved = Assert.IsType<EventEntity>(Assert.IsType<CreatedResult>(await functions.SaveCustomized(Request(input), default)).Value);
        Assert.Null((await Read(saved.Id)).WinnerActivityId);
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
