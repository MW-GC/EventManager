using System.Net;
using System.Net.Http.Json;

namespace MW_GC.EventManager.Web.Tests;

// One Comment row as a stub API stores it, and the API's JSON for it (#200 item 9: one copy of the record and the
// JSON shape). The eTag travels in the body and, on an add or edit, in the ETag header, as the API sends it.
internal sealed record StoredComment(Guid Id, string Text, DateTimeOffset Added, Guid? EventId, string Tag)
{
    public object Json(Guid activityId) =>
        new { id = Id, activityId, text = Text, createdUtc = Added, eventId = EventId, eTag = Tag };

    // The API's answer to an add or edit: the stored Comment with its eTag in the body and the header.
    public HttpResponseMessage Response(Guid activityId, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = JsonContent.Create(Json(activityId)) };
        response.Headers.TryAddWithoutValidation("ETag", Tag);
        return response;
    }

    // The API's answer to a list read: the Comments, oldest first.
    public static HttpResponseMessage List(Guid activityId, IEnumerable<StoredComment> comments) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(comments.Select(c => c.Json(activityId)).ToArray()) };
}
