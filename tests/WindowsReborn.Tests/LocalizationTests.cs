using WindowsReborn.Core.Localization;

namespace WindowsReborn.Tests;

public class LocalizationTests
{
    [Fact]
    public void Spanish_and_English_have_the_same_keys()
    {
        var es = Loc.LoadEmbedded("es");
        var en = Loc.LoadEmbedded("en");
        Assert.NotEmpty(en);
        Assert.Empty(en.Keys.Except(es.Keys));
        Assert.Empty(es.Keys.Except(en.Keys));
    }
}
