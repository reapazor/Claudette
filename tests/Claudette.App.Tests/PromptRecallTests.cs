using Claudette.App.ViewModels;

namespace Claudette.App.Tests;

/// <summary>Up and Down through earlier prompts in the composer (DESIGN.md §5, "Composer").</summary>
public class PromptRecallTests
{
    [Fact]
    public void Up_goes_back_and_down_comes_forward_to_the_draft()
    {
        var recall = new PromptRecall();
        recall.Add("one");
        recall.Add("two");
        recall.Add("two");
        recall.Add("  three ");

        Assert.Equal("three", recall.Older("half typed"));
        Assert.Equal("two", recall.Older("ignored"));
        Assert.Equal("one", recall.Older(""));
        Assert.Null(recall.Older(""));
        Assert.True(recall.IsRecalling);

        Assert.Equal("two", recall.Newer());
        Assert.Equal("three", recall.Newer());
        Assert.Equal("half typed", recall.Newer());
        Assert.False(recall.IsRecalling);
        Assert.Null(recall.Newer());
    }

    [Fact]
    public void Nothing_to_recall_and_sending_starts_again()
    {
        var recall = new PromptRecall();
        Assert.Null(recall.Older("x"));

        recall.Add("a");
        recall.Older("");
        recall.Add("b");

        Assert.False(recall.IsRecalling);
        Assert.Equal("b", recall.Older(""));
    }

    [Fact]
    public void Only_the_latest_prompts_are_kept()
    {
        var recall = new PromptRecall();
        for (var i = 0; i < PromptRecall.Limit + 10; i++)
        {
            recall.Add($"p{i}");
        }

        Assert.Equal(PromptRecall.Limit, recall.Count);
    }
}
