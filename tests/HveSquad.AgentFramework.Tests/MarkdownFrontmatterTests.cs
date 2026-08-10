using HveSquad.AgentFramework.Artifacts;

namespace HveSquad.AgentFramework.Tests;

public class MarkdownFrontmatterTests
{
    [Fact]
    public void Split_SeparatesFrontmatterFromBody()
    {
        MarkdownFrontmatter.Split("---\nname: A\n---\n\n# Title\n\nBody.", out var yaml, out var body);

        Assert.Equal("name: A", yaml);
        Assert.Equal("# Title\n\nBody.", body);
    }

    [Fact]
    public void Split_TreatsDocumentWithoutFrontmatterAsBody()
    {
        MarkdownFrontmatter.Split("# Title\n\nBody.", out var yaml, out var body);

        Assert.Empty(yaml);
        Assert.Equal("# Title\n\nBody.", body);
    }

    [Fact]
    public void Split_TreatsUnterminatedFenceAsBody()
    {
        MarkdownFrontmatter.Split("---\nname: A\n\n# Title", out var yaml, out var body);

        Assert.Empty(yaml);
        Assert.Contains("# Title", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_DoesNotTreatHorizontalRuleInBodyAsFrontmatter()
    {
        MarkdownFrontmatter.Split("# Title\n\n---\n\nMore.", out var yaml, out _);

        Assert.Empty(yaml);
    }
}
