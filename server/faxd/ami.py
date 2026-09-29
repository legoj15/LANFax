"""Asterisk Manager Interface client and AMI-event -> job-state mapper."""
import logging
import select
import socket
import threading
import uuid

_log = logging.getLogger("faxd.ami")

_TERMINAL = ("sent", "failed", "received")
_ACTIVE = ("queued", "converting", "dialing", "sending")


class AmiError(Exception):
    pass


class AmiNotConnected(AmiError):
    pass


class AmiTimeout(AmiError):
    pass


class _Pending:
    def __init__(self, name):
        self.name = name
        self.event = threading.Event()
        self.response = None
        self.list = []
        self.listing = False
        self.error = None

    def finish(self):
        if self.listing:
            self.response["list"] = self.list
        self.event.set()


def _encode(packet):
    return ("".join("%s: %s\r\n" % (k, v) for k, v in packet.items()) + "\r\n").encode("utf-8")


def _parse(text):
    pkt = {}
    for line in text.split("\r\n"):
        if ": " in line:
            k, v = line.split(": ", 1)
            pkt[k] = v
        elif line.endswith(":"):
            pkt[line[:-1]] = ""
    return pkt


class AmiClient:
    def __init__(self, host, port, user, secret, on_event, logger=None,
                 backoff_initial=1.0, backoff_max=30.0, connect_timeout=5.0, on_connect=None):
        self._host, self._port = host, port
        self._user, self._secret = user, secret
        self._on_event = on_event
        self._on_connect = on_connect   # called on the reader thread after every login
        self._log = logger or _log
        self._backoff_initial = backoff_initial
        self._backoff_max = backoff_max
        self._connect_timeout = connect_timeout
        self._stop = threading.Event()
        self._connected = threading.Event()
        self._thread = None
        self._sock = None
        self._send_lock = threading.Lock()
        self._plock = threading.Lock()
        self._pending = {}

    # -- lifecycle ---------------------------------------------------------
    def start(self):
        if self._thread and self._thread.is_alive():
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._run, name="ami", daemon=True)
        self._thread.start()

    def stop(self):
        self._stop.set()
        self._close_sock()
        t = self._thread
        if t and t is not threading.current_thread():
            t.join(2.0)
        self._connected.clear()

    def connected(self):
        return self._connected.is_set()

    def alive(self):
        """True while the reader thread runs (it survives disconnects; it only ends on stop)."""
        t = self._thread
        return bool(t and t.is_alive())

    def _close_sock(self):
        s = self._sock
        if s is not None:
            try:
                s.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            try:
                s.close()
            except OSError:
                pass

    # -- reader thread -----------------------------------------------------
    def _run(self):
        delay = self._backoff_initial
        while not self._stop.is_set():
            ok = False
            try:
                ok = self._session()
            except Exception as e:  # noqa: BLE001 - keep the thread alive
                if not self._stop.is_set():
                    self._log.warning("AMI connection error: %s", e)
            finally:
                self._connected.clear()
                self._close_sock()
                self._fail_pending()
            if self._stop.is_set():
                return
            if ok:
                delay = self._backoff_initial
            self._stop.wait(delay)
            delay = min(delay * 2, self._backoff_max)

    def _session(self):
        """One connection. Returns True if login succeeded."""
        sock = socket.create_connection((self._host, self._port), self._connect_timeout)
        sock.settimeout(5.0)
        self._sock = sock
        if self._stop.is_set():
            return False
        buf = bytearray()
        banner = self._read_until(sock, buf, b"\r\n")
        if banner is None:
            return False
        self._log.debug("AMI banner: %s", banner.decode("utf-8", "replace").strip())
        aid = uuid.uuid4().hex
        sock.sendall(_encode({"Action": "Login", "ActionID": aid, "Username": self._user,
                              "Secret": self._secret, "Events": "on"}))
        while True:
            pkt = self._read_packet(sock, buf)
            if pkt is None:
                return False
            if "Response" in pkt and pkt.get("ActionID") == aid:
                break
        if pkt.get("Response") != "Success":
            self._log.error("AMI login failed for user %r: %s", self._user, pkt.get("Message", ""))
            return False
        self._log.info("AMI connected to %s:%s", self._host, self._port)
        self._connected.set()
        if self._on_connect is not None:
            try:
                self._on_connect()
            except Exception:  # noqa: BLE001
                self._log.exception("AMI on_connect handler failed")
        while not self._stop.is_set():
            pkt = self._read_packet(sock, buf)
            if pkt is None:
                self._log.warning("AMI connection closed")
                return True
            self._dispatch(pkt)
        return True

    def _read_until(self, sock, buf, sep):
        while sep not in buf:
            if not self._fill(sock, buf):
                return None
        i = buf.index(sep)
        out = bytes(buf[:i])
        del buf[:i + len(sep)]
        return out

    def _read_packet(self, sock, buf):
        raw = self._read_until(sock, buf, b"\r\n\r\n")
        if raw is None:
            return None
        return _parse(raw.decode("utf-8", "replace"))

    def _fill(self, sock, buf):
        """Read more bytes; False on EOF or stop."""
        while not self._stop.is_set():
            r, _, _ = select.select([sock], [], [], 0.5)
            if r:
                data = sock.recv(65536)
                if not data:
                    return False
                buf.extend(data)
                return True
        return False

    def _dispatch(self, pkt):
        aid = pkt.get("ActionID")
        if aid:
            with self._plock:
                p = self._pending.get(aid)
                if p is not None:
                    self._route(aid, p, pkt)
        if "Event" in pkt:
            try:
                self._on_event(pkt)
            except Exception:  # noqa: BLE001
                self._log.exception("AMI on_event handler failed for %s", pkt.get("Event"))

    def _route(self, aid, p, pkt):
        """Called with _plock held."""
        if "Response" in pkt:
            p.response = pkt
            more = pkt.get("Response") == "Success" and (
                pkt.get("EventList", "").lower() == "start" or p.name == "DBGet")
            if more:
                p.listing = True
                return
            del self._pending[aid]
            p.finish()
        elif "Event" in pkt and p.listing:
            if pkt.get("EventList", "").lower() == "complete" or pkt["Event"].endswith("Complete"):
                del self._pending[aid]
                p.finish()
            else:
                p.list.append(pkt)

    def _fail_pending(self):
        with self._plock:
            pend, self._pending = self._pending, {}
        for p in pend.values():
            p.error = AmiNotConnected("AMI connection lost")
            p.event.set()

    # -- actions -----------------------------------------------------------
    def action(self, name, timeout=5.0, **fields):
        if not self.connected():
            raise AmiNotConnected("AMI not connected")
        aid = uuid.uuid4().hex
        p = _Pending(name)
        pkt = {"Action": name, "ActionID": aid}
        pkt.update(fields)
        with self._plock:
            self._pending[aid] = p
        try:
            with self._send_lock:
                sock = self._sock
                if sock is None:
                    raise AmiNotConnected("AMI not connected")
                try:
                    sock.sendall(_encode(pkt))
                except OSError as e:
                    raise AmiNotConnected("AMI send failed: %s" % e)
            if not p.event.wait(timeout):
                raise AmiTimeout("AMI action %s timed out after %ss" % (name, timeout))
        finally:
            with self._plock:
                self._pending.pop(aid, None)
        if p.error:
            raise p.error
        return p.response

    def db_get(self, family, key):
        r = self.action("DBGet", Family=family, Key=key)
        if r.get("Response") != "Success":
            return None
        for ev in r.get("list", []):
            if "Val" in ev:
                return ev["Val"]
        return r.get("Val")

    def db_put(self, family, key, value):
        return self.action("DBPut", Family=family, Key=key, Val=value).get("Response") == "Success"

    def db_del(self, family, key):
        return self.action("DBDel", Family=family, Key=key).get("Response") == "Success"

    def get_var(self, channel, var):
        r = self.action("Getvar", Channel=channel, Variable=var)
        if r.get("Response") != "Success":
            return None
        return r.get("Value")


