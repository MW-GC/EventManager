using MW_GC.EventManager.API.Validation;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>The visitor address of the anonymous like route (#101): which headers are read, in which order, and which values count.</summary>
[TestClass]
public sealed class ClientIpTests
{
    // ---- Client address -----------------------------------------------------------------------

    [TestMethod]
    [DataRow("1.2.3.4", "1.2.3.4")]
    [DataRow("1.2.3.4:5678", "1.2.3.4")]
    [DataRow("[::1]:80", "::1")]
    [DataRow("[2001:db8::7]", "2001:db8::7")]
    [DataRow("2001:db8::7", "2001:db8::7")]
    [DataRow("1.2.3.4, 10.0.0.1, 10.0.0.2", "1.2.3.4")]
    [DataRow("1.2.3.4:5678,10.0.0.1:443", "1.2.3.4")]
    [DataRow(" [2001:DB8::7]:443 , 10.0.0.1", "2001:db8::7")]
    [DataRow("::ffff:1.2.3.4", "1.2.3.4")]
    public void ClientIp_ReadsTheFirstForwardedForEntry_WithoutItsPort(string header, string expected)
    {
        var request = TestRequests.Empty();
        request.Headers[ClientIp.ForwardedForHeader] = header;
        Assert.AreEqual(expected, ClientIp.From(request));
    }

    [TestMethod]
    public void ClientIp_SeveralForwardedForHeaderLines_UseTheFirst()
    {
        var request = TestRequests.Empty();
        request.Headers[ClientIp.ForwardedForHeader] = new Microsoft.Extensions.Primitives.StringValues(["1.2.3.4:99", "10.0.0.1"]);
        Assert.AreEqual("1.2.3.4", ClientIp.From(request));
    }

    [TestMethod]
    public void ClientIp_HeaderPrecedence_ClientIpThenAzureClientIpThenForwardedFor()
    {
        var request = TestRequests.Empty();
        request.Headers[ClientIp.ForwardedForHeader] = "203.0.113.50, 10.0.0.1";
        Assert.AreEqual("203.0.113.50", ClientIp.From(request), "X-Forwarded-For alone");

        request.Headers[ClientIp.AzureClientIpHeader] = "198.51.100.20";
        Assert.AreEqual("198.51.100.20", ClientIp.From(request), "X-Azure-ClientIP beats X-Forwarded-For");

        request.Headers[ClientIp.ClientIpHeader] = "192.0.2.10:5678";
        Assert.AreEqual("192.0.2.10", ClientIp.From(request), "CLIENT-IP beats both");
    }

    [TestMethod]
    public void ClientIp_AnUnparseableHeader_CountsAsAbsent_AndTheNextSourceIsUsed()
    {
        var request = TestRequests.Empty();
        request.Headers[ClientIp.ClientIpHeader] = "not-an-address";
        request.Headers[ClientIp.AzureClientIpHeader] = "1.2.3.4:port";
        request.Headers[ClientIp.ForwardedForHeader] = "203.0.113.50";
        Assert.AreEqual("203.0.113.50", ClientIp.From(request));

        request.Headers[ClientIp.ForwardedForHeader] = "unknown";
        Assert.IsNull(ClientIp.From(request), "no usable source at all");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("unknown")]
    [DataRow("1.2.3.4:port")]
    [DataRow("[::1")]
    [DataRow("1.2.3.4:1234567")]
    public void ClientIp_UnusableHeader_IsNull(string header)
    {
        var request = TestRequests.Empty();
        request.Headers[ClientIp.ForwardedForHeader] = header;
        Assert.IsNull(ClientIp.From(request));
    }

    // ---- Client address sources (the one place the source order is decided) ------------------

    [TestMethod]
    public void ClientIp_ReadsEachSourceOnItsOwn()
    {
        var request = TestRequests.Empty();
        request.Headers[ClientIp.ClientIpHeader] = "192.0.2.10";
        request.Headers[ClientIp.AzureClientIpHeader] = "198.51.100.20";
        request.Headers[ClientIp.ForwardedForHeader] = "1.2.3.4:5678, 10.0.0.1";

        Assert.AreEqual("192.0.2.10", ClientIp.Read(request, ClientIpSource.ClientIp));
        Assert.AreEqual("198.51.100.20", ClientIp.Read(request, ClientIpSource.AzureClientIp));
        Assert.AreEqual("1.2.3.4", ClientIp.Read(request, ClientIpSource.ForwardedFor));
    }

    [TestMethod]
    public void ClientIp_EachSourceIsNull_WhenItHoldsNothingUsable()
    {
        var request = TestRequests.Empty();
        request.Headers[ClientIp.AzureClientIpHeader] = "unknown";

        Assert.IsNull(ClientIp.Read(request, ClientIpSource.ClientIp));
        Assert.IsNull(ClientIp.Read(request, ClientIpSource.AzureClientIp));
        Assert.IsNull(ClientIp.Read(request, ClientIpSource.ForwardedFor));
    }

    // Header names, not ClientIpSource values: the enum is internal and test methods are public.
    public static IEnumerable<object[]> AddressFormsPerSource =>
        from source in new[] { ClientIp.ClientIpHeader, ClientIp.AzureClientIpHeader, ClientIp.ForwardedForHeader }
        from form in new[]
        {
            new[] { "1.2.3.4", "1.2.3.4" },
            new[] { "1.2.3.4:5678", "1.2.3.4" },
            new[] { "[2001:db8::5]:443", "2001:db8::5" },
            new[] { "2001:db8::5", "2001:db8::5" },
            new[] { "[2001:DB8::5]", "2001:db8::5" },
            new[] { "::ffff:1.2.3.4", "1.2.3.4" },
            new[] { "1.2.3.4\r\n", "1.2.3.4" },
        }
        select new object[] { source, form[0], form[1] };

    [TestMethod]
    [DynamicData(nameof(AddressFormsPerSource))]
    public void ClientIp_EverySource_ParsesIPv4WithPort_BracketedIPv6WithPort_AndBareIPv6(string header, string value, string expected)
    {
        var request = TestRequests.Empty();
        request.Headers[header] = value;
        Assert.AreEqual(expected, ClientIp.Read(request, SourceOf(header)));
        Assert.AreEqual(expected, ClientIp.From(request), "the default order finds it too");
    }

    [TestMethod]
    [DataRow(ClientIp.ClientIpHeader)]
    [DataRow(ClientIp.AzureClientIpHeader)]
    [DataRow(ClientIp.ForwardedForHeader)]
    public void ClientIp_EverySource_TreatsAnUnparseableValueAsAbsent(string header)
    {
        foreach (var value in new[] { "", "unknown", "1.2.3.4:port", "[::1", "1.2.3.4:1234567", "1.2.3.4\r\nX-Evil: 1" })
        {
            var request = TestRequests.Empty();
            request.Headers[header] = value;
            Assert.IsNull(ClientIp.Read(request, SourceOf(header)), $"'{value}'");
        }
    }

    [TestMethod]
    public void ClientIp_TheFirstSourceWithAUsableAddressWins_InTheGivenOrder()
    {
        var request = TestRequests.Empty();
        request.Headers[ClientIp.ClientIpHeader] = "192.0.2.10";
        request.Headers[ClientIp.AzureClientIpHeader] = "198.51.100.20";
        request.Headers[ClientIp.ForwardedForHeader] = "1.2.3.4";

        Assert.AreEqual("1.2.3.4", ClientIp.From(request, [ClientIpSource.ForwardedFor, ClientIpSource.ClientIp]));
        Assert.AreEqual("198.51.100.20", ClientIp.From(request, [ClientIpSource.AzureClientIp, ClientIpSource.ForwardedFor]));

        request.Headers.Remove(ClientIp.AzureClientIpHeader);
        Assert.AreEqual("1.2.3.4", ClientIp.From(request, [ClientIpSource.AzureClientIp, ClientIpSource.ForwardedFor]), "an empty source falls through");
        Assert.IsNull(ClientIp.From(request, []));
    }

    [TestMethod]
    public void ClientIp_DefaultSources_AreClientIpThenAzureClientIpThenForwardedFor_AndNeverTheConnection()
    {
        // Woody's decision (2026-10-11). Changing ClientIp.Sources is the one-line decision; update this with it.
        CollectionAssert.AreEqual(
            new[] { ClientIpSource.ClientIp, ClientIpSource.AzureClientIp, ClientIpSource.ForwardedFor },
            ClientIp.Sources.ToArray());

        var request = TestRequests.Empty();
        request.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");
        Assert.IsNull(ClientIp.From(request), "RemoteIpAddress is never a source");
    }

    private static ClientIpSource SourceOf(string header) => header switch
    {
        ClientIp.ClientIpHeader => ClientIpSource.ClientIp,
        ClientIp.AzureClientIpHeader => ClientIpSource.AzureClientIp,
        ClientIp.ForwardedForHeader => ClientIpSource.ForwardedFor,
        _ => throw new ArgumentOutOfRangeException(nameof(header), header, null),
    };
}
