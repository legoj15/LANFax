"""HTTP API + SSE for faxd (stdlib only). See docs/faxd-spec.md, api.py."""
import hmac
import json
import logging
import os
import queue
import re
import threading
import time
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, unquote, urlsplit

from .arm import ArmError
from .enqueue import ExistingJob, normalize_number

log = logging.getLogger("faxd.api")

MAX_BODY = 25 * 1024 * 1024   # faxd.service caps the whole daemon at 96 MB
MAX_CONNS = 32                # concurrent connections, SSE streams included
REQUEST_TIMEOUT = 30          # seconds; socket timeout for every connection
MAX_UPLOADS = 2               # concurrent POST /jobs body reads (each may hold MAX_BODY)
REPLAY_MAX = 200              # missed job_events rows replayed on reconnect; more -> `resync`
CHUNK = 64 * 1024
_TOKEN_RE = re.compile(r"(token=)[^&\s]*", re.IGNORECASE)
_ID = r"([A-Za-z0-9_-]+)"
_CLIENT_KEY = re.compile(r"[A-Za-z0-9._:-]{16,128}")
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

    def _send(self, status, body, ctype, headers=None):
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def _json(self, status, obj, headers=None):
        body = json.dumps(obj, default=str).encode("utf-8")
        self._send(status, body, "application/json; charset=utf-8", headers)

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

    def _discard_body(self):
        """Read and drop a body we will not use (a repeated Idempotency-Key) so the client
        gets the answer instead of a reset. Bodies over the upload limit are not read."""
        self.close_connection = True
        try:
            n = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            return
        self._drain(max(0, n), self.api.max_body)

    def _client_key(self):
        """The optional Idempotency-Key header: None, or 16-128 chars [A-Za-z0-9._:-]."""
        raw = self.headers.get("Idempotency-Key")
        if raw is None:
            return None
        key = raw.strip()
        if not _CLIENT_KEY.fullmatch(key):
            raise _HttpError(400, "Idempotency-Key must be 16-128 chars of A-Z a-z 0-9 . _ : - (minimum 16)")
        return key

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
        updated_since = (query.get("updated_since") or [None])[0] or None
        if updated_since:
            updated_since = self._utc_param(updated_since, "updated_since")
        jobs = self.api.reg.list_jobs(direction=direction, states=states,
                                      limit=self._limit(query, 100), since=since,
                                      updated_since=updated_since)
        self._json(200, {"jobs": jobs})

    @staticmethod
    def _utc_param(raw, name):
        """ISO-8601 -> the registry's `YYYY-MM-DDTHH:MM:SSZ` form (naive = UTC). An unescaped
        `+` in a query string arrives as a space, so that is retried as `+`."""
        for cand in (raw, raw.replace(" ", "+")):
            try:
                dt = datetime.fromisoformat(cand)
            except ValueError:
                continue
            if dt.tzinfo is None:
                dt = dt.replace(tzinfo=timezone.utc)
            return dt.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        raise _HttpError(400, "%s must be an ISO-8601 timestamp" % name)

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

    def _prior(self, key):
        """The job an earlier request with this Idempotency-Key created, else None."""
        return self.api.reg.find_by_client_key(key) if key else None

    def _repeat(self, job, to):
        """Answer a repeated key: 200 with the job, or 409 when it was for another number."""
        try:
            same = normalize_number(to) == job.get("to_number")
        except ValueError:
            same = False
        if not same:
            return self._json(409, {"error": "idempotency key already used for a different number"})
        return self._json(200, job)

    def r_post_job(self, query):
        key = self._client_key()
        prior = self._prior(key)
        if prior is not None:
            self._discard_body()
            return self._repeat(prior, self.headers.get("X-Fax-To") or "")
        if not self.api.uploads.acquire(blocking=False):
            # not reading the (up to 25 MB) body is the point: answer and close
            self.close_connection = True
            return self._json(503, {"error": "too many uploads in progress; retry"},
                              {"Retry-After": "2"})
        try:
            body = self._read_body(self.api.max_body)
            to = (self.headers.get("X-Fax-To") or "").strip()
            if not to:
                raise _HttpError(400, "X-Fax-To header required")
            if not body.startswith(b"%PDF"):
                raise _HttpError(400, "body is not a PDF")
            subject = (self.headers.get("X-Fax-Subject") or "").strip()
            try:
                job = self.api.enqueue_fn(body, to, subject=subject, origin="api",
                                          client=self.client_ip, client_key=key)
            except ExistingJob as e:
                return self._repeat(e.job, to)
            except ValueError as e:
                raise _HttpError(400, str(e) or "invalid request")
            del body
        finally:
            self.api.uploads.release()
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
        key = self._client_key()
        obj = self._json_body()
        prior = self._prior(key)
        if prior is not None:
            to = obj.get("to")
            return self._repeat(prior, to if isinstance(to, str) else "")
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
                                      client=self.client_ip, client_key=key)
        except ExistingJob as e:
            return self._repeat(e.job, to)
        except ValueError as e:
            raise _HttpError(400, str(e) or "invalid request")
        self._json(201, job)

    def _last_event_id(self, query):
        """Last-Event-ID header (EventSource sends it on reconnect) or ?last_event_id=."""
        raw = self.headers.get("Last-Event-ID") or (query.get("last_event_id") or [""])[0]
        try:
            n = int(str(raw).strip())
        except ValueError:
            return None
        return n if n >= 0 else None

    def _replay(self, after):
        """[(sse type, event_id, payload)] for the job_events rows newer than `after`.

        Up to REPLAY_MAX rows: one `job` per job, its full current row (looked up in one
        query), ordered by its newest event, oldest first. More than that: a single
        `resync` {"reason": "too_many_events", "since": <ts of the oldest missed row>} whose
        id is the newest event, and no replay; the client reloads via GET /jobs?updated_since=.
        """
        reg = self.api.reg
        rows = reg.job_events_since(after, limit=REPLAY_MAX + 1)
        now = datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")
        if len(rows) > REPLAY_MAX:
            return [("resync", reg.max_event_id(),
                     {"type": "resync", "ts": now, "reason": "too_many_events",
                      "since": rows[0]["ts"]})]
        newest = {}
        for e in rows:
            newest[e["job_id"]] = e["id"]
        jobs = {j["id"]: j for j in reg.get_jobs(list(newest))} if newest else {}
        out = []
        for job_id, eid in sorted(newest.items(), key=lambda kv: kv[1]):
            row = jobs.get(job_id)
            if row:
                out.append(("job", eid, {"type": "job", "ts": now, **row, "event_id": eid}))
        return out

    def _event_id(self, ev):
        """SSE id of a live job/received event: its transition's job_events rowid."""
        if ev.get("type") not in ("job", "received"):
            return None
        if ev.get("event_id") is not None:
            return ev["event_id"]
        return self.api.reg.last_event_id(ev["id"]) if ev.get("id") else None

    def r_events(self, query):
        api = self.api
        last_id = self._last_event_id(query)
        q = api.bus.subscribe()   # before the replay query, so no transition falls in a gap
        try:
            # everything that can fail runs before the 200 goes out: a failure here is an
            # ordinary JSON error response, never a second status line inside the stream
            status, arm = api.status_fn(), api.arm.state()
            replay = self._replay(last_id) if last_id is not None else []
        except BaseException:
            api.bus.unsubscribe(q)
            raise
        try:
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream; charset=utf-8")
            self.send_header("Cache-Control", "no-cache")
            self.send_header("X-Accel-Buffering", "no")
            self.send_header("Connection", "close")
            self.end_headers()
            self.close_connection = True
            self._sse("status", status)
            self._sse("arm", arm)
            for etype, eid, ev in replay:
                self._sse(etype, ev, eid)
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
                self._sse(str(ev.get("type", "message")), ev, self._event_id(ev))
                last = time.monotonic()
        except OSError:
            log.debug("sse client %s gone", self.client_ip)
        except Exception:  # noqa: BLE001 - the 200 is out: log and end the stream, never a 500
            log.exception("sse stream to %s failed", self.client_ip)
        finally:
            api.bus.unsubscribe(q)

    def _sse(self, etype, data, eid=None):
        payload = json.dumps(data, default=str)
        head = "event: %s\n" % etype
        if eid is not None:
            head += "id: %d\n" % int(eid)
        self.wfile.write((head + "data: %s\n\n" % payload).encode("utf-8"))


class Api:
    def __init__(self, cfg, reg, bus, arm, status_fn, enqueue_fn, testpage_fn,
                 *, sse_keepalive=15.0, max_body=MAX_BODY, max_conns=MAX_CONNS,
                 max_uploads=MAX_UPLOADS):
        self.cfg = cfg
        self.reg = reg
        self.bus = bus
        self.arm = arm
        self.status_fn = status_fn
        self.enqueue_fn = enqueue_fn
        self.testpage_fn = testpage_fn
        self.sse_keepalive = sse_keepalive
        self.max_body = max_body
        self.uploads = threading.BoundedSemaphore(max_uploads)
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
