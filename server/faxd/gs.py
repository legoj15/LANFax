"""Ghostscript helpers: multi-document merge, fax header stamping, blank-page analysis.

gs is already a hard dependency of the outbound pipeline (PDF -> TIFF G4), so these
reuse it rather than adding a PDF library. Everything here is best effort with one
exception: compose() with more than one document cannot fall back (a multi-part fax
must be one PDF), and raises GsError for the caller to refuse.
"""
from __future__ import annotations

import os
import re
import subprocess
import tempfile
from datetime import datetime
from typing import Optional

DEFAULT_DPI = 50            # enough to tell ink from paper; a page renders in ~50 ms
BLANK_THRESHOLD = 252       # a page whose darkest sample is still this close to white is blank paper
MAX_OUTPUT = 100 * 1024 * 1024   # a merged/stamped PDF larger than this is not a fax; refuses rather than slurps
MAX_PGM = 64 * 1024 * 1024  # one rendered page above this (huge MediaBox) is skipped, not slurped
ASCII_ONLY = re.compile(r"[^ -~]")   # header/footer text must be ASCII (PostScript string)
PREVIEW_DPI = 80            # the compose preview: crisp enough to judge header/footer placement
PREVIEW_MAX_PAGES = 8       # pages sent to the client; the sheet says how many more exist
PREVIEW_PAGE_BYTES = 4 * 1024 * 1024   # one rendered preview page above this ends the series
PREVIEW_TOTAL_BYTES = 12 * 1024 * 1024 # ... as does the whole series above this
THUMB_DPI = 48              # the confirmation sheet's first-page thumbnail


class GsError(Exception):
    """gs is missing, timed out, or failed on the input."""


def _gs(gs_bin: str, args: list[str], timeout: float) -> bytes:
    """Run gs to completion; returns its stderr (the bbox device reports there). Raises GsError."""
    try:
        p = subprocess.run([gs_bin, *args], capture_output=True, timeout=timeout)
    except (FileNotFoundError, OSError) as e:
        raise GsError(f"ghostscript ({gs_bin}) could not be run: {e}") from e
    except subprocess.TimeoutExpired as e:
        raise GsError(f"ghostscript timed out after {timeout:.0f}s") from e
    if p.returncode != 0:
        tail = p.stderr.decode("utf-8", "replace").strip().splitlines()[-6:]
        raise GsError("ghostscript failed: " + " | ".join(tail))
    return p.stderr


def _escape(text: str) -> str:
    r"""A PostScript string literal body: ASCII, with ( ) \ escaped."""
    return ASCII_ONLY.sub("?", text).replace("\\", r"\\").replace("(", r"\(").replace(")", r"\)")


def _stamp_prologue(header: Optional[str], footer_total: Optional[int]) -> str:
    """The -c PostScript that stamps every page via the page device's BeginPage hook.

    BeginPage receives the page device dict (popped, unread: the Red Book forbids
    looking inside) and runs before each page is imaged, so the stamp lands on every
    page of every input document. Page geometry comes from clippath, not the device
    dict, and lands in points with the origin at the bottom-left corner.
    """
    lines = ["userdict /pg 0 put", "<< /BeginPage {",
             "  pop",
             "  userdict /pg userdict /pg get 1 add put",
             "  gsave",
             "    /Helvetica findfont 7 scalefont setfont 0 setgray",
             "    clippath pathbbox userdict begin",
             "      /ury exch def /urx exch def /lly exch def /llx exch def"]
    if header:
        lines.append(f"      llx 20 add ury 20 sub moveto ({_escape(header)}) show")
    if footer_total:
        lines.append("      llx 20 add lly 10 add moveto"
                     " (Page ) show userdict /pg get 20 string cvs show"
                     f" ( of {footer_total}) show")
    elif footer_total is None and header:
        # page numbers wanted but the count failed: number alone rather than nothing
        lines.append("      llx 20 add lly 10 add moveto"
                     " (Page ) show userdict /pg get 20 string cvs show")
    lines += ["    end", "  grestore", "} bind >> setpagedevice"]
    return "\n".join(lines)


def compose(gs_bin: str, parts: list[tuple[str, bytes]], *, header: Optional[str] = None,
            footer: bool = False, timeout: float = 60.0) -> bytes:
    """One PDF out of <parts> (name, pdf bytes), optionally stamped per page.

    A single unstamped part is returned byte-for-byte: the print path's exact bytes
    stay what the server faxes and keeps. Stamping or merging re-encodes through
    gs pdfwrite, which is fine (the pipeline rasterises for transmission anyway).
    """
    if len(parts) == 1 and not header and not footer:
        return parts[0][1]
    total: Optional[int] = None
    if footer:
        try:
            total = sum(filter(None, (page_count(gs_bin, b, timeout=timeout) for _, b in parts)))
        except GsError:
            total = None   # footer without the total beats no footer
    with tempfile.TemporaryDirectory(prefix="faxd-gs-") as tmp:
        names = []
        for i, (_, data) in enumerate(parts):
            path = os.path.join(tmp, f"in{i}.pdf")
            with open(path, "wb") as f:
                f.write(data)
            names.append(path)
        out = os.path.join(tmp, "out.pdf")
        args = ["-q", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=pdfwrite",
                "-dAutoRotatePages=/None", "-o", out]
        if header or footer:
            args += ["-c", _stamp_prologue(header, total), "-f", *names]
        else:
            args += names
        _gs(gs_bin, args, timeout)
        try:
            with open(out, "rb") as f:
                data = f.read(MAX_OUTPUT + 1)
            if len(data) > MAX_OUTPUT:
                raise GsError("merged output exceeds %d MB" % (MAX_OUTPUT // (1024 * 1024)))
            return data
        except OSError as e:
            raise GsError(f"ghostscript wrote no output ({e})") from e


def page_count(gs_bin: str, pdf: bytes, timeout: float = 30.0) -> Optional[int]:
    """Pages of a PDF per gs's bbox device (one HiResBoundingBox line per page on stderr)."""
    with tempfile.TemporaryDirectory(prefix="faxd-gs-") as tmp:
        path = os.path.join(tmp, "in.pdf")
        with open(path, "wb") as f:
            f.write(pdf)
        err = _gs(gs_bin, ["-q", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=bbox",
                           "-o", os.devnull, path], timeout)
        return err.count(b"HiResBoundingBox") or None


def _pgm_min(data: bytes) -> Optional[int]:
    """The darkest sample of a P5 (binary graymap, maxval<=255) image, or None if unreadable."""
    fields: list[bytes] = []
    i, n = 0, len(data)
    while len(fields) < 4 and i < n:
        c = data[i:i + 1]
        if c == b"#":
            while i < n and data[i:i + 1] not in (b"\n", b"\r"):
                i += 1
        elif c.isspace():
            i += 1
        else:
            j = i
            while j < n and not data[j:j + 1].isspace():
                j += 1
            fields.append(data[i:j])
            i = j
    if len(fields) < 4 or fields[0] != b"P5":
        return None
    try:
        w, h, maxval = (int(x) for x in fields[1:4])
        if w <= 0 or h <= 0 or not 0 < maxval < 256:
            return None
    except ValueError:
        return None
    i += 1   # exactly one whitespace byte after maxval
    body = data[i:i + w * h]
    if len(body) < w * h:
        return None
    return min(body)


def blank_pages(gs_bin: str, pdf: bytes, *, dpi: int = DEFAULT_DPI,
                threshold: int = BLANK_THRESHOLD, max_pages: int = 200,
                timeout: float = 60.0) -> list[int]:
    """1-based numbers of pages that render as blank paper ([] when gs cannot say).

    Renders at low dpi and flags a page whose darkest sample still rounds to white:
    that is the signature of both incident variants (a page with no drawing
    operators and one whose blitted raster strips are pure white).
    """
    try:
        with tempfile.TemporaryDirectory(prefix="faxd-gs-") as tmp:
            path = os.path.join(tmp, "in.pdf")
            with open(path, "wb") as f:
                f.write(pdf)
            _gs(gs_bin, ["-q", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=pgmraw",
                         "-r" + str(dpi), "-o", os.path.join(tmp, "p-%d.pgm"), path], timeout)
            pages = sorted((int(m.group(1)), name) for name in os.listdir(tmp)
                           if (m := re.fullmatch(r"p-(\d+)\.pgm", name)))
            nums = []
            for number, name in pages:            # numeric order: p-10 does not sort after p-1
                if len(nums) >= max_pages:
                    break
                path = os.path.join(tmp, name)
                try:
                    if os.path.getsize(path) > MAX_PGM:
                        continue   # a huge MediaBox rendered huge: not blank by any sane measure
                    with open(path, "rb") as f:
                        darkest = _pgm_min(f.read(MAX_PGM + 1))
                except OSError:
                    continue
                if darkest is not None and darkest >= threshold:
                    nums.append(number)
            return nums
    except GsError:
        return []


def header_text(station_id: str, to: str, now: Optional[datetime] = None) -> str:
    """The top line burned into every page: who, to whom, when (the fax machine convention)."""
    stamp = (now or datetime.now()).strftime("%Y-%m-%d %H:%M")
    return f"TO {to} FROM {station_id} {stamp}"


def preview_pages(gs_bin: str, pdf: bytes, *, dpi: int = PREVIEW_DPI, max_pages: int = PREVIEW_MAX_PAGES,
                  max_page_bytes: int = PREVIEW_PAGE_BYTES, total_bytes: int = PREVIEW_TOTAL_BYTES,
                  timeout: float = 120.0) -> list[bytes]:
    """PNG (pnggray) renders of the first `max_pages` pages, in page order; what the send
    dialog shows of the merged document. gs stops after `max_pages` (a page range, not a
    post-render crop: rendering a 100-page fax for 8 previews is not a thing this box does);
    the byte budgets below remain as a safety net for one huge page. Raises GsError; the
    caller decides (an unrenderable merge still answers, with no pages)."""
    with tempfile.TemporaryDirectory(prefix="faxd-preview-") as tmp:
        path = os.path.join(tmp, "in.pdf")
        with open(path, "wb") as f:
            f.write(pdf)
        _gs(gs_bin, ["-q", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=pnggray",
                     "-r" + str(dpi), "-dFirstPage=1", "-dLastPage=%d" % max_pages,
                     "-o", os.path.join(tmp, "p-%d.png"), path], timeout)
        pages = sorted((int(m.group(1)), name) for name in os.listdir(tmp)
                       if (m := re.fullmatch(r"p-(\d+)\.png", name)))
        out: list[bytes] = []
        used = 0
        for _, name in pages:
            if len(out) >= max_pages:
                break
            try:
                with open(os.path.join(tmp, name), "rb") as f:
                    data = f.read(max_page_bytes + 1)
            except OSError:
                continue
            if len(data) > max_page_bytes or used + len(data) > total_bytes:
                break
            out.append(data)
            used += len(data)
        return out


def page_jpeg(gs_bin: str, pdf: bytes, *, page: int = 1, dpi: int = THUMB_DPI, timeout: float = 30.0) -> Optional[bytes]:
    """One page as a JPEG (the confirmation sheet's thumbnail), or None when gs cannot say."""
    try:
        with tempfile.TemporaryDirectory(prefix="faxd-thumb-") as tmp:
            path = os.path.join(tmp, "in.pdf")
            with open(path, "wb") as f:
                f.write(pdf)
            out = os.path.join(tmp, "p.jpg")
            _gs(gs_bin, ["-q", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=jpeg",
                         "-dFirstPage=%d" % page, "-dLastPage=%d" % page, "-r" + str(dpi),
                         "-o", out, path], timeout)
            # a JPEG bigger than the blank-check page budget is not a thumbnail; a
            # truncated one embedded in the sheet would be declare-correct and corrupt
            if os.path.getsize(out) > MAX_PGM:
                return None
            with open(out, "rb") as f:
                return f.read()
    except (GsError, OSError):
        return None
