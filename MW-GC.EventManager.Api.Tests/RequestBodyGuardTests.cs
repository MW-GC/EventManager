using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// The shared request-body guard (#45), called directly with no Functions host: one read and one
/// deserialisation, 415 for a non-JSON Content-Type, 400 with a short message for a bad body.
/// </summary>
[TestClass]
public class RequestBodyGuardTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("application/json")]
    [DataRow("application/json; charset=utf-8")]
    [DataRow("APPLICATION/JSON")]
    [DataRow("application/problem+json")]
    [DataRow("application/vnd.mwgc.event+json; charset=utf-8")]
    public async Task JsonOrMissingContentTypeIsRead(string? contentType)
    {
        var read = await RequestBody.ReadAsync<ThemeEntity>(TestRequests.Raw("{\"name\":\"Spooky\"}", contentType), default);

        Assert.IsTrue(read.Ok);
        Assert.AreEqual("Spooky", read.Value!.Name);
    }

    [TestMethod]
    [DataRow("text/plain")]
    [DataRow("text/plain; charset=utf-8")]
    [DataRow("application/x-www-form-urlencoded")]
    [DataRow("multipart/form-data; boundary=x")]
    [DataRow("application/jsonx")]
    [DataRow("text/json-ish")]
    [DataRow("not a media type")]
    public async Task NonJsonContentTypeIs415EvenWithAValidBody(string contentType)
    {
        var read = await RequestBody.ReadAsync<ThemeEntity>(TestRequests.Raw("{\"name\":\"Spooky\"}", contentType), default);

        Assert.IsFalse(read.Ok);
        Assert.IsNull(read.Value);
        Assert.AreEqual(StatusCodes.Status415UnsupportedMediaType, ApiResults.Status(read.Error!));
        Assert.AreEqual(RequestBody.UnsupportedMediaTypeMessage, ApiResults.Message(read.Error!));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("{\"name\":")]
    [DataRow("{")]
    [DataRow("[1,2]")]
    [DataRow("\"just a string\"")]
    [DataRow("{\"name\":42}")]
    [DataRow("{\"id\":\"not-a-guid\"}")]
    [DataRow("{\"rowKey\":\"not-a-guid\"}")]
    [DataRow("{\"rowKey\":null}")]
    public async Task MalformedOrEmptyBodyIs400WithAShortMessage(string body)
    {
        var read = await RequestBody.ReadAsync<ThemeEntity>(TestRequests.Raw(body), default);

        Assert.IsFalse(read.Ok);
        var result = Assert.IsExactInstanceOfType<BadRequestObjectResult>(read.Error);
        Assert.AreEqual(RequestBody.InvalidJsonMessage, result.Value);
    }

    [TestMethod]
    public async Task MissingBodyIs400()
    {
        var read = await RequestBody.ReadAsync<ThemeEntity>(TestRequests.Empty(), default);

        Assert.AreEqual(RequestBody.InvalidJsonMessage, Assert.IsExactInstanceOfType<BadRequestObjectResult>(read.Error).Value);
    }

    [TestMethod]
    public async Task JsonNullBodyIs400()
    {
        var read = await RequestBody.ReadAsync<ThemeEntity>(TestRequests.Raw("null"), default);

        Assert.AreEqual(RequestBody.NullBodyMessage, Assert.IsExactInstanceOfType<BadRequestObjectResult>(read.Error).Value);
    }

    [TestMethod]
    public async Task NamesAreCaseInsensitiveLikeTheOldReader()
    {
        var read = await RequestBody.ReadAsync<GameEntity>(TestRequests.Raw("{\"Name\":\"Alpha\",\"WEBSITE\":\"https://a.test\"}"), default);

        Assert.IsTrue(read.Ok);
        Assert.AreEqual("Alpha", read.Value!.Name);
        Assert.AreEqual("https://a.test", read.Value.Website);
    }

    [TestMethod]
    public async Task NonUtf8CharsetIsTranscoded()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json; charset=utf-16";
        context.Request.Body = new MemoryStream(Encoding.Unicode.GetBytes("{\"name\":\"Fête\"}"));

        var read = await RequestBody.ReadAsync<HolidayEntity>(context.Request, default);

        Assert.IsTrue(read.Ok);
        Assert.AreEqual("Fête", read.Value!.Name);
    }

    [TestMethod]
    public async Task TheBodyIsReadOnce()
    {
        var request = TestRequests.Raw("{\"name\":\"Once\"}");

        Assert.IsTrue((await RequestBody.ReadAsync<ThemeEntity>(request, default)).Ok);
        Assert.AreEqual(request.Body.Length, request.Body.Position);
    }
}
