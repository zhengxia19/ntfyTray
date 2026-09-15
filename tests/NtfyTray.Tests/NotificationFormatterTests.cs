using System.Globalization;
using NtfyTray.Notifications;

namespace NtfyTray.Tests;

public class NotificationFormatterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void BlankTitleUsesDefault(string? title)
    {
        var result = NotificationFormatter.Format(title, "正文", "测试", "默认标题", false);
        Assert.Equal("默认标题", result.Title);
        Assert.Equal("正文", result.Body);
    }

    [Fact]
    public void TextIsPreservedWithoutMarkupOrActionInterpretation()
    {
        const string text = "中文\n<&>\"' https://example.com/";
        var result = NotificationFormatter.Format("  标题  ", text, "topic", "fallback", false);
        Assert.Equal("  标题  ", result.Title);
        Assert.Equal(text, result.Body);
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("e\u0301")]
    [InlineData("👩‍👩‍👧‍👦")]
    [InlineData("🇨🇳")]
    public void TruncationPreservesWholeTextElementsAndIncludesEllipsis(string element)
    {
        var input = string.Concat(Enumerable.Repeat(element, 1100));
        var result = NotificationFormatter.Format(input, input, "topic", "fallback", false);
        Assert.Equal(string.Concat(Enumerable.Repeat(element, 127)) + "…", result.Title);
        Assert.Equal(string.Concat(Enumerable.Repeat(element, 1023)) + "…", result.Body);
    }

    [Fact]
    public void ExactLimitsDoNotAddEllipsis()
    {
        var result = NotificationFormatter.Format(new string('a', 128), new string('b', 1024), "topic", "fallback", false);
        Assert.Equal(new string('a', 128), result.Title);
        Assert.Equal(new string('b', 1024), result.Body);
    }

    [Fact]
    public void TopicReservesSpaceWithinBodyLimit()
    {
        var result = NotificationFormatter.Format(null, new string('文', 1024), "测试", "fallback", true);
        Assert.Equal(1024, new StringInfo(result.Body).LengthInTextElements);
        Assert.EndsWith("…\n来源：测试", result.Body);
    }

    [Fact]
    public void OversizedTopicStillRespectsBodyLimit()
    {
        var result = NotificationFormatter.Format(null, "正文", new string('源', 1100), "fallback", true);
        Assert.Equal(1024, new StringInfo(result.Body).LengthInTextElements);
        Assert.StartsWith("\n来源：", result.Body);
        Assert.EndsWith("…", result.Body);
    }

    [Fact]
    public void EmptyMessageIsValidAndDefaultTitleIsAlsoBounded()
    {
        var result = NotificationFormatter.Format(null, "", "测试", new string('a', 129), true);
        Assert.Equal(new string('a', 127) + "…", result.Title);
        Assert.Equal("\n来源：测试", result.Body);
    }
}
