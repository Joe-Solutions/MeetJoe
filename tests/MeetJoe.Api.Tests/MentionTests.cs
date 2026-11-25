using MeetJoe.Application.Chat;

namespace MeetJoe.Api.Tests;

public class MentionTests
{
    [Fact]
    public void ResolveMentions_matches_everyone_and_multi_word_names()
    {
        var ana = Guid.NewGuid();
        var paul = Guid.NewGuid();
        var members = new[]
        {
            (ana, "Ana Rivera"),
            (paul, "Paul Kevin Macandili"),
        };

        var (everyone, ids) = ChatService.ResolveMentions(
            "Hey @everyone and @Paul Kevin Macandili please look", members);

        Assert.True(everyone);
        Assert.Contains(paul, ids);
        Assert.DoesNotContain(ana, ids);
    }

    [Fact]
    public void ResolveMentions_requires_word_boundary_after_name()
    {
        var ana = Guid.NewGuid();
        var members = new[] { (ana, "Ana") };

        var (_, ids) = ChatService.ResolveMentions("Hi @Anastasia", members);
        Assert.Empty(ids);

        (_, ids) = ChatService.ResolveMentions("Hi @Ana!", members);
        Assert.Contains(ana, ids);
    }
}
