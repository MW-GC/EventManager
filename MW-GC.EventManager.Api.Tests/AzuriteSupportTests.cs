namespace MW_GC.EventManager.Api.Tests;

/// <summary>Reading the optional <c>EM_API_URL</c> of the Azurite tests (#169), and the local-Azurite guard (#194).</summary>
[TestClass]
public class AzuriteSupportTests
{
    private const string Key = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    [TestMethod]
    [DataRow(null, null)]
    [DataRow("", null)]
    [DataRow("   \t ", null)]
    [DataRow(" http://127.0.0.1:7081/api/ ", "http://127.0.0.1:7081/api")]
    public void ABlankApiUrlCountsAsUnset(string? value, string? expected)
    {
        Assert.AreEqual(expected, AzuriteSupport.ApiUrlFrom(value));
    }

    [TestMethod]
    [DataRow("UseDevelopmentStorage=true")]
    [DataRow("usedevelopmentstorage=TRUE;")]
    [DataRow("DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=" + Key + ";TableEndpoint=http://127.0.0.1:10012/devstoreaccount1;")]
    [DataRow("DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=" + Key + ";BlobEndpoint=http://127.0.0.1:10010/devstoreaccount1;QueueEndpoint=http://127.0.0.1:10011/devstoreaccount1;TableEndpoint=http://127.0.0.1:10012/devstoreaccount1;")]
    [DataRow("AccountName=dev;AccountKey=" + Key + ";TableEndpoint=http://localhost:10002/dev")]
    [DataRow("AccountName=dev;AccountKey=" + Key + ";TableEndpoint=http://[::1]:10002/dev")]
    [DataRow("AccountName=devstoreaccount1;AccountKey=" + Key + ";TableEndpoint=http://azurite:10002/devstoreaccount1")]
    public void AConnectionStringToLocalAzuriteRuns(string connection)
    {
        Assert.IsTrue(AzuriteSupport.IsLocalAzurite(connection));
    }

    [TestMethod]
    [DataRow("UseDevelopmentStorage=false")]
    [DataRow("DefaultEndpointsProtocol=https;AccountName=prodaccount;AccountKey=" + Key + ";EndpointSuffix=core.windows.net")]
    [DataRow("DefaultEndpointsProtocol=https;AccountName=devstoreaccount1;AccountKey=" + Key + ";EndpointSuffix=core.windows.net")]
    [DataRow("AccountName=prodaccount;AccountKey=" + Key + ";TableEndpoint=https://prodaccount.table.core.windows.net/")]
    [DataRow("AccountName=prodaccount;AccountKey=" + Key + ";TableEndpoint=http://127.0.0.1.example.test:10002/prodaccount")]
    [DataRow("AccountName=prodaccount;AccountKey=" + Key + ";TableEndpoint=http://localhost.example.test/prodaccount")]
    [DataRow("AccountName=prodaccount;AccountKey=" + Key + ";BlobEndpoint=http://127.0.0.1:10000/x;TableEndpoint=https://prodaccount.table.core.windows.net/")]
    [DataRow("TableEndpoint=not a uri")]
    [DataRow("just text")]
    public void AConnectionStringToOtherStorageDoesNotRun(string connection)
    {
        Assert.IsFalse(AzuriteSupport.IsLocalAzurite(connection));
    }

    [TestMethod]
    public void TheAzuriteTestsSkipOtherStorageBeforeContactingItAndSayWhy()
    {
        var before = Environment.GetEnvironmentVariable(AzuriteSupport.ConnectionVariable);
        try
        {
            // A made-up remote account: the guard must stop the test before any client exists.
            Environment.SetEnvironmentVariable(AzuriteSupport.ConnectionVariable,
                "DefaultEndpointsProtocol=https;AccountName=prodaccount;AccountKey=" + Key + ";TableEndpoint=https://prodaccount.table.core.windows.net/");

            var skipped = Assert.ThrowsExactly<AssertInconclusiveException>(() => AzuriteSupport.Connection());

            Assert.Contains("does not point at local Azurite", skipped.Message);
            Assert.Contains(AzuriteSupport.ConnectionVariable, skipped.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AzuriteSupport.ConnectionVariable, before);
        }
    }
}
