"""faxd.gs: merge/stamp/blank-analysis logic against a faked gs subprocess."""
import os
import sys
from datetime import datetime

import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from faxd import gs  # noqa: E402

PDF_A = b"%PDF-1.4 aaa"
PDF_B = b"%PDF-1.4 bbb"


def fake_gs(monkeypatch, run):
    """Route gs._gs to run(gs_bin, args, timeout); returns every call's args."""
    calls = []

    def _fake(gs_bin, args, timeout):
        calls.append(list(args))
        return run(args) or b""

    monkeypatch.setattr(gs, "_gs", _fake)
    return calls


def write_out(args, data):
    """Write `data` to the path after -o (what a real gs leaves behind)."""
    with open(args[args.index("-o") + 1], "wb") as f:
        f.write(data)


def input_bytes(args):
    """Concatenate the PDFs named on the command line (after -f, or the bare tails)."""
    paths = args[args.index("-f") + 1:] if "-f" in args else args[args.index("-o") + 2:]
    out = b""
    for p in paths:
        with open(p, "rb") as f:
            out += f.read()
    return out


def device_of(args):
    """The -sDEVICE=... value of one gs invocation."""
    return next(a.split("=", 1)[1] for a in args if a.startswith("-sDEVICE="))


# ---- compose ---------------------------------------------------------------

def test_single_part_unstamped_is_byte_identical_and_runs_nothing(monkeypatch):
    calls = fake_gs(monkeypatch, lambda args: (_ for _ in ()).throw(AssertionError("no gs expected")))
    assert gs.compose("gs", [("a.pdf", PDF_A)]) == PDF_A
    assert calls == []


def test_compose_stamps_single_document(monkeypatch):
    def run(args):
        assert "-c" in args
        prologue = args[args.index("-c") + 1]
        assert "(TO 5550100 FROM LANFax 2026-10-14 09:30)" in prologue
        write_out(args, b"%PDF stamped")

    calls = fake_gs(monkeypatch, run)
    out = gs.compose("gs", [("a.pdf", PDF_A)], header="TO 5550100 FROM LANFax 2026-10-14 09:30")
    assert out == b"%PDF stamped"
    args = calls[0]
    assert device_of(args) == "pdfwrite"
    assert "-dSAFER" in args and "-dNOPAUSE" in args


def test_compose_merges_multiple_documents(monkeypatch):
    def run(args):
        write_out(args, input_bytes(args) + b"-merged")

    fake_gs(monkeypatch, run)
    out = gs.compose("gs", [("a.pdf", PDF_A), ("b.pdf", PDF_B)])
    assert out == PDF_A + PDF_B + b"-merged"


def test_compose_footer_counts_pages_across_parts(monkeypatch):
    def run(args):
        if device_of(args) == "bbox":
            return b"HiResBoundingBox: 0 0 612 792\n" * 2   # each part: 2 pages
        prologue = args[args.index("-c") + 1]
        assert "( of 4)" in prologue
        assert "20 string cvs" in prologue
        write_out(args, b"out")

    fake_gs(monkeypatch, run)
    gs.compose("gs", [("a.pdf", PDF_A), ("b.pdf", PDF_B)], footer=True)


def test_compose_footer_survives_a_failed_count(monkeypatch):
    def run(args):
        if device_of(args) == "bbox":
            raise gs.GsError("nope")
        prologue = args[args.index("-c") + 1]
        assert "( of" not in prologue and "(Page ) show" in prologue
        write_out(args, b"out")

    fake_gs(monkeypatch, run)
    gs.compose("gs", [("a.pdf", PDF_A)], footer=True, header="h")


def test_compose_failure_raises_for_the_caller_to_decide(monkeypatch):
    fake_gs(monkeypatch, lambda args: (_ for _ in ()).throw(gs.GsError("broken pdf")))
    with pytest.raises(gs.GsError):
        gs.compose("gs", [("a.pdf", PDF_A), ("b.pdf", PDF_B)])


# ---- escaping ---------------------------------------------------------------

@pytest.mark.parametrize("raw,escaped", [
    ("plain", "plain"),
    ("a(b)c", r"a\(b\)c"),
    (r"back\slash", r"back\\slash"),
    ("naïve café", "na?ve caf?"),
])
def test_escape(raw, escaped):
    assert gs._escape(raw) == escaped


def test_stamp_prologue_pops_the_device_dict_and_counts_pages():
    p = gs._stamp_prologue("H", 3)
    assert p.startswith("userdict /pg 0 put")
    assert "  pop" in p
    assert "(H) show" in p and "( of 3)" in p
    assert "clippath pathbbox" in p
    assert p.rstrip().endswith("} bind >> setpagedevice")


# ---- page counting ----------------------------------------------------------

def test_page_count_reads_hires_markers(monkeypatch):
    fake_gs(monkeypatch, lambda args: b"x\nHiResBoundingBox: 1\nHiResBoundingBox: 2\n")
    assert gs.page_count("gs", PDF_A) == 2


def test_page_count_none_when_gs_says_nothing(monkeypatch):
    fake_gs(monkeypatch, lambda args: b"")
    assert gs.page_count("gs", PDF_A) is None


# ---- pgm parsing ------------------------------------------------------------

def test_pgm_min_plain():
    data = b"P5\n4 1\n255\n" + bytes([200, 255, 255, 255])
    assert gs._pgm_min(data) == 200


def test_pgm_min_with_comment():
    data = b"P5 # a comment\n4 1\n255\n" + bytes([254, 255, 255, 255])
    assert gs._pgm_min(data) == 254


def test_pgm_min_rejects_16bit_and_garbage():
    assert gs._pgm_min(b"P5\n2 2\n65535\n....") is None
    assert gs._pgm_min(b"P6\n2 2\n255\n......") is None
    assert gs._pgm_min(b"P5\n2 2\n255\n") is None   # truncated body


# ---- blank pages ------------------------------------------------------------

def pgm(w, h, darkest):
    return b"P5\n%d %d\n255\n" % (w, h) + bytes([darkest]) + b"\xff" * (w * h - 1)


def test_blank_pages_flags_white_pages_only(monkeypatch):
    def run(args):
        out = args[args.index("-o") + 1]
        with open(out % 1, "wb") as f:
            f.write(pgm(8, 8, 255))
        with open(out % 2, "wb") as f:
            f.write(pgm(8, 8, 90))
        with open(out % 3, "wb") as f:
            f.write(pgm(8, 8, 251))

    fake_gs(monkeypatch, run)
    assert gs.blank_pages("gs", PDF_A, threshold=252) == [1]
    assert gs.blank_pages("gs", PDF_A, threshold=250) == [1, 3]


def test_blank_pages_tolerates_gs_failure(monkeypatch):
    fake_gs(monkeypatch, lambda args: (_ for _ in ()).throw(gs.GsError("bad pdf")))
    assert gs.blank_pages("gs", PDF_A) == []


# ---- header text ------------------------------------------------------------

def test_header_text_is_ascii_and_dated():
    assert gs.header_text("LANFax", "15550100", datetime(2026, 10, 14, 9, 30)) == \
        "TO 15550100 FROM LANFax 2026-10-14 09:30"
