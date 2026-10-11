namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// The anonymous Event feedback page's API calls (#101). Only the two anonymous routes, never an
/// admin one. The page gives this service a plain <see cref="HttpClient"/> WITHOUT
/// <see cref="SessionExpiredHandler"/>: a signed-out visitor has no session to renew, so a failed
/// call must show a message on the page instead of sending them to sign in.
/// The caller reads and disposes the response.
/// </summary>
public sealed class FeedbackService(HttpClient http)
{
    /// <summary><c>GET /api/feedback/{token}</c>: the Event's name and date, or 404.</summary>
    public Task<HttpResponseMessage> GetEventAsync(string token) =>
        http.GetAsync($"api/feedback/{Uri.EscapeDataString(token)}");

    /// <summary><c>POST /api/feedback/{token}/like</c>: records this visitor's like, or 404.</summary>
    public Task<HttpResponseMessage> LikeAsync(string token) =>
        http.PostAsync($"api/feedback/{Uri.EscapeDataString(token)}/like", content: null);
}