class AmiProgress:
    """Maps AMI events to job state. Constructed with (reg, bus, ami)."""

    def __init__(self, reg, bus, ami):
        self._reg, self._bus, self._ami = reg, bus, ami
        self._lock = threading.RLock()
        self._pending = {}    # uniqueid -> ast_jobid, row not found yet
        self._jobs = {}       # uniqueid -> job id
        self._chan = {}       # uniqueid -> channel name

    def known(self, uniqueid):
        with self._lock:
            return uniqueid in self._jobs

    def active(self):
        with self._lock:
            return bool(self._jobs)

    def reset(self):
        """Forget every channel mapping (call on AMI reconnect: Uniqueids from the old
        session will never see their Hangup)."""
        with self._lock:
            self._pending.clear()
            self._jobs.clear()
            self._chan.clear()

    def _publish(self, row):
        if row:
            self._bus.publish("job", **row)

    def _resolve(self, uid):
        ast = self._pending.get(uid)
        if ast is None:
            return
        row = self._reg.find_by_ast_jobid(ast)
        if not row:
            return
        del self._pending[uid]
        self._jobs[uid] = row["id"]
        self._publish(self._reg.set_state_if(row["id"], ("queued", "converting"), "dialing"))

    def _annotate(self, job_id, *, pages=None, detail=None):
        """Record pages/detail without changing state; a no-op once the job finished."""
        row = self._reg.get_job(job_id)
        if not row or row.get("state") in _TERMINAL:
            return
        st = row["state"]
        self._publish(self._reg.set_state_if(job_id, (st,), st, pages=pages, detail=detail))

    def handle(self, ev):
        with self._lock:
            self._handle(ev)

    def _handle(self, ev):
        name = ev.get("Event")
        uid = ev.get("Uniqueid")
        if uid and uid in self._pending:
            self._resolve(uid)
        if uid and ev.get("Channel"):
            if uid in self._jobs or uid in self._pending:
                self._chan[uid] = ev["Channel"]

        if name == "VarSet" and ev.get("Variable") == "JOBID" and uid:
            self._pending[uid] = ev.get("Value", "")
            if ev.get("Channel"):
                self._chan[uid] = ev["Channel"]
            self._resolve(uid)
        elif name == "FAXStatus":
            if ev.get("Operation") == "receive":
                self._receiving(ev)
            elif uid in self._jobs:
                self._publish(self._reg.set_state_if(
                    self._jobs[uid], _ACTIVE, "sending", detail=ev.get("Status")))
        elif name == "ReceiveFAX":
            self._receiving(ev)
        elif name == "SendFAX":
            if uid in self._jobs:
                detail = "sent %s page(s)" % ev.get("PagesTransferred", "?")
                if ev.get("TransferRate"):
                    detail += " at %s" % ev["TransferRate"]
                try:
                    pages = int(ev.get("PagesTransferred", ""))
                except ValueError:
                    pages = None
                self._annotate(self._jobs[uid], pages=pages, detail=detail)
        elif name == "Hangup":
            if uid:
                self._jobs.pop(uid, None)
                self._pending.pop(uid, None)
                self._chan.pop(uid, None)
        elif name == "OriginateResponse" and ev.get("Response") == "Failure":
            job_id = self._jobs.get(uid)
            if job_id is None and ev.get("Channel"):
                for u, c in self._chan.items():
                    if c == ev["Channel"] and u in self._jobs:
                        job_id = self._jobs[u]
                        break
            if job_id:
                self._annotate(job_id, detail="dial attempt failed")

    def _receiving(self, ev):
        who = ev.get("CallerIDNum") or ev.get("RemoteStationID") or ""
        self._bus.publish("status", receiving=True, **{"from": who})

    def poll_sessions(self):
        """Update page counts from FAXSessions; call every ~2 s while sending."""
        try:
            resp = self._ami.action("FAXSessions")
        except AmiError as e:
            _log.debug("FAXSessions poll skipped: %s", e)
            return
        entries = resp.get("list", [])
        with self._lock:
            for ent in entries:
                chan = ent.get("Channel")
                if not chan:
                    continue
                uid = next((u for u, c in self._chan.items()
                            if c == chan and u in self._jobs), None)
                if uid is None:
                    continue
                raw = ent.get("PagesTransmitted") or ent.get("PageNumber")
                try:
                    pages = int(raw)
                except (TypeError, ValueError):
                    continue
                row = self._reg.get_job(self._jobs[uid])
                if row and row.get("pages") != pages:
                    self._annotate(self._jobs[uid], pages=pages)
