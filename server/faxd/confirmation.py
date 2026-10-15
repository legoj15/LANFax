"""The fax confirmation sheet: a printable PDF receipt for one finished fax.

Hand-built like testpage.py (Helvetica base-14, correct xref) because gs draws
rather than typesets. A first-page thumbnail, when the fax's PDF is still on
disk, is embedded as the JPEG gs rendered (DCTDecode, no re-encoding). The
sheet is a record, not the fax: ASCII only, long values wrap, missing values
are omitted.
"""
from __future__ import annotations

import os
import tempfile
from datetime import datetime
from typing import Optional

RESULT_WORDS = {"sent": "OK", "failed": "FAILED", "cancelled": "CANCELLED"}
RESULT_COLORS = {"sent": "0 0.5 0.16 rg", "failed": "0.62 0.11 0.11 rg", "cancelled": "0.35 0.35 0.35 rg"}
MAX_LOG_ROWS = 14

THUMB_WIDTH = 150.0     # points on the page; height follows the JPEG's true aspect
THUMB_MAX_HEIGHT = 340.0
THUMB_RIGHT = 558.0
THUMB_TOP = 690.0


def _esc(s: str) -> str:
    """A PDF string literal body: ASCII, with \\ ( ) escaped. The C1 range is mapped out
    too: the fonts declare WinAnsiEncoding, where 0x80-0x9F are signs and currency."""
    s = s.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")
    s = s.encode("latin-1", "replace").decode("latin-1")
    return "".join("?" if 0x80 <= ord(c) <= 0x9F else c for c in s)


def _local(iso: Optional[str]) -> Optional[datetime]:
    """An iso8601 stamp from the registry as a server-local datetime (None when absent)."""
    if not iso:
        return None
    try:
        dt = datetime.fromisoformat(str(iso).replace("Z", "+00:00"))
    except ValueError:
        return None
    return dt.astimezone()


def _when(dt: Optional[datetime]) -> str:
    return dt.strftime("%Y-%m-%d %H:%M") if dt else ""


def format_duration(seconds: float) -> str:
    seconds = max(0, int(seconds))
    if seconds < 60:
        return f"{seconds} s"
    if seconds < 3600:
        return f"{seconds // 60} min {seconds % 60} s"
    return f"{seconds // 3600} h {seconds % 3600 // 60} min"


def _wrap(text: str, width: float, size: float) -> list[str]:
    """Naive word wrap at Helvetica's average advance (~0.5 em)."""
    per_line = max(8, int(width / (size * 0.5)))
    out: list[str] = []
    for paragraph in text.splitlines() or [""]:
        line = ""
        for word in paragraph.split(" "):
            cand = f"{line} {word}".strip()
            if len(cand) > per_line and line:
                out.append(line)
                line = word
            else:
                line = cand
        out.append(line)
    return out


