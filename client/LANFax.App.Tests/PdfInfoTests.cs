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
    public void Ignores_count_outside_the_pages_node()
    {
        // An outline (bookmarks) tree carries /Count too; only the /Type /Pages node counts pages.
        var b = Encoding.ASCII.GetBytes("%PDF-1.4\n" +
            "1 0 obj << /Type /Catalog /Pages 2 0 R /Outlines 5 0 R >> endobj\n" +
            "2 0 obj << /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >> endobj\n" +
            "3 0 obj << /Type /Page /Parent 2 0 R >> endobj\n" +
            "4 0 obj << /Type /Page /Parent 2 0 R >> endobj\n" +
            "5 0 obj << /Type /Outlines /Count 50 >> endobj\n%%EOF");
        Assert.Equal(2, PdfInfo.CountPages(b));
    }

    [Fact]
    public void Falls_back_to_page_objects_when_only_non_pages_objects_have_a_count()
    {
        var b = Encoding.ASCII.GetBytes("%PDF-1.4\n" +
            "3 0 obj << /Type /Page >> endobj\n5 0 obj << /Type /Outlines /Count 50 >> endobj\n%%EOF");
        Assert.Equal(1, PdfInfo.CountPages(b));
    }

    [Fact]
    public async Task Describes_a_file_off_the_calling_thread()
    {
        var path = Path.GetTempFileName();
        try
        {
            var bytes = Pdf(2);
            File.WriteAllBytes(path, bytes);
            // The caller is a dedicated thread blocked on the result, so the read cannot run inline on it. (Awaited
            // from a pool thread, the continuation may pick up its own queued work item: flaky, seen 19 == 19.)
            int caller = -1, worker = -1;
            string? text = null;
            var t = new Thread(() =>
            {
                caller = Environment.CurrentManagedThreadId;
                text = PdfInfo.DescribeFileAsync(path, () => worker = Environment.CurrentManagedThreadId).GetAwaiter().GetResult();
            });
            t.Start();
            Assert.True(t.Join(TimeSpan.FromSeconds(10)));
            Assert.Equal(PdfInfo.Describe(2, bytes.Length), text);
            Assert.NotEqual(-1, worker);
            Assert.NotEqual(caller, worker);
            Assert.Equal("unknown size", await PdfInfo.DescribeFileAsync(path + ".missing"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Unknown_for_garbage() => Assert.Null(PdfInfo.CountPages(Encoding.ASCII.GetBytes("%PDF-1.4 nothing here")));

    [Theory]
    [InlineData(2, 3072L, "2 pages, 3 KB")]
    [InlineData(1, 500L, "1 page, 500 bytes")]
    [InlineData(null, 2_621_440L, "unknown page count, 2.5 MB")]
    public void Describe(int? pages, long bytes, string expected) => Assert.Equal(expected, PdfInfo.Describe(pages, bytes));
}
