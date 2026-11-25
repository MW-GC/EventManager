using Microsoft.AspNetCore.Http;
using MW_GC.EventManager.API.Functions;
using static MW_GC.EventManager.Api.Tests.Series.SeriesHarness;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// The Series validation rules (#204), through <c>POST /api/series</c> and <c>PUT /api/series/{id}</c>: each
/// refused body answers 400 with the rule's message and writes nothing.
/// </summary>
[TestClass]
public sealed class SeriesValidationTests
{
    private readonly SeriesHarness h = new();

    public static IEnumerable<object[]> Refused =>
    [
        [Body(name: "   "), "Name is required."],
        [Body(name: new string('x', 101)), "Name must be 100 characters or fewer."],
        [Body(first: "0001-01-01T00:00:00+00:00"), "Start is required."],
        [Body(end: "2027-03-04T19:00:00-05:00"), "End must be after the start."],
        [Body(end: "2027-03-04T18:59:59-05:00"), "End must be after the start."],
        [Body(slotCount: 0), "Slot count must be from 1 to 5."],
        [Body(slotCount: 6), "Slot count must be from 1 to 5."],
        [Body(rule: new { kind = "Daily" }), "Repeat must be a known rule."],
        [Body(rule: new { kind = "AnnualHoliday", preset = "Groundhog Day" }), "Holiday preset must be a known preset."],
        [Body(rule: new { kind = "AnnualHoliday" }), "Holiday preset must be a known preset."],
        [Body(rule: new { kind = "MonthlyNthWeekday", week = "Fifth", weekday = "Thursday" }), "Week of month must be First, Second, Third, Fourth or Last."],
        [Body(rule: new { kind = "MonthlyNthWeekday", week = 5, weekday = "Thursday" }), "Week of month must be First, Second, Third, Fourth or Last."],
        [Body(rule: new { kind = "MonthlyNthWeekday", week = "Second", weekday = "Funday" }), "Weekday must be a day of the week."],
        [Body(rule: new { kind = "MonthlyNthWeekday", week = "Second", weekday = 7 }), "Weekday must be a day of the week."],
        [Body(rule: new { kind = "MonthlyNthWeekday", week = "Second" }), "Weekday must be a day of the week."],
        // A comma list (which the enum parser would OR into BiWeekly) and a number written as a string.
        [Body(rule: new { kind = "Weekly,BiWeekly" }), "Repeat must be a known rule."],
        [Body(rule: new { kind = "3" }), "Repeat must be a known rule."],
        // Dates the generator cannot step from: the start and the end must be in the years 2000 to 2100.
        [Body(first: "9999-12-30T19:00:00-05:00"), "Start must be in the years 2000 to 2100."],
        [Body(first: "9999-12-30T19:00:00-05:00", rule: new { kind = "MonthlySameDate" }), "Start must be in the years 2000 to 2100."],
        [Body(first: "9999-12-30T19:00:00-05:00", rule: new { kind = "MonthlyNthWeekday", week = "Last", weekday = "Thursday" }), "Start must be in the years 2000 to 2100."],
        [Body(first: "9999-12-30T19:00:00-05:00", rule: new { kind = "AnnualSameDate" }), "Start must be in the years 2000 to 2100."],
        [Body(first: "9999-12-30T19:00:00-05:00", rule: new { kind = "AnnualHoliday", preset = "NewYearsEve" }), "Start must be in the years 2000 to 2100."],
        [Body(first: "2101-01-01T00:00:00+00:00"), "Start must be in the years 2000 to 2100."],
        [Body(first: "1999-12-31T19:00:00-05:00"), "Start must be in the years 2000 to 2100."],
        [Body(end: "9999-12-31T00:00:00-05:00"), "End must be in the years 2000 to 2100."],
        [Body(end: "2101-01-01T00:00:00+00:00"), "End must be in the years 2000 to 2100."],
        // After the start as an instant, but in 1999 at the offset it is written in.
        [Body(first: "2000-01-01T00:00:00+00:00", end: "1999-12-31T23:30:00-05:00"), "End must be in the years 2000 to 2100."],
    ];

