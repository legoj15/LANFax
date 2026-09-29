"""HTTP API + SSE for faxd (stdlib only). See docs/faxd-spec.md, api.py."""
import hmac
import json
import logging
import os
import queue
import re
import threading
import time
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, unquote, urlsplit

from .arm import ArmError

log = logging.getLogger("faxd.api")

MAX_BODY = 25 * 1024 * 1024   # faxd.service caps the whole daemon at 96 MB
MAX_CONNS = 32                # concurrent connections, SSE streams included
REQUEST_TIMEOUT = 30          # seconds; socket timeout for every connection
CHUNK = 64 * 1024
_TOKEN_RE = re.compile(r"(token=)[^&\s]*", re.IGNORECASE)
_ID = r"([A-Za-z0-9_-]+)"
_WEB = os.path.join(os.path.dirname(os.path.abspath(__file__)), "web", "index.html")


class _HttpError(Exception):
    def __init__(self, status, message):
        super().__init__(message)
        self.status = status
        self.message = message


def _redact(s):
    """Hide ?token=... (the /events query) before anything reaches the log."""
    return _TOKEN_RE.sub(r"\1***", str(s))


class _Server(ThreadingHTTPServer):
    daemon_threads = True
    block_on_close = False
    allow_reuse_address = True

    def __init__(self, addr, handler, max_conns=MAX_CONNS):
        self._slots = threading.BoundedSemaphore(max_conns)
        super().__init__(addr, handler)

    def process_request(self, request, client_address):
        # thread-per-connection: refuse (close) instead of spawning without bound
        if not self._slots.acquire(blocking=False):
            log.warning("connection limit reached; refusing %s", client_address[0])
            self.shutdown_request(request)
            return
        try:
            super().process_request(request, client_address)
        except BaseException:
            self._slots.release()
            raise

    def process_request_thread(self, request, client_address):
        try:
            super().process_request_thread(request, client_address)
        finally:
            self._slots.release()


