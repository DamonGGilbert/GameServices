using GameServices.Api.Moderation;

namespace GameServices.Tests;

/// <summary>
/// THE WORD FILTER: it must catch the tricks people use, and must NOT catch innocent words that happen to contain a
/// bad one (class, grape, Scunthorpe...).
/// </summary>
public class ContentFilterTests
{
    [Theory]
    [InlineData("fuck")]
    [InlineData("FUCK")]
    [InlineData("fuuuuuck")]
    [InlineData("f u c k")]
    [InlineData("f.u.c.k")]
    [InlineData("f_u_c_k")]
    [InlineData("$h1t")]
    [InlineData("sh!t")]
    [InlineData("fück")]
    [InlineData("motherfucker")]
    [InlineData("you ass")]
    [InlineData("@sses")]
    [InlineData("retarded")]
    [InlineData("nazis")]
    [InlineData("kys")]
    public void Bad_words_are_caught(string text)
    {
        Assert.False(ContentFilter.IsClean(text));
        Assert.False(ContentFilter.IsCleanName(text));
    }

    [Theory]
    [InlineData("class")]
    [InlineData("assassin")]
    [InlineData("grape")]
    [InlineData("therapist")]
    [InlineData("spicy")]
    [InlineData("raccoon")]
    [InlineData("cocktail")]
    [InlineData("Scunthorpe")]
    [InlineData("sniggering")]
    [InlineData("Dickens")]
    [InlineData("torpedo")]
    [InlineData("Essex")]
    [InlineData("cucumber")]
    [InlineData("I was at the bank at 10")]
    [InlineData("Good game! Walked 12,345 steps today :)")]
    [InlineData("f*ck")]   // already self-censored
    public void Innocent_words_are_left_alone(string text)
    {
        Assert.True(ContentFilter.IsClean(text));
        Assert.Equal(text, ContentFilter.Censor(text));
    }

    [Fact]
    public void Chat_is_censored_word_by_word_and_keeps_its_shape()
    {
        Assert.Equal("what the **** was that", ContentFilter.Censor("what the fuck was that"));
        Assert.Equal("oh ******* off", ContentFilter.Censor("oh f.u.c.k off"));
        Assert.Equal("nice **** mate", ContentFilter.Censor("nice $h1t mate"));
        Assert.Equal("a class act", ContentFilter.Censor("a class act"));
    }

    [Theory]
    [InlineData("BigAssDog")]
    [InlineData("xX5h1tXx")]
    [InlineData("Fu_Ck")]
    [InlineData("Lord Hitler")]
    public void Names_are_checked_even_when_run_together(string name)
    {
        Assert.False(ContentFilter.IsCleanName(name));
    }

    [Theory]
    [InlineData("Sparky")]
    [InlineData("Grass Hit Squad")]
    [InlineData("ClassAct99")]
    [InlineData("Mr Assassin")]
    [InlineData("Scunthorpe Walkers")]
    [InlineData("WkRankTesterA")]
    public void Ordinary_names_are_fine(string name)
    {
        Assert.True(ContentFilter.IsCleanName(name));
    }
}
