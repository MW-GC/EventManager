namespace MW_GC.EventManager.Shared.Models;

// Issue #101: the anonymous Event feedback page and its admin side.

/// <summary>
/// What a signed-out visitor sees for a valid feedback link (<c>GET /api/feedback/{token}</c>):
/// the Event's name and date, nothing else.
/// </summary>
public sealed record FeedbackEvent(string Name, DateTimeOffset Date);

/// <summary>
/// The answer to <c>POST /api/feedback/{token}/like</c>. <see cref="AlreadyLiked"/> is true when this
/// visitor's address had already liked the Event; the count did not change.
/// </summary>
public sealed record FeedbackLikeResult(bool AlreadyLiked);

/// <summary>An Event's feedback token (admin only). The anonymous link is <c>&lt;site&gt;/feedback/&lt;token&gt;</c>.</summary>
public sealed record FeedbackLink(string Token);

/// <summary>How many visitor addresses liked an Event (admin only).</summary>
public sealed record EventLikes(int Count);
