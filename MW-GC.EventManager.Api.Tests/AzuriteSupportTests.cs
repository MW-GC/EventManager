namespace MW_GC.EventManager.Api.Tests;

/// <summary>Reading the optional <c>EM_API_URL</c> of the Azurite tests (#169).</summary>
[TestClass]
public class AzuriteSupportTests
{
    [TestMethod]
    [DataRow(null, null)]
    [DataRow("", null)]
    [DataRow("   \t ", null)]
    [DataRow(" http://127.0.0.1:7081/api/ ", "http://127.0.0.1:7081/api")]
    public void ABlankApiUrlCountsAsUnset(string? value, string? expected)
    {
        Assert.AreEqual(expected, AzuriteSupport.ApiUrlFrom(value));
    }
}
