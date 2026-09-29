using System.Text;
using LANFax.App.Logic;

namespace LANFax.App.Tests;

public class PdfInfoTests
{
    /// <summary>Hand-built PDF: catalog, a Pages node (with or without /Count) and <paramref name="pages"/> Page objects.</summary>
    static byte[] Pdf(int pages, bool withCount = true)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        sb.Append("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        var kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{3 + i} 0 R"));
        sb.Append($"2 0 obj << /Type /Pages /Kids [{kids}]{(withCount ? $" /Count {pages}" : "")} >> endobj\n");
        for (int i = 0; i < pages; i++)
            sb.Append($"{3 + i} 0 obj << /Type/Page /Parent 2 0 R /MediaBox [0 0 612 792] >> endobj\n");
        sb.Append("trailer << /Root 1 0 R >>\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void Counts_two_pages() => Assert.Equal(2, PdfInfo.CountPages(Pdf(2)));

    [Fact]
    public void Counts_page_objects_when_the_pages_node_has_no_count() =>
        Assert.Equal(3, PdfInfo.CountPages(Pdf(3, withCount: false)));

    [Fact]
    public void Uses_root_count_when_page_objects_are_hidden()
    {
        // Object streams (PDF 1.5+) hide the Page dictionaries; only the root /Count is visible.
        var b = Encoding.ASCII.GetBytes("%PDF-1.5\n1 0 obj << /Type /Pages /Count 7 /Kids [] >> endobj\n%%EOF");
        Assert.Equal(7, PdfInfo.CountPages(b));
    }

    [Fact]
    public void Unknown_for_garbage() => Assert.Null(PdfInfo.CountPages(Encoding.ASCII.GetBytes("%PDF-1.4 nothing here")));

    [Theory]
    [InlineData(2, 3072L, "2 pages, 3 KB")]
    [InlineData(1, 500L, "1 page, 500 bytes")]
    [InlineData(null, 2_621_440L, "unknown page count, 2.5 MB")]
    public void Describe(int? pages, long bytes, string expected) => Assert.Equal(expected, PdfInfo.Describe(pages, bytes));
}