    [TestMethod]
    [DynamicData(nameof(Refused))]
    public async Task A_refused_create_names_the_rule_and_writes_nothing(object body, string message)
    {
        var result = await h.SeriesApi.Create(Request(body, key: Guid.NewGuid()), default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual(message, ApiResults.Message(result));
        Assert.IsEmpty(h.SeriesRows);
        Assert.IsEmpty(h.Events);
    }

    [TestMethod]
    [DynamicData(nameof(Refused))]
    public async Task A_refused_edit_names_the_rule_and_writes_nothing(object body, string message)
    {
        var (id, _, _) = await h.CreateAsync();
        var writes = Writes();

        var result = await h.SeriesApi.Update(Request(body, query: "?scope=all"), id, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual(message, ApiResults.Message(result));
        Assert.AreEqual(writes, Writes());
        Assert.AreEqual("Thursday night", (await h.SeriesAsync(id))!.Name);
    }

    // Insert, upsert, update and delete calls that reached the Series or Events table.
    private int Writes() => new[] { h.SeriesTable, h.EventTable }.Sum(t => t.Invocations.Count(i => i.Method.Name is
        nameof(Azure.Data.Tables.TableClient.AddEntityAsync) or nameof(Azure.Data.Tables.TableClient.UpsertEntityAsync)
        or nameof(Azure.Data.Tables.TableClient.UpdateEntityAsync) or nameof(Azure.Data.Tables.TableClient.DeleteEntityAsync)));

    [TestMethod]
    public async Task Valid_rules_of_every_kind_are_stored_by_name()
    {
        object[] rules =
        [
            new { kind = "Weekly" }, new { kind = "biweekly" }, new { kind = "MonthlySameDate" }, new { kind = "AnnualSameDate" },
            new { kind = "MonthlyNthWeekday", week = "Last", weekday = "Friday" },
            new { kind = "AnnualHoliday", preset = "Thanksgiving" },
        ];
        foreach (var rule in rules)
            Assert.AreEqual(StatusCodes.Status201Created,
                ApiResults.Status(await h.SeriesApi.Create(Request(Body(rule: rule), key: Guid.NewGuid()), default)), rule.ToString());

        var stored = h.SeriesRows.Values.Select(r => r.GetString("Schedule")).ToList();
        Assert.IsTrue(stored.Any(s => s.Contains("\"preset\":\"Thanksgiving\"", StringComparison.Ordinal)), string.Join("\n", stored));
        Assert.IsTrue(stored.Any(s => s.Contains("\"week\":\"Last\",\"weekday\":\"Friday\"", StringComparison.Ordinal)), string.Join("\n", stored));
        Assert.IsTrue(stored.Any(s => s.Contains("\"kind\":\"BiWeekly\"", StringComparison.Ordinal)), string.Join("\n", stored));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("?scope=this")]
    [DataRow("?scope=everything")]
    public async Task An_edit_needs_a_Series_scope(string query)
    {
        var (id, _, _) = await h.CreateAsync();

        var result = await h.SeriesApi.Update(Request(Body(name: "Changed"), query: query), id, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual("Scope must be all or following. Edit one Occurrence through PUT /api/events/{id}.", ApiResults.Message(result));
        Assert.AreEqual("Thursday night", (await h.SeriesAsync(id))!.Name);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("?scope=this")]
    [DataRow("?scope=following")]
    public async Task A_cancel_needs_the_all_scope(string query)
    {
        var (id, _, _) = await h.CreateAsync();

        var result = await h.SeriesApi.Delete(Bare(query: query), id, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual("Scope must be all. Cancel one Occurrence through DELETE /api/events/{id}.", ApiResults.Message(result));
        Assert.IsNotNull(await h.SeriesAsync(id));
        Assert.HasCount(8, h.Events);
    }
}
