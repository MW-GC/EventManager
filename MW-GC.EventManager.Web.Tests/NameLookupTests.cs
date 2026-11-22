using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// #183: the one "name, or the first 8 characters of the id" lookup behind the tags on the Games and Activities
// pages and in the Event editor's slots.
[TestClass]
public sealed class NameLookupTests
{
    [TestMethod]
    public void NameOrIdStart_MissingItemNameOrList_ShowsTheIdStartElseTheName()
    {
        var spooky = new ThemeEntity { Id = Guid.Parse("11111111-aaaa-4bbb-8ccc-000000000001"), Name = "Spooky" };
        var unnamed = new ThemeEntity { Id = Guid.Parse("22222222-aaaa-4bbb-8ccc-000000000002"), Name = null! };
        var missing = Guid.Parse("0badc0de-1111-4222-8333-444455556666");
        List<ThemeEntity> themes = [spooky, unnamed];

        Assert.AreEqual("Spooky", NameLookup.NameOrIdStart(themes, spooky.Id, t => t.Name));
        Assert.AreEqual("0badc0de", NameLookup.NameOrIdStart(themes, missing, t => t.Name), "an id the list does not have");
        Assert.AreEqual("22222222", NameLookup.NameOrIdStart(themes, unnamed.Id, t => t.Name), "an item with no name");
        Assert.AreEqual("11111111", NameLookup.NameOrIdStart<ThemeEntity>(null, spooky.Id, t => t.Name), "a list not loaded yet");
    }
}
