namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// The Activity route no other Library list has. Read, create, update and delete go through
/// <see cref="LibraryClient{T}"/> with the route <c>activities</c>; an Activity's Comments go
/// through <see cref="CommentClient"/>.
/// </summary>
public sealed class ActivityService(HttpClient http)
{
    public Task<HttpResponseMessage> DuplicateAsync(Guid id) => http.PostAsync($"api/activities/{id}/duplicate", null);
}
