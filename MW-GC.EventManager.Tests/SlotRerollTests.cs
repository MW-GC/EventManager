using System.Collections;
using System.Reflection;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using Xunit;

namespace MW_GC.EventManager.Tests;

// Exercise the actual page handlers without a renderer or network services.
public class SlotRerollTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Events page = new();
    private readonly Guid gameA = Guid.NewGuid();
    private readonly Guid gameB = Guid.NewGuid();
    private readonly Guid theme = Guid.NewGuid();
    private readonly Guid holiday = Guid.NewGuid();
    private readonly ActivityEntity current;
    private readonly ActivityEntity other;
    private readonly ActivityEntity replacement;

    public SlotRerollTests()
    {
        current = Activity(gameA);
        other = Activity(gameB);
        replacement = Activity(gameA);
        replacement.ThemeIds = [theme];
        replacement.HolidayIds = [holiday];
        Set("_games", new List<GameEntity> { new() { Id = gameA }, new() { Id = gameB } });
        Set("_activities", new List<ActivityEntity> { current, other, replacement });
        AddSlot(current);
        AddSlot(other);
    }

    [Fact]
    public void RerollChangesOnlyTargetAndPreservesMetadata()
    {
        Set("_custName", "Keep this name");
        var date = new DateTime(2026, 9, 21);
        Set("_custDate", date);
        var originalOther = Slots[1];
        Call("RerollSlot", 0);
        Assert.Equal(replacement.Id, Id(Slots[0]!, "ActivityId"));
        Assert.Equal(gameA, Id(Slots[0]!, "GameId"));
        Assert.Same(originalOther, Slots[1]);
        Assert.Equal(2, Slots.Count);
        Assert.Equal("Keep this name", Get("_custName"));
        Assert.Equal(date, Get("_custDate"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NeverDuplicatesActivitiesOrReturnsCurrent(bool unique)
    {
        Set("_custUniqueGames", unique);
        Assert.Equal(new[] { replacement.Id }, Candidates().Select(a => a.Id));
    }

    [Fact]
    public void UniqueGamesExcludesGamesUsedByOtherSlots()
    {
        var alternative = Activity(gameB);
        Activities.Add(alternative);
        Assert.DoesNotContain(alternative, Candidates());
        Set("_custUniqueGames", false);
        Assert.Contains(alternative, Candidates());
    }

    [Theory]
    [InlineData("_custGameIds")]
    [InlineData("_custThemeIds")]
    [InlineData("_custHolidayIds")]
    public void SelectedFiltersAreApplied(string field)
    {
        Set(field, new List<Guid> { field == "_custGameIds" ? gameA : field == "_custThemeIds" ? theme : holiday });
        Assert.Single(Candidates());
        Set(field, new List<Guid> { Guid.NewGuid() });
        Assert.Empty(Candidates());
        var original = Slots[0];
        Call("RerollSlot", 0);
        Assert.Same(original, Slots[0]);
    }

    [Fact]
    public void ThemedOnlyAcceptsThemeOrHolidayAndRejectsUntagged()
    {
        var untagged = Activity(gameA);
        Activities.Add(untagged);
        Set("_custThemedOnly", true);
        Assert.DoesNotContain(untagged, Candidates());
        replacement.ThemeIds.Clear();
        Assert.Contains(replacement, Candidates());
        replacement.HolidayIds.Clear();
        Assert.Empty(Candidates());
    }

    [Fact]
    public void FiltersCombineRatherThanOverrideEachOther()
    {
        Set("_custGameIds", new List<Guid> { gameA });
        Set("_custThemeIds", new List<Guid> { theme });
        Set("_custHolidayIds", new List<Guid> { holiday });
        Assert.Single(Candidates());
        replacement.HolidayIds.Clear();
        Assert.Empty(Candidates());
    }

    [Fact]
    public void ExhaustedPoolIsNoOpAndIgnoresOrphanedActivities()
    {
        Activities.Remove(replacement);
        Activities.Add(Activity(Guid.NewGuid()));
        var original = Slots[0];
        Assert.Empty(Candidates());
        Call("RerollSlot", 0);
        Assert.Same(original, Slots[0]);
        Assert.Equal(2, Slots.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void InvalidIndexIsNoOp(int index)
    {
        Assert.Empty((List<ActivityEntity>)Call("GetRerollCandidates", index)!);
        Call("RerollSlot", index);
        Assert.Equal(current.Id, Id(Slots[0]!, "ActivityId"));
    }

    [Fact]
    public void RepeatedRollsKeepOtherSlotsAndStayInEligiblePool()
    {
        Activities.Add(Activity(gameA));
        var originalOther = Slots[1];
        for (var i = 0; i < 100; i++)
        {
            var before = Id(Slots[0]!, "ActivityId");
            var eligible = Candidates().Select(a => a.Id).ToList();
            Call("RerollSlot", 0);
            var after = Id(Slots[0]!, "ActivityId");
            Assert.NotEqual(before, after);
            Assert.Contains(after, eligible);
            Assert.Same(originalOther, Slots[1]);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RandomizeAllFillsSlotsAndRerollsKeepIdsUnique(bool unique)
    {
        Set("_custUniqueGames", unique);
        for (var i = 0; i < 100; i++)
        {
            Call("RandomizeAll");
            Assert.Equal(2, Slots.Count);
            Assert.Equal(2, Slots.Cast<object>().Select(s => Id(s, "ActivityId")).Distinct().Count());
            var otherSlot = Slots[1];
            Call("RerollSlot", 0);
            Assert.Same(otherSlot, Slots[1]);
            Assert.Equal(2, Slots.Cast<object>().Select(s => Id(s, "ActivityId")).Distinct().Count());
            Assert.Null(Get("_generationError"));
        }
    }

    [Fact]
    public void SameGameCanFillAllSlotsWithoutDuplicateIds()
    {
        Set("_custUniqueGames", false);
        Set("_custGameIds", new List<Guid> { gameA });
        Activities.Add(current); // Duplicate source records are not extra capacity.
        for (var i = 0; i < 100; i++)
        {
            Call("RandomizeAll");
            Assert.Equal(2, Slots.Count);
            Assert.All(Slots.Cast<object>(), s => Assert.Equal(gameA, Id(s, "GameId")));
            Assert.Equal(2, Slots.Cast<object>().Select(s => Id(s, "ActivityId")).Distinct().Count());
        }
        Call("AddSlot");
        Assert.Equal(2, Slots.Count);
    }

    [Theory]
    [InlineData("_custGameIds")]
    [InlineData("_custThemeIds")]
    [InlineData("_custHolidayIds")]
    public void ImpossibleRandomizePreservesSlotsAndReportsError(string filter)
    {
        var original = Slots;
        Set(filter, new List<Guid> { Guid.NewGuid() });
        Call("RandomizeAll");
        Assert.Same(original, Slots);
        Assert.Equal(2, Slots.Count);
        Assert.False(string.IsNullOrWhiteSpace((string?)Get("_generationError")));
        Set(filter, new List<Guid>());
        Call("RandomizeAll");
        Assert.Null(Get("_generationError"));
    }

    [Fact]
    public void InitialImpossibleGenerationShowsErrorRatherThanSilentlyBlank()
    {
        Slots.Clear();
        Activities.Clear();
        Activities.Add(Activity(Guid.NewGuid()));
        Call("RandomizeAll");
        Assert.Empty(Slots);
        Assert.False(string.IsNullOrWhiteSpace((string?)Get("_generationError")));
    }

    [Fact]
    public void RandomizeAppliesAllFiltersTogether()
    {
        Set("_custUniqueGames", false);
        current.ThemeIds = [theme];
        current.HolidayIds = [holiday];
        Activities.Add(Activity(gameA));
        Set("_custGameIds", new List<Guid> { gameA });
        Set("_custThemeIds", new List<Guid> { theme });
        Set("_custHolidayIds", new List<Guid> { holiday });
        Set("_custThemedOnly", true);
        Call("RandomizeAll");
        Assert.Equal(2, Slots.Count);
        Assert.All(Slots.Cast<object>(), s => Assert.Contains(Id(s, "ActivityId"), new[] { current.Id, replacement.Id }));
        Assert.Equal(2, Slots.Cast<object>().Select(s => Id(s, "ActivityId")).Distinct().Count());
        Assert.Null(Get("_generationError"));
    }

    [Fact]
    public void AddSlotSkipsExhaustedGames()
    {
        Set("_custUniqueGames", false);
        for (var i = 0; i < 100; i++)
        {
            Call("AddSlot");
            Assert.Equal(3, Slots.Count);
            Assert.Equal(replacement.Id, Id(Slots[2]!, "ActivityId"));
            Slots.RemoveAt(2);
        }
    }

    private static ActivityEntity Activity(Guid game) => new() { Id = Guid.NewGuid(), GameId = game };
    private IList Slots => (IList)Get("_custSelections")!;
    private List<ActivityEntity> Activities => (List<ActivityEntity>)Get("_activities")!;
    private List<ActivityEntity> Candidates() => (List<ActivityEntity>)Call("GetRerollCandidates", 0)!;
    private object? Get(string name) => typeof(Events).GetField(name, Private)!.GetValue(page);
    private void Set(string name, object value) => typeof(Events).GetField(name, Private)!.SetValue(page, value);
    private object? Call(string name, params object[] args) => typeof(Events).GetMethod(name, Private)!.Invoke(page, args);
    private static Guid Id(object slot, string name) => (Guid)slot.GetType().GetProperty(name)!.GetValue(slot)!;
    private void AddSlot(ActivityEntity activity)
    {
        var type = typeof(Events).GetNestedType("SlotSelection", BindingFlags.NonPublic)!;
        var slot = Activator.CreateInstance(type)!;
        type.GetProperty("GameId")!.SetValue(slot, activity.GameId);
        type.GetProperty("ActivityId")!.SetValue(slot, activity.Id);
        Slots.Add(slot);
    }
}
