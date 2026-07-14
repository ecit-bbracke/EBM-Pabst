using DocumentRagSystem.WebApi;
using FluentAssertions;
using Xunit;

namespace DocumentRagSystem.UnitTests
{
    public class MarkdownTableTest
    {
        [Fact]
        public void Insert_Linebreak()
        {
            var result = MarkdownTableFixer.Fix("| Product Data Sheet | 9694300352 |  | | --- | --- | --- | |  | VUC0119YUJBS |  | |  | 4114N/2H6PU | The engineer's choice |");

            result.Trim().Should().Be("| Product Data Sheet | 9694300352 |  | \n| --- | --- | --- |\n|  | VUC0119YUJBS |  |\n|  | 4114N/2H6PU | The engineer's choice |");
        }

        [Theory]
        [InlineData("Plain source text without markdown table pipes.")]
        [InlineData("Text with a separator marker | --- but no valid table start.")]
        [InlineData("| Header | Value | body without a markdown separator row")]
        public void Return_Original_Text_When_Input_Is_Not_A_Fixable_Table(string text)
        {
            MarkdownTableFixer.Fix(text).Should().Be(text);
        }
    }
}
