"""Hand-built single-page Letter test PDF (Helvetica, base-14, no embedding)."""
from datetime import datetime

SENTENCE = "The quick brown fox jumps over the lazy dog 0123456789"


def _esc(s: str) -> str:
    s = s.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")
    return s.encode("latin-1", "replace").decode("latin-1")


def make_test_pdf(*, to: str, station: str, when: datetime, note: str = "") -> bytes:
    fmt = "%Y-%m-%d %H:%M %Z" if when.tzinfo else "%Y-%m-%d %H:%M"
    ops = []
    y = 730

    def text(size, s, dy):
        nonlocal y
        ops.append(f"BT /F1 {size} Tf 54 {y} Td ({_esc(s)}) Tj ET")
        y -= dy

    text(28, "LANFax test page", 44)
    text(14, f"To: {to}", 20)
    text(14, f"From: {station}", 20)
    text(14, f"Sent: {when.strftime(fmt).strip()}", 20)
    text(14, note, 30)
    for size in (8, 10, 12, 16, 24):
        text(size, SENTENCE, size + 10)
    y -= 10
    for size in (6, 8, 12, 18):
        text(size, "||||||||" * 8, size + 12)
    y -= 20
    text(12, "If you received this page, the fax path works.", 0)
    stream = "\n".join(ops).encode("latin-1")

    objs = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        b"<< /Length %d >>\nstream\n" % len(stream) + stream + b"\nendstream",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
    ]
    out = bytearray(b"%PDF-1.4\n")
    offsets = []
    for i, body in enumerate(objs, start=1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % i + body + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n" % (len(objs) + 1)
    out += b"0000000000 65535 f \n"
    for o in offsets:
        out += b"%010d 00000 n \n" % o
    out += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objs) + 1, xref)
    return bytes(out)
