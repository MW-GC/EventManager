using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using Xunit;

namespace MW_GC.EventManager.Tests;

/// <summary>The per-entity validators (#45) that both the create and the update route call.</summary>
public class EntityValidatorTests
{
    private static readonly string Name100 = new('n', 100);
    private static readonly string Name101 = new('n', 101);

    public static TheoryData<string?, string> BadNames => new()
    {
        { null, "Name is required." },
        { "", "Name is required." },
        { " ", "Name is required." },
        { "\t\r\n ", "Name is required." },
        { Name101, "Name must be 100 characters or fewer." },
        { "  " + Name101 + "  ", "Name must be 100 characters or fewer." },
    };

    [Theory]
    [MemberData(nameof(BadNames))]
    public void EveryEntityRejectsTheSameBadNames(string? name, string expected)
    {
        Assert.Equal(expected, GameValidator.Validate(new GameEntity { Name = name! }));
        Assert.Equal(expected, ThemeValidator.Validate(new ThemeEntity { Name = name! }));
        Assert.Equal(expected, HolidayValidator.Validate(new HolidayEntity { Name = name! }));
        Assert.Equal(expected, ActivityValidator.Validate(new ActivityEntity { Name = name!, GameId = Guid.NewGuid() }));
        Assert.Equal(expected, EventValidator.Validate(new EventEntity { Name = name!, Selections = OneSelection() }));
    }

    [Fact]
    public void NamesAreTrimmedAndAHundredCharactersIsAllowed()
    {
        var game = new GameEntity { Name = "  Alpha \t" };
        var theme = new ThemeEntity { Name = " " + Name100 + " " };
        var holiday = new HolidayEntity { Name = "\nYule " };
        var activity = new ActivityEntity { Name = " Free-for-all ", GameId = Guid.NewGuid() };
        var evt = new EventEntity { Name = " Friday ", Selections = OneSelection() };

        Assert.Null(GameValidator.Validate(game));
        Assert.Null(ThemeValidator.Validate(theme));
        Assert.Null(HolidayValidator.Validate(holiday));
        Assert.Null(ActivityValidator.Validate(activity));
        Assert.Null(EventValidator.Validate(evt));

        Assert.Equal("Alpha", game.Name);
        Assert.Equal(Name100, theme.Name);
        Assert.Equal("Yule", holiday.Name);
        Assert.Equal("Free-for-all", activity.Name);
        Assert.Equal("Friday", evt.Name);
    }

    [Theory]
    [InlineData(nameof(GameEntity.Website))]
    [InlineData(nameof(GameEntity.ImageUrl))]
    [InlineData(nameof(GameEntity.IconUrl))]
    public void GameUrlsAreCappedByLengthOnly(string field)
    {
        GameEntity With(string value)
        {
            var game = new GameEntity { Name = "Alpha" };
            typeof(GameEntity).GetProperty(field)!.SetValue(game, value);
            return game;
        }

        Assert.Null(GameValidator.Validate(With(new string('u', 2048))));
        // Length only: no scheme check here (a later ticket owns that).
        Assert.Null(GameValidator.Validate(With("not even a url")));
        Assert.Null(GameValidator.Validate(With(null!)));
        Assert.Equal($"{field} must be 2048 characters or fewer.", GameValidator.Validate(With(new string('u', 2049))));
    }

    [Theory]
    [InlineData(nameof(ActivityEntity.Description))]
    [InlineData(nameof(ActivityEntity.Rules))]
    [InlineData(nameof(ActivityEntity.SetupRequirements))]
    [InlineData(nameof(ActivityEntity.Comments))]
    public void ActivityTextFieldsAreCappedAtTwoThousandAndNamed(string field)
    {
        ActivityEntity With(string? value)
        {
            var activity = new ActivityEntity { Name = "Act", GameId = Guid.NewGuid() };
            typeof(ActivityEntity).GetProperty(field)!.SetValue(activity, value);
            return activity;
        }

        Assert.Null(ActivityValidator.Validate(With(new string('t', 2000))));
        Assert.Null(ActivityValidator.Validate(With(null)));
        Assert.Equal($"{field} must be 2000 characters or fewer.", ActivityValidator.Validate(With(new string('t', 2001))));
    }

    [Fact]
    public void ActivityIdListsBecomeEmptyWhenNullAndAreDeduplicated()
    {
        var theme = Guid.NewGuid();
        var holiday = Guid.NewGuid();
        var nulls = new ActivityEntity { Name = "Act", GameId = Guid.NewGuid(), ThemeIds = null!, HolidayIds = null! };
        var dupes = new ActivityEntity { Name = "Act", GameId = Guid.NewGuid(), ThemeIds = [theme, theme], HolidayIds = [holiday, holiday, holiday] };

        Assert.Null(ActivityValidator.Validate(nulls));
        Assert.Null(ActivityValidator.Validate(dupes));

        Assert.NotNull(nulls.ThemeIds);
        Assert.Empty(nulls.ThemeIds);
        Assert.NotNull(nulls.HolidayIds);
        Assert.Empty(nulls.HolidayIds);
        Assert.Equal(new[] { theme }, dupes.ThemeIds);
        Assert.Equal(new[] { holiday }, dupes.HolidayIds);
    }

    [Fact]
    public void ActivityNeedsAGameId()
    {
        Assert.Equal("GameId is required.", ActivityValidator.Validate(new ActivityEntity { Name = "Act" }));
    }

    [Fact]
    public void EventKeepsTheExistingSelectionMessagesAfterTheNameRule()
    {
        Assert.Equal("Select between 1 and 5 activities.", EventValidator.Validate(new EventEntity { Name = "Friday", Selections = [] }));
        Assert.Equal("Name is required.", EventValidator.Validate(new EventEntity { Name = " ", Selections = [] }));
    }

    private static List<Selection> OneSelection()
    {
        var game = new Game { Id = Guid.NewGuid(), Name = "Alpha" };
        return [new Selection { Game = game, Activity = new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Act" } }];
    }
}