def build_confirmation(job: dict, events: list[dict], *, station_id: str, version: str,
                       now: Optional[datetime] = None, thumbnail: Optional[bytes] = None) -> bytes:
    """The one-page Letter sheet for a terminal outbound job.

    `thumbnail` is the JPEG gs.page_jpeg rendered from the fax's PDF (None when the
    file is gone, unreadable, or gs refused); `now` stamps the footer (tests inject it).
    """
    state = job.get("state", "")
    result = RESULT_WORDS.get(state, state.upper() or "?")
    started = _local(job.get("created_at"))
    finished = _local(job.get("finished_at"))
    duration = ""
    if started and finished:
        duration = format_duration((finished - started).total_seconds())

    # the thumbnail occupies the sheet's top-right; the field values wrap short of it
    thumb = None
    if thumbnail:
        dims = _jpeg_size(thumbnail)
        if dims:
            w, h = dims
            scale = min(THUMB_WIDTH / w, THUMB_MAX_HEIGHT / h)
            left = THUMB_RIGHT - w * scale
            bottom = THUMB_TOP - h * scale
            thumb = (w, h, left, bottom, w * scale, h * scale)

    ops: list[str] = []

    def text(x: float, y: float, size: float, s: str, bold: bool = False, gray: bool = False) -> None:
        font = "/F2" if bold else "/F1"
        color = "0.45 0.45 0.45 rg" if gray else "0 0 0 rg"
        ops.append(f"BT {font} {size} Tf {color} {x:.0f} {y:.0f} Td ({_esc(s)}) Tj ET")

    ops.append(f"BT /F2 20 Tf 54 742 Td (FAX CONFIRMATION) Tj ET")
    text(54, 726, 8, f"{station_id}  ·  LANFax", gray=True)
    ops.append("0.6 w 0.5 G 54 716 m 558 716 l S")

    ops.append(f"{RESULT_COLORS.get(state, '0 0 0 rg')} BT /F2 16 Tf 54 690 Td ({_esc(result)}) Tj ET")
    text(54, 672, 9, "Fax completed" if state == "sent" else "Fax not completed", gray=True)

    rows: list[tuple[str, str]] = [("To", job.get("to_number") or "unknown number"),
                                   ("Station ID", station_id),
                                   ("Started", _when(started)),
                                   ("Finished", _when(finished) or "not finished")]
    if duration:
        rows.append(("Duration", duration))
    if job.get("pages") is not None:
        rows.append(("Pages", str(job["pages"])))
    if job.get("subject"):
        rows.append(("Subject", str(job["subject"])))
    if job.get("filename"):
        rows.append(("Document", str(job["filename"])))
    rows.append(("Job ID", str(job.get("id", ""))))
    if job.get("detail"):
        rows.append(("Detail", str(job["detail"])))
    if job.get("error"):
        rows.append(("Error", str(job["error"])))

    y = 630.0
    for label, value in rows:
        text(54, y, 9, label, bold=True)
        width = (THUMB_RIGHT - THUMB_WIDTH - 24) - 170 if thumb else 558 - 170
        lines = _wrap(value, width, 9)
        for i, line in enumerate(lines[:4]):
            text(170, y - i * 12, 9, line)
        y -= max(18, 12 * min(len(lines), 4) + 6)

    if thumb:
        w, h, left, bottom, w_pt, h_pt = thumb
        ops.append(f"q {w_pt:.0f} 0 0 {h_pt:.0f} {left:.0f} {bottom:.0f} cm /Im7 Do Q")
        ops.append(f"0.4 w 0.5 G {left:.0f} {bottom:.0f} {w_pt:.0f} {h_pt:.0f} re S")

    log = [e for e in events or [] if e.get("state") or e.get("detail")]
    if log:
        y -= 14
        text(54, y, 11, "Transmission log", bold=True)
        y -= 16
        for e in log[:MAX_LOG_ROWS]:
            ts = _local(e.get("ts"))
            what = str(e.get("state") or "")
            detail = str(e.get("detail") or "")
            line = (ts.strftime("%H:%M:%S") if ts else "").ljust(9) + what.ljust(10)
            if detail and detail != what:
                line += detail
            text(54, y, 8, _wrap(line, 558 - 54, 8)[0], gray=True)   # the log never wraps: truncate
            y -= 11
        if len(log) > MAX_LOG_ROWS:
            text(54, y, 8, f"... and {len(log) - MAX_LOG_ROWS} more events", gray=True)

    stamp = (now.astimezone() if now and now.tzinfo else now) if now else datetime.now().astimezone()
    text(54, 40, 7, f"Sheet generated {_when(stamp)} by faxd {version}. "
                    f"The sent pages are kept as this fax's PDF.", gray=True)

    stream = "\n".join(ops).encode("latin-1")
    xobjects = b" /XObject << /Im7 7 0 R >>" if thumb else b""
    page = (b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources "
            b"<< /Font << /F1 5 0 R /F2 6 0 R >>" + xobjects + b" >> /Contents 4 0 R >>")
    contents = b"<< /Length %d >>\nstream\n" % len(stream) + stream + b"\nendstream"
    f1 = b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"
    f2 = b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"
    objs = [b"<< /Type /Catalog /Pages 2 0 R >>",
            b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            page, contents, f1, f2]
    if thumb:
        w, h = thumb[0], thumb[1]
        objs.append(b"<< /Type /XObject /Subtype /Image /Filter /DCTDecode /Width %d /Height %d "
                    b"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Length %d >>\nstream\n" % (w, h, len(thumbnail))
                    + thumbnail + b"\nendstream")
    return _assemble(objs)


def _jpeg_size(data: bytes) -> Optional[tuple[int, int]]:
    """(width, height) from a JPEG's SOF0/SOF2 marker, or None."""
    i, n = 2, len(data)
    while i + 9 < n:
        if data[i] != 0xFF:
            i += 1
            continue
        marker = data[i + 1]
        if marker in (0xC0, 0xC2):
            return int.from_bytes(data[i + 7:i + 9], "big"), int.from_bytes(data[i + 5:i + 7], "big")
        if marker in (0x01, 0xD8) or 0xD0 <= marker <= 0xD7:
            i += 2
            continue
        if i + 4 > n:
            return None
        i += 2 + int.from_bytes(data[i + 2:i + 4], "big")
    return None


def _assemble(objs: list[bytes]) -> bytes:
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


def confirmation_path(directory: Optional[str], job_id: str) -> Optional[str]:
    """Where the sheet for a job is cached (None when the daemon has no cache dir)."""
    if not directory or not job_id.isascii() or not all(c.isalnum() or c in "-_" for c in job_id):
        return None
    return os.path.join(directory, job_id + ".pdf")


def sheet_stale(path: str, job: dict) -> bool:
    """True when the job moved on after its sheet was built (cancel_job can take a row
    from cancelled back to queued when the pipeline wins a race): the cache lies then."""
    stamp = _local(job.get("updated_at"))
    if stamp is None:
        return False
    try:
        return stamp.timestamp() > os.path.getmtime(path)
    except OSError:
        return True


def write_confirmation(path: str, pdf: bytes) -> None:
    """The sheet lands atomically: two requests racing to build the same one are both fine."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=os.path.dirname(path), suffix=".part")
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(pdf)
        os.replace(tmp, path)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise
