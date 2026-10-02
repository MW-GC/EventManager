using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using Xunit;

namespace MW_GC.EventManager.Tests;

/// <summary>
/// The shared request-body guard (#45), called directly with no Functions host: one read and one
/// deserialisation, 415 for a non-JSON Content-Type, 400 with a short message for a bad body.
/// </summary>
public class RequestBodyGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("APPLICATION/JSON")]
    [InlineData("application/problem+json")]
    [InlineData("application/vnd.mwgc.event+json; charset=utf-8")]
    public async Task JsonOrMissingContentTypeIsRead(string? contentType)
    {
        var read = await RequestBody.ReadAsync<ThemeEntity>(TestRequests.Raw("{\"name\":\"Spooky\"}", contentType), default);

        Assert.True(read.Ok);
        Assert.Equal("Spooky", read.Value!.Name);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/plain; charset=utf-8")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data; boundary=x")]
    [InlineData("application/jsonx")]
    [InlineData("text/json-ish")]
    [InlineData("not a media type")]
    public async Task NonJsonContentTypeIs415EvenWithAValidBody(string contentType)
    {
        var read = await RequestBody.ReadAsync<ThemeEntity>(TestRequests.Raw("{\"name\":\"Spooky\"}", contentType), default);

        Assert.False(read.Ok);
        Assert.Null(read.Value);
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, ApiResults.Status(read.Error!));
        Assert.Equal(RequestBody.UnsupportedMediaTypeMessage, ApiResults.Message(read.Error!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"name\":")]
    [InlineData("{")]
    [InlineData("[1,2]")]
    [InlineData("\"just a string\"")]
    [InlineData("{\"name\":42}")]
    [InlineData("{\"id\":\"not-a-guid\"}")]
    [InlineData("{\"rowKey\":\"not-a-guid\"}")]
    [InlineData("{\"rowKey\":null}")]
    public async Task MalformedOrEmptyBodyIs400WithAShortMessage(string body)
    {
        var read = await RequestBody.ReadAsync<ThemeEntity>(TestRequests.Raw(body), default);

        Assert.False(read.Ok);
        var result = Assert.IsType<BadRequestObjectResult>(read.Error);
        Assert.Equal(RequestBody.InvalidJsonMessage, result.Value);
    }

    [Fact]
    public async Task MissingBodyIs400()
    {
        var read = await RequestBody.ReadAsync<ThemeEntity>(TestRequests.Empty(), default);

        Assert.Equal(RequestBody.InvalidJsonMessage, Assert.IsType<BadRequestObjectResult>(read.Error).Value);
    }

    [Fact]
    public async Task JsonNullBodyIs400()
    {
        var read = await RequestBody.ReadAsync<ThemeEntity>(TestRequests.Raw("null"), default);

        Assert.Equal(RequestBody.NullBodyMessage, Assert.IsType<BadRequestObjectResult>(read.Error).Value);
    }

    [Fact]
    public async Task NamesAreCaseInsensitiveLikeTheOldReader()
    {
        var read = await RequestBody.ReadAsync<GameEntity>(TestRequests.Raw("{\"Name\":\"Alpha\",\"WEBSITE\":\"https://a.test\"}"), default);

        Assert.True(read.Ok);
        Assert.Equal("Alpha", read.Value!.Name);
        Assert.Equal("https://a.test", read.Value.Website);
    }

    [Fact]
    public async Task NonUtf8CharsetIsTranscoded()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json; charset=utf-16";
        context.Request.Body = new MemoryStream(Encoding.Unicode.GetBytes("{\"name\":\"Fête\"}"));

        var read = await RequestBody.ReadAsync<HolidayEntity>(context.Request, default);

        Assert.True(read.Ok);
        Assert.Equal("Fête", read.Value!.Name);
    }

    [Fact]
    public async Task TheBodyIsReadOnce()
    {
        var request = TestRequests.Raw("{\"name\":\"Once\"}");

        Assert.True((await RequestBody.ReadAsync<ThemeEntity>(request, default)).Ok);
        Assert.Equal(request.Body.Length, request.Body.Position);
    }
}
