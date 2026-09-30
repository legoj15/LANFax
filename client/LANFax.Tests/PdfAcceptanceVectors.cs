using System.Text;

namespace LANFax.Tests;

/// <summary>
/// The one acceptance rule for a print job, pinned against both places that apply it: <c>PrintPipeListener</c> (a
/// job arriving on the pipe) and <c>Test-SinglePdf</c> in Install-LANFaxPrinter.ps1 (a document left in the old
/// print.pdf). Accepted = starts with <c>%PDF</c>, holds exactly one <c>%%EOF</c>, nothing but PDF whitespace
/// (space, CR, LF, tab, FF, NUL) after it, and no larger than the size limit. docs/client-spec.md "Print job
/// acceptance" describes the same rule.
/// </summary>
public static class PdfAcceptanceVectors
{
    /// <summary>The size limit the vectors are checked with (the product default is 200 MB).</summary>
    public const long MaxBytes = 1000;

    public static readonly (string name, byte[] bytes, bool accepted)[] Cases =
    [
        ("one", L("%PDF-1.7\nbody\n%%EOF\n"), true),
        ("no-final-newline", L("%PDF-1.7\nbody\n%%EOF"), true),
        ("trailing-whitespace", L("%PDF-1.7\nbody\n%%EOF\r\n \t\f\0"), true),
        ("two-jobs-over-each-other", L("%PDF-1.7\na\n%%EOF\n%PDF-1.7\nb\n%%EOF\n"), false),
        ("two-sections", L("%PDF-1.7\na\n%%EOF\nb\n%%EOF\n"), false), // Print To PDF never writes an incremental update
        ("cut-off", L("%PDF-1.7\ncut off"), false),
        ("data-after-eof", L("%PDF-1.7\na\n%%EOF\nxref"), false),
        ("not-a-pdf", L("hello %%EOF"), false),
        ("short", L("%PD"), false),
        ("empty", [], false),
        ("oversize", L("%PDF-1.7\n" + new string('x', 2000) + "\n%%EOF\n"), false),
    ];

    public static IEnumerable<object[]> Names => Cases.Select(c => new object[] { c.name });

    public static (string name, byte[] bytes, bool accepted) Get(string name) => Cases.Single(c => c.name == name);

    private static byte[] L(string s) => Encoding.Latin1.GetBytes(s);
}
