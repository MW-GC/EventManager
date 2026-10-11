using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.API.Functions;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Layout;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// Issue #101: the anonymous feedback page, rendered with bUnit against a stub API. Each state, and
// that the page only ever calls the two anonymous routes (no admin route, no list, no sign-in).
[TestClass]
public sealed class FeedbackPageTests : IDisposable
{
    private const string Token = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJ0123456";
    private const string GetPath = "/api/feedback/" + Token;
    private const string LikePath = "/api/feedback/" + Token + "/like";
    private static readonly FeedbackEvent Night = new("Game night", new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero));

    private readonly StubApiHandler api = new();
    private readonly BunitContext context = new();

    public FeedbackPageTests()
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddFluentUIComponents();
        context.Services.AddSingleton(new FeedbackService(api.Client()));
    }

    public void Dispose()
    {
        // Every test: only the anonymous routes were called, and nothing navigated (no sign-in redirect).
        Assert.IsTrue(api.Requests.All(r => r.Path.StartsWith("/api/feedback/", StringComparison.Ordinal)),
            "admin or list route called: " + string.Join(", ", api.Requests.Select(r => $"{r.Method} {r.Path}")));
        Assert.AreEqual("http://localhost/", context.Services.GetRequiredService<NavigationManager>().Uri);
        context.Dispose();
    }

    private IRenderedComponent<Feedback> Render() => context.Render<Feedback>(p => p.Add(x => x.Token, Token));

    private static string Text(IRenderedComponent<Feedback> page) => page.Markup;
    private static List<string> Alerts(IRenderedComponent<Feedback> page) => page.FindAll("[role=alert]").Select(a => a.TextContent.Trim()).ToList();
    private static List<string> Statuses(IRenderedComponent<Feedback> page) => page.FindAll("[role=status]").Select(a => a.TextContent.Trim()).ToList();

    [TestMethod]
    public void Loading_ShowsAStatusUntilTheEventArrives()
    {
        var gate = api.Hold(HttpMethod.Get, GetPath, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Night) });
        var page = Render();

        Assert.AreEqual("Loading…", Assert.ContainsSingle(Statuses(page)));
        Assert.IsEmpty(page.FindAll("fluent-button"));

        gate.SetResult();
        page.WaitForAssertion(() => Assert.AreEqual("Game night", page.Find("h1").TextContent));
    }

    [TestMethod]
    public void ValidLink_ShowsTheNameDateAndOneLikeButton()
    {
        api.Json(HttpMethod.Get, GetPath, Night);
        var page = Render();

        page.WaitForAssertion(() => Assert.AreEqual("Game night", page.Find("h1").TextContent));
        Assert.Contains(Night.Date.ToLocalTime().ToString("f"), page.Find(".feedback-date").TextContent);
        Assert.AreEqual("Like", Assert.ContainsSingle(page.FindAll("fluent-button")).TextContent.Trim());
        Assert.IsEmpty(Alerts(page));
        Assert.AreEqual(1, api.Count(HttpMethod.Get, GetPath));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.NotFound)]
    public void UnknownToken_SaysTheLinkIsNotValid_AndShowsNoEventData(HttpStatusCode status)
    {
        api.Status(HttpMethod.Get, GetPath, status);
        var page = Render();

        page.WaitForAssertion(() => Assert.AreEqual("This link is not valid.", Assert.ContainsSingle(Alerts(page))));
        Assert.IsEmpty(page.FindAll("fluent-button"));
        Assert.AreEqual("Event feedback", page.Find("h1").TextContent);
    }

    [TestMethod]
    public async Task Like_ThenLikeAgain_ShowsLikedThenAlreadyLiked()
    {
        api.Json(HttpMethod.Get, GetPath, Night).Json(HttpMethod.Post, LikePath, new FeedbackLikeResult(false));
        var page = Render();
        page.WaitForAssertion(() => page.Find("fluent-button"));

        await page.Find("fluent-button").ClickAsync(new());
        page.WaitForAssertion(() => Assert.AreEqual("Thanks! Your like was recorded.", Assert.ContainsSingle(Statuses(page))));

        api.Json(HttpMethod.Post, LikePath, new FeedbackLikeResult(true));
        await page.Find("fluent-button").ClickAsync(new());
        page.WaitForAssertion(() => Assert.AreEqual(
            "You already liked this event. Each connection can like an event once.", Assert.ContainsSingle(Statuses(page))));
        Assert.AreEqual(2, api.Count(HttpMethod.Post, LikePath));
        Assert.IsEmpty(Alerts(page));
    }

    [TestMethod]
    public async Task RateLimited_SaysWaitAMinute()
    {
        api.Json(HttpMethod.Get, GetPath, Night).Status(HttpMethod.Post, LikePath, HttpStatusCode.TooManyRequests);
        var page = Render();
        page.WaitForAssertion(() => page.Find("fluent-button"));

        await page.Find("fluent-button").ClickAsync(new());

        page.WaitForAssertion(() => Assert.AreEqual(
            "Too many tries from your connection. Wait a minute, then try again.", Assert.ContainsSingle(Alerts(page))));
    }

    [TestMethod]
    public void LoadNetworkError_ShowsAMessageWithRetry_AndRetryLoads()
    {
        api.Offline(HttpMethod.Get, GetPath);
        var page = Render();
        page.WaitForAssertion(() => Assert.AreEqual(ApiResult.UnreachableMessage, page.Find("[role=alert] span").TextContent));

        api.Json(HttpMethod.Get, GetPath, Night);
        page.Find("[role=alert] fluent-button").Click();

        page.WaitForAssertion(() => Assert.AreEqual("Game night", page.Find("h1").TextContent));
        Assert.IsEmpty(Alerts(page));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.InternalServerError, "The server had a problem (HTTP 500). Try again.")]
    [DataRow(HttpStatusCode.Unauthorized, "The request was not accepted (HTTP 401).")]
    public void LoadServerError_ShowsAMessage_NeverASignInLink(HttpStatusCode status, string message)
    {
        api.Status(HttpMethod.Get, GetPath, status);
        var page = Render();

        page.WaitForAssertion(() => Assert.AreEqual(message, page.Find("[role=alert] span").TextContent));
        Assert.IsEmpty(page.FindAll("a"));
    }

    [TestMethod]
    public async Task LikeNetworkError_ShowsAMessageWithRetry_AndRetryLikes()
    {
        api.Json(HttpMethod.Get, GetPath, Night).Offline(HttpMethod.Post, LikePath);
        var page = Render();
        page.WaitForAssertion(() => page.Find("fluent-button"));

        await page.Find("fluent-button").ClickAsync(new());
        page.WaitForAssertion(() => Assert.AreEqual(ApiResult.UnreachableMessage, page.Find("[role=alert] span").TextContent));

        api.Json(HttpMethod.Post, LikePath, new FeedbackLikeResult(false));
        await page.Find("[role=alert] fluent-button").ClickAsync(new());
        page.WaitForAssertion(() => Assert.AreEqual("Thanks! Your like was recorded.", Assert.ContainsSingle(Statuses(page))));
        Assert.IsEmpty(Alerts(page));
    }

    [TestMethod]
    public async Task LikeAfterTheEventWasDeleted_SaysTheLinkIsNotValid()
    {
        api.Json(HttpMethod.Get, GetPath, Night).Status(HttpMethod.Post, LikePath, HttpStatusCode.NotFound);
        var page = Render();
        page.WaitForAssertion(() => page.Find("fluent-button"));

        await page.Find("fluent-button").ClickAsync(new());

        page.WaitForAssertion(() => Assert.AreEqual("This link is not valid.", Assert.ContainsSingle(Alerts(page))));
        Assert.DoesNotContain("Game night", Text(page));
    }

    [TestMethod]
    public void LikeButton_IsANamedFocusableButton()
    {
        api.Json(HttpMethod.Get, GetPath, Night);
        var page = Render();
        page.WaitForAssertion(() => page.Find("fluent-button"));

        var like = page.Find("fluent-button");
        Assert.AreEqual("Like", like.TextContent.Trim());
        Assert.IsFalse(like.HasAttribute("disabled"));
        // The page title is the h1 that App.razor's FocusOnNavigate moves focus to.
        Assert.AreEqual("-1", page.Find("h1").GetAttribute("tabindex"));
    }

    [TestMethod]
    public void FeedbackLayout_HasNoNavigationOrAdminControls()
    {
        var layout = context.Render<FeedbackLayout>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "page body"))));

        Assert.Contains("page body", layout.Markup);
        Assert.IsEmpty(layout.FindAll("a, fluent-nav-menu, fluent-nav-link, fluent-button, nav"));
    }

    [TestMethod]
    public void FeedbackPage_UsesTheFeedbackLayout()
    {
        var layout = typeof(Feedback).GetCustomAttribute<LayoutAttribute>();
        Assert.AreEqual(typeof(FeedbackLayout), layout?.LayoutType);
        Assert.AreEqual("/feedback/{Token}", Assert.ContainsSingle(typeof(Feedback).GetCustomAttributes<RouteAttribute>()).Template);
    }

    // The service only sends the two anonymous routes, and both are routes the API declares.
    [TestMethod]
    public async Task FeedbackService_SendsTheTwoAnonymousRoutesTheApiDeclares()
    {
        var service = new FeedbackService(api.Client());
        (await service.GetEventAsync(Token)).Dispose();
        (await service.LikeAsync(Token)).Dispose();

        CollectionAssert.AreEqual(new[] { $"GET {GetPath}", $"POST {LikePath}" },
            api.Requests.Select(r => $"{r.Method.Method} {r.Path}").ToArray());

        var declared = typeof(FeedbackFunctions).GetMethods()
            .SelectMany(m => m.GetParameters())
            .Select(p => p.GetCustomAttribute<HttpTriggerAttribute>())
            .Where(t => t is not null && t.Route!.StartsWith("feedback/", StringComparison.Ordinal))
            .SelectMany(t => t!.Methods!.Select(v => $"{v.ToUpperInvariant()} api/{t.Route}"))
            .ToList();
        CollectionAssert.AreEquivalent(new[] { "GET api/feedback/{token}", "POST api/feedback/{token}/like" }, declared);
    }
}
