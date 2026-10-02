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

/// <summary>
/// Event generation with an injected clock and RNG: the default name follows the caller's
/// local offset, the winner pick is reproducible, a negative count behaves the same in both
/// modes, and <c>POST /api/events/generate</c> persists the Event it returns exactly once.
/// </summary>
public class EventGenerationClockAndRngTests
{
    // 00:30 UTC on Oct 2 is still Oct 1 for a caller at UTC-05:00.
    private static readonly DateTimeOffset Instant = new(2026, 10, 2, 0, 30, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    // Always draws the given index (clamped to the range asked for) and records each bound.
    private sealed class FixedIndexRandom(int index) : Random
    {
        public List<int> Bounds { get; } = [];
        public override int Next(int maxValue)
        {
            Bounds.Add(maxValue);
            return Math.Min(index, maxValue - 1);
        }
    }

    private static readonly Game[] Games = Enumerable.Range(1, 3).Select(i => new Game { Id = Guid.NewGuid(), Name = $"Game {i}" }).ToArray();

    private static List<Selection> Selections() => Games
        .Select(g => new Selection { Game = g, Activity = new Activity { Id = Guid.NewGuid(), GameId = g.Id, Name = $"{g.Name} activity" } })
        .ToList();

    [Theory]
    [InlineData(-300, "Event - Oct 1, 7:30 PM")]   // UTC-05:00: the caller's evening of Oct 1.
    [InlineData(null, "Event - Oct 2, 12:30 AM")]  // No offset: UTC, as before.
    [InlineData(0, "Event - Oct 2, 12:30 AM")]
    [InlineData(540, "Event - Oct 2, 9:30 AM")]     // UTC+09:00.
    [InlineData(330, "Event - Oct 2, 6:00 AM")]     // UTC+05:30, a non-whole-hour offset.
    [InlineData(-840, "Event - Oct 1, 10:30 AM")]  // Lower bound, UTC-14:00.
    [InlineData(840, "Event - Oct 2, 2:30 PM")]     // Upper bound, UTC+14:00.
    public void NameUsesTheCallersLocalTimeFromTheInjectedClock(int? offset, string expected)
    {
        var generator = new EventGenerator(new Random(1), new FixedTimeProvider(Instant));

        Assert.Equal(Instant, generator.UtcNow);
        Assert.Equal(expected, generator.GenerateName(offset));
        Assert.Equal(expected, generator.GenerateName(Instant, offset));
    }

    [Fact]
    public void DependencyInjectionHandsTheRegisteredTimeProviderToTheGenerator()
    {
        // Program.cs registers TimeProvider.System next to EventGenerator; a registered fake must
        // reach the generator the same way.
        using var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(new FixedTimeProvider(Instant))
            .AddSingleton<EventGenerator>()
            .BuildServiceProvider();

        var generator = services.GetRequiredService<EventGenerator>();

        Assert.Equal(Instant, generator.UtcNow);
        Assert.Equal("Event - Oct 1, 7:30 PM", generator.GenerateName(-300));
    }

    [Fact]
    public void SeededRandomDeterminesTheWinnerPick()
    {
        var selections = Selections();
        var generator = new EventGenerator(new Random(44), new FixedTimeProvider(Instant));

        // new Random(44).Next(3) yields 2, 2, 1, 2, 0.
        Assert.Same(selections[2], generator.PickWinner(selections));
        Assert.Same(selections[2], generator.PickWinner(selections));
        Assert.Same(selections[1], generator.PickWinner(selections));
        Assert.Same(selections[2], generator.PickWinner(selections));
        Assert.Same(selections[0], generator.PickWinner(selections));

        // The same seed replays the same picks, so the pick depends only on the injected RNG.
        var reference = new Random(44);
        var replay = new EventGenerator(new Random(44), new FixedTimeProvider(Instant));
        for (var i = 0; i < 20; i++)
            Assert.Same(selections[reference.Next(selections.Count)], replay.PickWinner(selections));
    }

    [Fact]
    public void WinnerPickDrawsOnceOverTheWholeSelectionList()
    {
        var selections = Selections();
        var random = new FixedIndexRandom(1);

        Assert.Same(selections[1], new EventGenerator(random, new FixedTimeProvider(Instant)).PickWinner(selections));
        Assert.Equal(3, Assert.Single(random.Bounds));
        Assert.Throws<ArgumentException>(() => new EventGenerator(random).PickWinner([]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-5)]
    [InlineData(int.MinValue)]
    public void NegativeCountReturnsNullInBothModesWithoutThrowing(int count)
    {
        var activities = Selections().Select(s => s.Activity).ToArray();
        var generator = new EventGenerator(new Random(3), new FixedTimeProvider(Instant));

        foreach (var games in new[] { Games, Array.Empty<Game>() })
        {
            var unique = generator.Generate(games, activities, new() { Count = count, UniqueGamesOnly = true });
            var repeated = generator.Generate(games, activities, new() { Count = count, UniqueGamesOnly = false });
            Assert.Null(unique);
            Assert.Null(repeated);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ZeroCountStillReturnsAnEmptyList(bool unique)
    {
        var activities = Selections().Select(s => s.Activity).ToArray();
        var result = new EventGenerator(new Random(3), new FixedTimeProvider(Instant)).Generate(Games, activities, new() { Count = 0, UniqueGamesOnly = unique });
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    // ---- HTTP functions over a mocked Table Storage client.

    private sealed class Harness
    {
        public Dictionary<string, TableEntity> Rows { get; } = [];
        public Mock<TableClient> Events { get; } = new();
        public EventFunctions Functions { get; }

        public Harness(EventGenerator generator)
        {
            Events.Setup(t => t.UpsertEntityAsync(It.IsAny<TableEntity>(), TableUpdateMode.Replace, It.IsAny<CancellationToken>()))
                .Callback<TableEntity, TableUpdateMode, CancellationToken>((row, _, _) => Rows[row.RowKey] = new TableEntity(row))
                .ReturnsAsync(Mock.Of<Response>());
            Events.Setup(t => t.GetEntityAsync<TableEntity>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string id, IEnumerable<string> _, CancellationToken _) => Response.FromValue(Rows[id], Mock.Of<Response>()));
            Events.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<TableEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .Returns(() => AsyncPageable<TableEntity>.FromPages([Page<TableEntity>.FromValues(Rows.Values.ToList(), null, Mock.Of<Response>())]));

            var games = new Mock<TableClient>();
            var gameRows = Games.Select(g => new GameEntity { Id = g.Id, Name = g.Name }).ToList();
            games.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<GameEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .Returns(() => AsyncPageable<GameEntity>.FromPages([Page<GameEntity>.FromValues(gameRows, null, Mock.Of<Response>())]));

            var activities = new Mock<TableClient>();
            var activityRows = Games.Select(g => new TableEntity("activities", Guid.NewGuid().ToString()) { ["GameId"] = g.Id, ["Name"] = g.Name + " activity" }).ToList();
            activities.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<TableEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .Returns(() => AsyncPageable<TableEntity>.FromPages([Page<TableEntity>.FromValues(activityRows, null, Mock.Of<Response>())]));

            var service = new Mock<TableServiceClient>();
            service.Setup(s => s.GetTableClient("events")).Returns(Events.Object);
            service.Setup(s => s.GetTableClient("games")).Returns(games.Object);
            service.Setup(s => s.GetTableClient("activities")).Returns(activities.Object);
            Functions = new EventFunctions(new(service.Object, "events", "events"), new(service.Object, "games", "games"), new(service.Object, "activities", "activities"), generator, Microsoft.Extensions.Logging.Abstractions.NullLogger<EventFunctions>.Instance);
        }

        public void VerifyUpserts(int times)
        {
            Events.Verify(t => t.UpsertEntityAsync(It.IsAny<TableEntity>(), TableUpdateMode.Replace, It.IsAny<CancellationToken>()), Times.Exactly(times));
            Events.Verify(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()), Times.Never());
        }
    }

    private static HttpRequest Request(string json)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddOptions().BuildServiceProvider();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        return context.Request;
    }

    private static HttpRequest Request<T>(T value) => Request(JsonSerializer.Serialize(value));

    [Fact]
    public async Task GenerateNamesTheEventForTheCallersLocalDateStoresTheUtcInstantAndPersistsItOnce()
    {
        var harness = new Harness(new EventGenerator(new Random(5), new FixedTimeProvider(Instant)));

        // The same camelCase body the browser and the run step send.
        var created = Assert.IsType<CreatedResult>(await harness.Functions.Generate(Request("{\"count\":2,\"utcOffsetMinutes\":-300}"), default));
        var generated = Assert.IsType<EventEntity>(created.Value);

        Assert.Equal("Event - Oct 1, 7:30 PM", generated.Name);
        Assert.Equal(Instant, generated.Date);
        Assert.Equal(TimeSpan.Zero, generated.Date.Offset);
        Assert.Equal($"/api/events/{generated.Id}", created.Location);
        Assert.Equal(2, generated.Selections.Count);

        // Decision: generate persists the Event it returns, with exactly one upsert.
        harness.VerifyUpserts(1);
        var row = Assert.Single(harness.Rows).Value;
        Assert.Equal(generated.Id.ToString(), row.RowKey);
        Assert.Equal("Event - Oct 1, 7:30 PM", row.GetString("Name"));
        Assert.Equal(Instant, row.GetDateTimeOffset("Date"));

        var stored = Assert.IsType<EventEntity>(Assert.IsType<OkObjectResult>(await harness.Functions.Get(Request("{}"), generated.Id, default)).Value);
        Assert.Equal(generated.Name, stored.Name);
        Assert.Equal(generated.Date, stored.Date);
        Assert.Equal(generated.Selections.Select(s => s.Activity.Id), stored.Selections.Select(s => s.Activity.Id));
        var listed = Assert.IsType<List<EventEntity>>(Assert.IsType<OkObjectResult>(await harness.Functions.GetAll(Request("{}"), default)).Value);
        Assert.Equal(generated.Id, Assert.Single(listed).Id);
    }

    [Theory]
    [InlineData("{\"count\":1}", "Event - Oct 2, 12:30 AM")]
    [InlineData("{\"count\":1,\"utcOffsetMinutes\":null}", "Event - Oct 2, 12:30 AM")]
    [InlineData("{\"count\":1,\"utcOffsetMinutes\":540}", "Event - Oct 2, 9:30 AM")]
    [InlineData("{\"count\":1,\"utcOffsetMinutes\":-840}", "Event - Oct 1, 10:30 AM")]
    [InlineData("{\"count\":1,\"utcOffsetMinutes\":840}", "Event - Oct 2, 2:30 PM")]
    public async Task GenerateAcceptsOffsetsWithinRangeAndDefaultsToUtc(string json, string expectedName)
    {
        var harness = new Harness(new EventGenerator(new Random(5), new FixedTimeProvider(Instant)));

        var generated = Assert.IsType<EventEntity>(Assert.IsType<CreatedResult>(await harness.Functions.Generate(Request(json), default)).Value);

        Assert.Equal(expectedName, generated.Name);
        Assert.Equal(Instant, generated.Date);
        harness.VerifyUpserts(1);
    }

    [Theory]
    [InlineData(841)]
    [InlineData(-841)]
    [InlineData(900)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public async Task OutOfRangeOffsetIsRejectedWithoutWriting(int offset)
    {
        var harness = new Harness(new EventGenerator(new Random(5), new FixedTimeProvider(Instant)));

        var result = Assert.IsType<BadRequestObjectResult>(await harness.Functions.Generate(Request(new GenerateEventRequest { Count = 2, UtcOffsetMinutes = offset }), default));

        Assert.Equal("UtcOffsetMinutes must be between -840 and 840.", Assert.IsType<string>(result.Value));
        harness.VerifyUpserts(0);
        Assert.Empty(harness.Rows);
    }

    [Fact]
    public async Task SelectWinnerUsesTheGeneratorsInjectedRandomAndPersistsTheWinner()
    {
        var random = new FixedIndexRandom(2);
        var harness = new Harness(new EventGenerator(random, new FixedTimeProvider(Instant)));
        var generated = Assert.IsType<EventEntity>(Assert.IsType<CreatedResult>(await harness.Functions.Generate(Request("{\"count\":3,\"uniqueGamesOnly\":true}"), default)).Value);
        Assert.Null(generated.WinnerActivityId);
        random.Bounds.Clear();

        var picked = Assert.IsType<EventEntity>(Assert.IsType<OkObjectResult>(await harness.Functions.SelectWinner(Request("{}"), generated.Id, default)).Value);

        Assert.Equal(3, Assert.Single(random.Bounds));
        Assert.Equal(picked.Selections[2].Activity.Id, picked.WinnerActivityId);
        Assert.Equal(picked.WinnerActivityId, harness.Rows[generated.Id.ToString()].GetGuid("WinnerActivityId"));
        harness.VerifyUpserts(2); // One for generate, one for the winner.
    }
}
