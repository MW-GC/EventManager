using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>The per-entity validators (#45) that both the create and the update route call.</summary>
[TestClass]
public class EntityValidatorTests
{
    private static readonly string Name100 = new('n', 100);
    private static readonly string Name101 = new('n', 101);

    public static IEnumerable<object?[]> BadNames =>
    [
        [null, "Name is required."],
        ["", "Name is required."],
        [" ", "Name is required."],
        ["\t\r\n ", "Name is required."],
        [Name101, "Name must be 100 characters or fewer."],
        ["  " + Name101 + "  ", "Name must be 100 characters or fewer."],
    ];

    [TestMethod]
    [DynamicData(nameof(BadNames))]
    public void EveryEntityRejectsTheSameBadNames(string? name, string expected)
    {
        Assert.AreEqual(expected, GameValidator.Validate(new GameEntity { Name = name! }));
        Assert.AreEqual(expected, ThemeValidator.Validate(new ThemeEntity { Name = name! }));
        Assert.AreEqual(expected, HolidayValidator.Validate(new HolidayEntity { Name = name! }));
        Assert.AreEqual(expected, ActivityValidator.Validate(new ActivityEntity { Name = name!, GameId = Guid.NewGuid() }));
        Assert.AreEqual(expected, EventValidator.Validate(new EventEntity { Name = name!, Selections = OneSelection() }));
    }

    [TestMethod]
    public void NamesAreTrimmedAndAHundredCharactersIsAllowed()
    {
        var game = new GameEntity { Name = "  Alpha \t" };
        var theme = new ThemeEntity { Name = " " + Name100 + " " };
        var holiday = new HolidayEntity { Name = "\nYule " };
        var activity = new ActivityEntity { Name = " Free-for-all ", GameId = Guid.NewGuid() };
        var evt = new EventEntity { Name = " Friday ", Selections = OneSelection() };

        Assert.IsNull(GameValidator.Validate(game));
        Assert.IsNull(ThemeValidator.Validate(theme));
        Assert.IsNull(HolidayValidator.Validate(holiday));
        Assert.IsNull(ActivityValidator.Validate(activity));
        Assert.IsNull(EventValidator.Validate(evt));

        Assert.AreEqual("Alpha", game.Name);
        Assert.AreEqual(Name100, theme.Name);
        Assert.AreEqual("Yule", holiday.Name);
        Assert.AreEqual("Free-for-all", activity.Name);
        Assert.AreEqual("Friday", evt.Name);
    }

    [TestMethod]
    [DataRow(nameof(GameEntity.Website))]
    [DataRow(nameof(GameEntity.ImageUrl))]
    [DataRow(nameof(GameEntity.IconUrl))]
    public void GameUrlsAreCappedByLengthOnly(string field)
    {
        GameEntity With(string value)
        {
            var game = new GameEntity { Name = "Alpha" };
            typeof(GameEntity).GetProperty(field)!.SetValue(game, value);
            return game;
        }

        Assert.IsNull(GameValidator.Validate(With(new string('u', 2048))));
        // Length only: no scheme check here (a later ticket owns that).
        Assert.IsNull(GameValidator.Validate(With("not even a url")));
        Assert.IsNull(GameValidator.Validate(With(null!)));
        Assert.AreEqual($"{field} must be 2048 characters or fewer.", GameValidator.Validate(With(new string('u', 2049))));
    }

    [TestMethod]
    [DataRow(nameof(ActivityEntity.Description))]
    [DataRow(nameof(ActivityEntity.Rules))]
    [DataRow(nameof(ActivityEntity.SetupRequirements))]
    [DataRow(nameof(ActivityEntity.Comments))]
    public void ActivityTextFieldsAreCappedAtTwoThousandAndNamed(string field)
    {
        ActivityEntity With(string? value)
        {
            var activity = new ActivityEntity { Name = "Act", GameId = Guid.NewGuid() };
            typeof(ActivityEntity).GetProperty(field)!.SetValue(activity, value);
            return activity;
        }

        Assert.IsNull(ActivityValidator.Validate(With(new string('t', 2000))));
        Assert.IsNull(ActivityValidator.Validate(With(null)));
        Assert.AreEqual($"{field} must be 2000 characters or fewer.", ActivityValidator.Validate(With(new string('t', 2001))));
    }

    [TestMethod]
    public void ActivityIdListsBecomeEmptyWhenNullAndAreDeduplicated()
    {
        var theme = Guid.NewGuid();
        var holiday = Guid.NewGuid();
        var nulls = new ActivityEntity { Name = "Act", GameId = Guid.NewGuid(), ThemeIds = null!, HolidayIds = null! };
        var dupes = new ActivityEntity { Name = "Act", GameId = Guid.NewGuid(), ThemeIds = [theme, theme], HolidayIds = [holiday, holiday, holiday] };

        Assert.IsNull(ActivityValidator.Validate(nulls));
        Assert.IsNull(ActivityValidator.Validate(dupes));

        Assert.IsNotNull(nulls.ThemeIds);
        Assert.IsEmpty(nulls.ThemeIds);
        Assert.IsNotNull(nulls.HolidayIds);
        Assert.IsEmpty(nulls.HolidayIds);
        Assert.AreSequenceEqual(new[] { theme }, dupes.ThemeIds);
        Assert.AreSequenceEqual(new[] { holiday }, dupes.HolidayIds);
    }

    [TestMethod]
    public void ActivityNeedsAGameId()
    {
        Assert.AreEqual("GameId is required.", ActivityValidator.Validate(new ActivityEntity { Name = "Act" }));
    }

    [TestMethod]
    public void EventKeepsTheExistingSelectionMessagesAfterTheNameRule()
    {
        Assert.AreEqual("Select between 1 and 5 activities.", EventValidator.Validate(new EventEntity { Name = "Friday", Selections = [] }));
        Assert.AreEqual("Name is required.", EventValidator.Validate(new EventEntity { Name = " ", Selections = [] }));
    }

    private static List<Selection> OneSelection()
    {
        var game = new Game { Id = Guid.NewGuid(), Name = "Alpha" };
        return [new Selection { Game = game, Activity = new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Act" } }];
    }
}