class _Handler(BaseHTTPRequestHandler):
    server_version = "faxd"
    sys_version = ""
    timeout = REQUEST_TIMEOUT

    def log_message(self, fmt, *args):
        log.debug("%s - %s", self.client_address[0], _redact(fmt % args))

    # ---- plumbing -------------------------------------------------------
    @property
    def api(self):
        return self.server.api

    def _send(self, status, body, ctype):
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def _json(self, status, obj):
        body = json.dumps(obj, default=str).encode("utf-8")
        self._send(status, body, "application/json; charset=utf-8")

    def _authorized(self, query, allow_query_token):
        expected = self.api.token
        if not expected:
            return False
        supplied = ""
        h = self.headers.get("Authorization", "")
        if h.lower().startswith("bearer "):
            supplied = h[7:].strip()
        elif allow_query_token:
            supplied = (query.get("token") or [""])[0]
        return hmac.compare_digest(supplied.encode("utf-8"), expected.encode("utf-8"))

    def _read_body(self, limit, required=True):
        """Read the body into one preallocated bytearray (no second copy); 413 above
        `limit` before reading anything. A missing Content-Length is an empty body
        unless `required`."""
        raw = self.headers.get("Content-Length")
        if raw is None:
            if required:
                raise _HttpError(400, "Content-Length required")
            return bytearray()
        try:
            n = int(raw)
            if n < 0:
                raise ValueError
        except ValueError:
            raise _HttpError(400, "bad Content-Length")
        if n > limit:
            self.close_connection = True
            self._drain(n, limit * 2)
            raise _HttpError(413, "body too large")
        buf = bytearray(n)
        got = 0
        with memoryview(buf) as view:
            while got < n:
                k = self.rfile.readinto(view[got:got + CHUNK])
                if not k:
                    raise _HttpError(400, "truncated body")
                got += k
        return buf

    def _drain(self, n, cap):
        # Read and discard so the client sees our 413 instead of a reset.
        if n > cap:
            return
        left = n
        while left > 0:
            chunk = self.rfile.read(min(CHUNK, left))
            if not chunk:
                break
            left -= len(chunk)

    def _json_body(self, required=True):
        body = self._read_body(1024 * 1024, required=False)
        if not body.strip():
            if required:
                raise _HttpError(400, "JSON body required")
            return {}
        try:
            obj = json.loads(body.decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            raise _HttpError(400, "invalid JSON")
        if not isinstance(obj, dict):
            raise _HttpError(400, "JSON object expected")
        return obj

    @property
    def client_ip(self):
        return self.client_address[0]

    # ---- dispatch -------------------------------------------------------
    def _handle(self, method):
        try:
            self._dispatch(method)
        except _HttpError as e:
            self._safe_json(e.status, {"error": e.message})
        except ArmError as e:
            self._safe_json(502, {"error": str(e) or "arm script failed"})
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass
        except Exception:
            log.exception("unhandled error in %s %s", method, _redact(self.path))
            self._safe_json(500, {"error": "internal error"})

    def _safe_json(self, status, obj):
        try:
            self._json(status, obj)
        except OSError:
            pass

    def _dispatch(self, method):
        parts = urlsplit(self.path)
        path = unquote(parts.path)
        query = parse_qs(parts.query)

        if path in ("/", "/healthz"):
            if method != "GET":
                raise _HttpError(405, "method not allowed")
            return self._index() if path == "/" else self._json(200, {"ok": True})

        if not self._authorized(query, method == "GET" and path == "/events"):
            self.close_connection = True
            return self._json(401, {"error": "unauthorized"})

        for rx, handlers in self.api.routes:
            m = rx.fullmatch(path)
            if not m:
                continue
            fn = handlers.get(method)
            if fn is None:
                raise _HttpError(405, "method not allowed")
            return fn(self, query, *m.groups())
        raise _HttpError(404, "not found")

    def do_GET(self): self._handle("GET")
    def do_POST(self): self._handle("POST")
    def do_PUT(self): self._handle("PUT")
    def do_DELETE(self): self._handle("DELETE")
    def do_PATCH(self): self._handle("PATCH")
    def do_HEAD(self): self._handle("HEAD")
    def do_OPTIONS(self): self._handle("OPTIONS")

    # ---- routes ---------------------------------------------------------
    def _index(self):
        try:
            with open(_WEB, "rb") as f:
                body = f.read()
        except OSError:
            raise _HttpError(404, "web page missing")
        self._send(200, body, "text/html; charset=utf-8")

    def r_status(self, query):
        out = dict(self.api.status_fn())
        out["arm"] = self.api.arm.state()
        self._json(200, out)

    @staticmethod
    def _limit(query, default):
        raw = (query.get("limit") or [None])[0]
        if raw is None:
            return default
        try:
            n = int(raw)
        except ValueError:
            raise _HttpError(400, "bad limit")
        if n < 1:
            raise _HttpError(400, "bad limit")
        return min(n, 1000)

    def r_jobs(self, query):
        direction = (query.get("direction") or [None])[0] or None
        if direction not in (None, "in", "out"):
            raise _HttpError(400, "direction must be in or out")
        states = None
        raw = (query.get("state") or [""])[0]
        if raw:
            states = [s for s in (x.strip() for x in raw.split(",")) if s]
        since = (query.get("since") or [None])[0] or None
        jobs = self.api.reg.list_jobs(direction=direction, states=states,
                                      limit=self._limit(query, 100), since=since)
        self._json(200, {"jobs": jobs})

    def r_inbox(self, query):
        jobs = self.api.reg.list_jobs(direction="in", states=None,
                                      limit=self._limit(query, 50), since=None)
        self._json(200, {"jobs": jobs})

    def r_job(self, query, job_id):
        job = self.api.reg.get_job(job_id)
        if not job:
            raise _HttpError(404, "no such job")
        out = dict(job)
        out["events"] = self.api.reg.job_events(job_id)
        self._json(200, out)

    def r_job_pdf(self, query, job_id):
        job = self.api.reg.get_job(job_id)
        if not job:
            raise _HttpError(404, "no such job")
        path = job.get("pdf_path")
        if not path:
            raise _HttpError(404, "no pdf")
        try:
            f = open(path, "rb")
        except OSError:
            raise _HttpError(404, "pdf not available")
        with f:
            size = os.fstat(f.fileno()).st_size
            name = re.sub(r"[^A-Za-z0-9._-]", "_", job.get("filename") or (job_id + ".pdf"))
            self.send_response(200)
            self.send_header("Content-Type", "application/pdf")
            self.send_header("Content-Length", str(size))
            self.send_header("Content-Disposition", 'inline; filename="%s"' % name)
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            while True:
                chunk = f.read(CHUNK)
                if not chunk:
                    break
                self.wfile.write(chunk)

    def r_post_job(self, query):
        body = self._read_body(self.api.max_body)
        to = (self.headers.get("X-Fax-To") or "").strip()
        if not to:
            raise _HttpError(400, "X-Fax-To header required")
        if not body.startswith(b"%PDF"):
            raise _HttpError(400, "body is not a PDF")
        subject = (self.headers.get("X-Fax-Subject") or "").strip()
        try:
            job = self.api.enqueue_fn(body, to, subject=subject, origin="api",
                                      client=self.client_ip)
        except ValueError as e:
            raise _HttpError(400, str(e) or "invalid request")
        self._json(201, job)

    @staticmethod
    def _minutes(obj, default):
        m = obj.get("minutes", default)
        if isinstance(m, bool) or not isinstance(m, int) or not 1 <= m <= 1440:
            raise _HttpError(400, "minutes must be an integer 1..1440")
        return m

    def _origin(self):
        return "faxd api from %s" % self.client_ip

    def r_arm(self, query):
        m = self._minutes(self._json_body(), None)
        self._json(200, self.api.arm.arm(m, self._origin()))

    def r_disarm(self, query):
        self._read_body(1024 * 1024, required=False)
        self._json(200, self.api.arm.disarm(self._origin()))

    def r_test_receive(self, query):
        m = self._minutes(self._json_body(required=False), 20)
        self._json(200, self.api.arm.arm(m, self._origin()))

    def r_test_send(self, query):
        obj = self._json_body()
        to = obj.get("to")
        note = obj.get("note", "")
        if not isinstance(to, str) or not to.strip():
            raise _HttpError(400, "'to' required")
        if not isinstance(note, str):
            raise _HttpError(400, "'note' must be a string")
        to = to.strip()
        try:
            pdf = self.api.testpage_fn(to=to, station=self.api.cfg.station_id,
                                       when=datetime.now().astimezone(), note=note)
            job = self.api.enqueue_fn(pdf, to, subject="test page", origin="test",
                                      client=self.client_ip)
        except ValueError as e:
            raise _HttpError(400, str(e) or "invalid request")
        self._json(201, job)

    def r_events(self, query):
        api = self.api
        q = api.bus.subscribe()
        try:
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream; charset=utf-8")
            self.send_header("Cache-Control", "no-cache")
            self.send_header("X-Accel-Buffering", "no")
            self.send_header("Connection", "close")
            self.end_headers()
            self.close_connection = True
            self._sse("status", api.status_fn())
            self._sse("arm", api.arm.state())
            last = time.monotonic()
            tick = max(0.05, min(0.5, api.sse_keepalive))
            while not api.stop.is_set():
                try:
                    ev = q.get(timeout=tick)
                except queue.Empty:
                    if not api.bus.subscribed(q):
                        log.info("sse client %s fell behind; closing its stream", self.client_ip)
                        break
                    if time.monotonic() - last >= api.sse_keepalive:
                        self.wfile.write(b": keepalive\n\n")
                        last = time.monotonic()
                    continue
                self._sse(str(ev.get("type", "message")), ev)
                last = time.monotonic()
        except OSError:
            log.debug("sse client %s gone", self.client_ip)
        finally:
            api.bus.unsubscribe(q)

    def _sse(self, etype, data):
        payload = json.dumps(data, default=str)
        self.wfile.write(("event: %s\ndata: %s\n\n" % (etype, payload)).encode("utf-8"))


class Api:
    def __init__(self, cfg, reg, bus, arm, status_fn, enqueue_fn, testpage_fn,
                 *, sse_keepalive=15.0, max_body=MAX_BODY, max_conns=MAX_CONNS):
        self.cfg = cfg
        self.reg = reg
        self.bus = bus
        self.arm = arm
        self.status_fn = status_fn
        self.enqueue_fn = enqueue_fn
        self.testpage_fn = testpage_fn
        self.sse_keepalive = sse_keepalive
        self.max_body = max_body
        self.token = cfg.token
        self.stop = threading.Event()
        self._serving = threading.Event()
        H = _Handler
        self.routes = [
            (re.compile(r"/status"), {"GET": H.r_status}),
            (re.compile(r"/jobs"), {"GET": H.r_jobs, "POST": H.r_post_job}),
            (re.compile(r"/jobs/" + _ID), {"GET": H.r_job}),
            (re.compile(r"/jobs/" + _ID + r"/pdf"), {"GET": H.r_job_pdf}),
            (re.compile(r"/inbox"), {"GET": H.r_inbox}),
            (re.compile(r"/arm"), {"POST": H.r_arm}),
            (re.compile(r"/disarm"), {"POST": H.r_disarm}),
            (re.compile(r"/test/send"), {"POST": H.r_test_send}),
            (re.compile(r"/test/receive"), {"POST": H.r_test_receive}),
            (re.compile(r"/events"), {"GET": H.r_events}),
        ]
        self.httpd = _Server((cfg.http_bind, cfg.http_port), _Handler, max_conns)
        self.httpd.api = self
        self.port = self.httpd.server_address[1]

    def serve_forever(self):
        self._serving.set()
        try:
            self.httpd.serve_forever(poll_interval=0.2)
        finally:
            self._serving.clear()

    def shutdown(self):
        self.stop.set()
        if self._serving.is_set():
            self.httpd.shutdown()
        self.httpd.server_close()
