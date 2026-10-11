using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.API.Functions;

/// <summary>
/// The anonymous Event feedback page's API (#101).
/// <para>
/// ANONYMOUS (allowed for signed-out visitors by the <c>/api/feedback/*</c> rule in
/// <c>staticwebapp.config.json</c>): <c>GET /api/feedback/{token}</c> and <c>POST /api/feedback/{token}/like</c>.
/// They answer with an Event's name and date, or record a like, and nothing else. An unknown or
/// malformed token is a 404 with an empty body on both, so neither route tells a guessed token apart
/// from a wrong one.
/// </para>
/// <para>
/// ADMIN (behind the existing <c>/api/*</c> admin rule): <c>GET /api/events/{id}/feedback-link</c> and
/// <c>GET /api/events/{id}/likes</c>. The token appears only in the feedback-link response, never in an
/// Event list or Event response.
/// </para>
/// </summary>
internal sealed class FeedbackFunctions
{
    private readonly FeedbackStore _feedback;
    private readonly TableStore<EventEntity> _events;
    private readonly LikeRateLimiter _limiter;
    private readonly ILogger<FeedbackFunctions> _logger;

    public FeedbackFunctions(
        FeedbackStore feedback,
        TableStore<EventEntity> events,
        LikeRateLimiter limiter,
        ILogger<FeedbackFunctions> logger)
    {
        _feedback = feedback;
        _events = events;
        _limiter = limiter;
        _logger = logger;
    }

    public const string MissingClientIpMessage = "The client address could not be determined.";

    [Function("GetFeedbackEvent")]
    public Task<IActionResult> GetEvent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "feedback/{token}")] HttpRequest req,
        string token, CancellationToken ct) => RouteFailures.RunAsync(_logger, "GetFeedbackEvent", ct, async () =>
    {
        var evt = await FindEventAsync(token, ct);
        return evt is null ? new NotFoundResult() : new OkObjectResult(new FeedbackEvent(evt.Name, evt.Date));
    });

    [Function("LikeFeedbackEvent")]
    public Task<IActionResult> Like(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "feedback/{token}/like")] HttpRequest req,
        string token, CancellationToken ct) => RouteFailures.RunAsync(_logger, "LikeFeedbackEvent", ct, async () =>
    {
        // A malformed token is the same empty 404 as an unknown one, whatever else the request lacks.
        // It costs no storage lookup, so it is answered before the address check and the rate limit.
        if (!FeedbackTokens.IsWellFormed(token)) return new NotFoundResult();

        // The raw address stays in this local: it is hashed below and never logged or stored.
        var clientIp = ClientIp.From(req);
        if (clientIp is null) return new BadRequestObjectResult(MissingClientIpMessage);

        // Every well-formed like request counts toward the limit, known token or not, so a 429 says
        // nothing about a token.
        if (!_limiter.TryAcquire(clientIp, out var retryAfter))
        {
            req.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            return new StatusCodeResult(StatusCodes.Status429TooManyRequests);
        }

        if (await _feedback.FindEventIdAsync(token, ct) is not { } eventId || await _events.GetAsync(eventId, ct) is null)
            return new NotFoundResult();

        var added = await _feedback.TryAddLikeAsync(eventId, token, clientIp, ct);
        return new OkObjectResult(new FeedbackLikeResult(AlreadyLiked: !added));
    });

    [Function("GetEventFeedbackLink")]
    public Task<IActionResult> GetLink(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "events/{id:guid}/feedback-link")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "GetEventFeedbackLink", ct, async () =>
    {
        if (await _events.GetAsync(id, ct) is null) return new NotFoundResult();
        return new OkObjectResult(new FeedbackLink(await _feedback.GetOrCreateTokenAsync(id, ct)));
    });

    [Function("GetEventLikes")]
    public Task<IActionResult> GetLikes(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "events/{id:guid}/likes")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "GetEventLikes", ct, async () =>
    {
        if (await _events.GetAsync(id, ct) is null) return new NotFoundResult();
        return new OkObjectResult(new EventLikes(await _feedback.CountLikesAsync(id, ct)));
    });

    // The Event behind a token, or null for a malformed, unknown or dead token or a deleted Event.
    private async Task<EventEntity?> FindEventAsync(string token, CancellationToken ct) =>
        await _feedback.FindEventIdAsync(token, ct) is { } eventId ? await _events.GetAsync(eventId, ct) : null;
}
