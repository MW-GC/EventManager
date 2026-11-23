using System.Text.Json;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Web.Tests;

// #135: the Event metrics form checks each value with the API's own rule before it sends anything, and shows the
// API's own words under the field, tied to it for a screen reader.
[TestClass]
public sealed class EventMetricsValidationTests : EventMetricsFormFixture
{
    private const string CountMessage = "must be a whole number from 0 to 1000000.";
    private const string AmountMessage = "Amount spent must be between 0 and 1000000000 with at most 2 decimal places.";

    [TestMethod]
    [DataRow("Attendees", "-1", "Attendees must be a whole number from 0 to 1000000.")]
    [DataRow("Attendees", "2.5", "Attendees must be a whole number from 0 to 1000000.")]
    [DataRow("New attendees", "abc", "New attendees must be a whole number from 0 to 1000000.")]
    [DataRow("New attendees", "1000001", "New attendees must be a whole number from 0 to 1000000.")]
    [DataRow("Returning attendees", "-3", "Returning attendees must be a whole number from 0 to 1000000.")]
    [DataRow("Amount spent", "-0.01", "Amount spent must be between 0 and 1000000000 with at most 2 decimal places.")]
    [DataRow("Amount spent", "1.005", "Amount spent must be between 0 and 1000000000 with at most 2 decimal places.")]
    [DataRow("Amount spent", "1000000000.01", "Amount spent must be between 0 and 1000000000 with at most 2 decimal places.")]
    [DataRow("Amount spent", "ten", "Amount spent must be between 0 and 1000000000 with at most 2 decimal places.")]
    // #173 item 2: a comma (decimal or thousands) is refused, never read as another number.
    [DataRow("Amount spent", "40,5", "Amount spent must be between 0 and 1000000000 with at most 2 decimal places.")]
    [DataRow("Attendees", "1,000", "Attendees must be a whole number from 0 to 1000000.")]
    // #173 item 2: past int's range is refused, never wrapped round.
    [DataRow("Attendees", "2147483648", "Attendees must be a whole number from 0 to 1000000.")]
    [DataRow("Returning attendees", "99999999999", "Returning attendees must be a whole number from 0 to 1000000.")]
    public async Task AnInvalidValueShowsItsMessageUnderTheField_AndSendsNothing(string label, string typed, string message)
    {
        var page = await OpenDetails();

        await Type(page, label, typed);
        await SaveMetrics(page);

        Assert.AreEqual(message, Assert.ContainsSingle(FieldAlerts(page, label)));
        Assert.AreEqual(message, Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.AreEqual(0, api.Count(HttpMethod.Put, metricsPath));
        Assert.AreEqual(typed, Value(page, label));
    }

    // The form's messages are the API's own words: each is compared with EventMetricsValidator.
    [TestMethod]
    public async Task EveryFieldsMessageIsTheApis()
    {
        var page = await OpenDetails();
        var cases = new (string Label, string Typed, EventMetricsEntity Same)[]
        {
            ("Attendees", "-1", new() { Attendees = -1 }),
            ("New attendees", "1000001", new() { NewAttendees = 1_000_001 }),
            ("Returning attendees", "-5", new() { ReturningAttendees = -5 }),
            ("Amount spent", "1.005", new() { AmountSpent = 1.005m }),
        };
        foreach (var c in cases) await Type(page, c.Label, c.Typed);

        await SaveMetrics(page);

        foreach (var c in cases)
            Assert.AreEqual(EventMetricsValidator.Validate(c.Same), Assert.ContainsSingle(FieldAlerts(page, c.Label)), c.Label);
        Assert.AreEqual(0, api.Count(HttpMethod.Put, metricsPath));
    }

    // #173 item 6, #195 item 4: the form refuses exactly what the API refuses, with the API's words, at and past each
    // limit. The limits are written once, as literals, in this table (the API keeps its own copy in
    // EventMetricsValidator and the Web form in EventMetricsRules). A typed text the form reads as a number reaches
    // the API as that number (sent), and the API's rule gives the same verdict on it. Text the form cannot read as a
    // number never reaches the API, whose JSON reader would refuse it with its malformed-body 400; the form says
    // what the field takes instead, in its own words.
    [TestMethod]
    [DataRow("Attendees", "0", "0", null)]
    [DataRow("Attendees", "1000000", "1000000", null)]
    [DataRow("Attendees", "1000001", "1000001", "Attendees " + CountMessage)]
    [DataRow("New attendees", "-1", "-1", "New attendees " + CountMessage)]
    [DataRow("New attendees", "+12", "12", null)]
    [DataRow("Returning attendees", "+1000000", "1000000", null)]
    [DataRow("Returning attendees", "+1000001", "1000001", "Returning attendees " + CountMessage)]
    [DataRow("Attendees", "12 people", null, "Attendees " + CountMessage)]
    [DataRow("New attendees", "1e3", null, "New attendees " + CountMessage)]
    [DataRow("Amount spent", "0", "0", null)]
    [DataRow("Amount spent", "0.01", "0.01", null)]
    [DataRow("Amount spent", "1000000000", "1000000000", null)]
    [DataRow("Amount spent", "1000000000.01", "1000000000.01", AmountMessage)]
    [DataRow("Amount spent", "-0.01", "-0.01", AmountMessage)]
    [DataRow("Amount spent", "1.005", "1.005", AmountMessage)]
    [DataRow("Amount spent", "+40.5", "40.5", null)]
    [DataRow("Amount spent", "+1.005", "1.005", AmountMessage)]
    [DataRow("Amount spent", "$40", null, AmountMessage)]
    [DataRow("Amount spent", "4 0", null, AmountMessage)]
    public async Task TheFormsRulesAreTheApis_ValueForValue(string label, string typed, string? number, string? message)
    {
        var page = await OpenDetails();
        api.On(HttpMethod.Put, metricsPath, () => Row(1, null, null, null, FirstTag));
        var jsonName = Metrics.Single(m => m.Label == label).JsonName;

        await Type(page, label, typed);
        await SaveMetrics(page);

        if (message is null)
        {
            var body = Assert.ContainsSingle(api.Bodies(HttpMethod.Put, metricsPath))!;
            using (var sent = JsonDocument.Parse(body))
                Assert.AreEqual(number, sent.RootElement.GetProperty(jsonName).GetRawText(), "the number the API receives");
            Assert.IsNull(EventMetricsValidator.Validate(JsonSerializer.Deserialize<EventMetricsEntity>(body, JsonSerializerOptions.Web)!), "the API accepts it");
            Assert.IsEmpty(FieldAlerts(page, label));
            return;
        }
        Assert.AreEqual(0, api.Count(HttpMethod.Put, metricsPath), "a refused value sends nothing");
        Assert.AreEqual(message, Assert.ContainsSingle(FieldAlerts(page, label)));
        if (number is not null)
            Assert.AreEqual(message, EventMetricsValidator.Validate(
                JsonSerializer.Deserialize<EventMetricsEntity>($"{{\"{jsonName}\":{number}}}", JsonSerializerOptions.Web)!), "the API's own verdict");
    }

    // #173 item 9, #195 item 6: each message is tied to its field, so a screen reader reads it with the field. The
    // text field's input sits in a shadow root that no id reference (aria-describedby) can reach, so while a
    // message shows it is the field's accessible name, which the text field copies onto its input.
    [TestMethod]
    public async Task EveryFieldMessageIsTiedToItsField()
    {
        var page = await OpenDetails();
        foreach (var metric in Metrics)
        {
            Assert.IsNull(Field(page, metric.Label).GetAttribute("aria-invalid"), metric.Label);
            Assert.AreEqual(metric.Label, Field(page, metric.Label).GetAttribute("aria-label"), metric.Label);
        }
        await Type(page, "Attendees", "-1");
        await Type(page, "New attendees", "x");
        await Type(page, "Returning attendees", "1000001");
        await Type(page, "Amount spent", "1.005");

        await SaveMetrics(page);

        foreach (var metric in Metrics)
        {
            var field = Field(page, metric.Label);
            var message = Assert.ContainsSingle(FieldAlerts(page, metric.Label));
            Assert.AreEqual("true", field.GetAttribute("aria-invalid"), metric.Label);
            Assert.AreEqual(message, field.GetAttribute("aria-label"), metric.Label);
            Assert.StartsWith(metric.Label + " ", message, "the name still starts with the field's label");
            Assert.IsNull(field.GetAttribute("aria-describedby"), $"{metric.Label}: an id reference the input cannot resolve");
        }

        // A fixed field is valid again and is named by its label alone.
        await Type(page, "Attendees", "3");
        Assert.IsNull(Field(page, "Attendees").GetAttribute("aria-invalid"));
        Assert.AreEqual("Attendees", Field(page, "Attendees").GetAttribute("aria-label"));
        Assert.AreEqual("true", Field(page, "New attendees").GetAttribute("aria-invalid"));
    }

    [TestMethod]
    public async Task FixingTheValueClearsItsMessage_AndTheSaveGoesThrough()
    {
        var page = await OpenDetails();
        await Type(page, "Attendees", "-1");
        await SaveMetrics(page);
        Assert.ContainsSingle(FieldAlerts(page, "Attendees"));

        api.On(HttpMethod.Put, metricsPath, () => Row(1, null, null, null, FirstTag));
        await Type(page, "Attendees", "1");
        Assert.IsEmpty(FieldAlerts(page, "Attendees"));
        await SaveMetrics(page);

        Assert.AreEqual(1, api.Count(HttpMethod.Put, metricsPath));
        Assert.IsEmpty(page.FormDialog().Alerts());
    }
}
