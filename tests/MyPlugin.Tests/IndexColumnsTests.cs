using System;
using System.Linq;
using MyPlugin;
using Xunit;
namespace MyPlugin.Tests
{
    public class IndexColumnsTests
    {
        [Fact] public void ExistingOrderAndDirectionRoundTrip()
        {
            var columns = IndexColumnsLogic.Parse("B DESC, A ASC", new[] { "A", "B" });
            Assert.Equal(new[] { "B", "A" }, columns.Select(c => c.Name));
            Assert.True(columns[0].Descending); Assert.False(columns[1].Descending);
            Assert.Equal("\"B\" DESC, \"A\"", TableSchemaLogic.IndexColumns(columns));
        }
        [Fact] public void QuotedCommaQuoteAndReservedNames()
        {
            var columns = IndexColumnsLogic.Parse("\"A,B\", \"A\"\"B\" DESC, \"DESC\"", new[] { "A,B", "A\"B", "DESC" });
            Assert.Equal(new[] { "A,B", "A\"B", "DESC" }, columns.Select(c => c.Name));
            Assert.Equal("\"A,B\", \"A\"\"B\" DESC, \"DESC\"", TableSchemaLogic.IndexColumns(columns));
        }
        [Theory]
        [InlineData("UPPER(A)")]
        [InlineData("A, A")]
        [InlineData("UNKNOWN")]
        [InlineData("\"A")]
        [InlineData("\"A\" NULLS FIRST")]
        public void UnsupportedDefinitionsCannotBeSilentlyReplaced(string text)
        {
            Assert.Throws<ArgumentException>(() => IndexColumnsLogic.Parse(text, new[] { "A", "B" }));
        }
        [Fact] public void EmptySelectionAndDescendingName()
        {
            Assert.Empty(IndexColumnsLogic.Parse("", new[] { "ID" }));
            var column = Assert.Single(IndexColumnsLogic.Parse("MY DESC", new[] { "MY DESC" }));
            Assert.Equal("MY DESC", column.Name); Assert.False(column.Descending);
        }
    }
}
