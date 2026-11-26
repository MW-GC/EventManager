using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// The Activity route no other Library list has, on the Activities' <see cref="LibraryClient{T}"/>. Read, create,
/// update and delete are the client's own; an Activity's Comments go through <see cref="CommentClient"/>.
/// </summary>
public static class ActivityLibraryClientExtensions
{
    /// <summary>
    /// Copies an Activity (<c>POST api/activities/{id}/duplicate</c>). Returns null on success or the
    /// <see cref="ApiResult"/> message for the failure, and never throws for one.
    /// </summary>
    public static Task<string?> DuplicateAsync(this LibraryClient<ActivityEntity> activities, Guid id) =>
        activities.PostActionAsync(id, "duplicate");
}
