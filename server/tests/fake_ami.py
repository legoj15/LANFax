"""Reusable fake AMI server for tests (threaded, localhost, stdlib only)."""
import socket
import threading
import time


def encode(packet):
    return "".join("%s: %s\r\n" % (k, v) for k, v in packet.items()).encode() + b"\r\n"


def parse(text):
    pkt = {}
    for line in text.split("\r\n"):
        if ": " in line:
            k, v = line.split(": ", 1)
            pkt[k] = v
        elif line.endswith(":"):
            pkt[line[:-1]] = ""
    return pkt


class FakeAmiServer:
    """Accepts clients one at a time (a new client replaces the old one).

    actions: dict Action name -> callable(packet) -> list of packets, or None
    for "send no reply". ActionID is added to returned packets automatically.
    """

    def __init__(self, user="faxd", secret="s3cret", actions=None):
        self.user = user
        self.secret = secret
        self.actions = dict(actions or {})
        self.logins = 0
        self.failed_logins = 0
        self.received = []
        self.pre_login = []   # packets sent right before the Login reply
        self._cond = threading.Condition()
        self._conn = None
        self._muted = None    # a connection that is kept open but never answered again
        self._closed = False
        self._lsock = socket.socket()
        self._lsock.bind(("127.0.0.1", 0))
        self._lsock.listen(5)
        self.port = self._lsock.getsockname()[1]
        threading.Thread(target=self._accept_loop, daemon=True).start()

    def _accept_loop(self):
        while not self._closed:
            try:
                conn, _ = self._lsock.accept()
            except OSError:
                return
            with self._cond:
                old, self._conn = self._conn, conn
            if old:
                self._shut(old)
            threading.Thread(target=self._serve, args=(conn,), daemon=True).start()

    @staticmethod
    def _shut(conn):
        try:
            conn.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        try:
            conn.close()
        except OSError:
            pass

    @staticmethod
    def _send(conn, packet):
        try:
            conn.sendall(encode(packet))
        except OSError:
            pass

    def _serve(self, conn):
        try:
            conn.sendall(b"Asterisk Call Manager/7.0.3\r\n")
            buf = b""
            while True:
                data = conn.recv(4096)
                if not data:
                    return
                buf += data
                while b"\r\n\r\n" in buf:
                    raw, buf = buf.split(b"\r\n\r\n", 1)
                    pkt = parse(raw.decode())
                    self.received.append(pkt)
                    self._dispatch(conn, pkt)
        except OSError:
            return

    def _dispatch(self, conn, pkt):
        with self._cond:
            if conn is self._muted:
                return
        aid = pkt.get("ActionID")
        name = pkt.get("Action")
        if name == "Login":
            ok = pkt.get("Username") == self.user and pkt.get("Secret") == self.secret
            if ok:
                reply = {"Response": "Success", "Message": "Authentication accepted"}
            else:
                reply = {"Response": "Error", "Message": "Authentication failed"}
            if aid:
                reply["ActionID"] = aid
            for p in self.pre_login:
                self._send(conn, p)
            self._send(conn, reply)
            with self._cond:
                if ok:
                    self.logins += 1
                else:
                    self.failed_logins += 1
                self._cond.notify_all()
            if not ok:
                self._shut(conn)
            return
        handler = self.actions.get(name)
        if handler is None:
            packets = [{"Response": "Error", "Message": "Invalid/unknown command"}]
        else:
            packets = handler(pkt)
        if packets is None:
            return
        for p in packets:
            p = dict(p)
            if aid and "ActionID" not in p:
                p["ActionID"] = aid
            self._send(conn, p)

    def emit(self, packet):
        with self._cond:
            conn = self._conn
        if conn:
            self._send(conn, packet)

    def mute(self):
        """Stop answering the current client without closing its socket (a hung PBX or a
        half-open TCP connection). A later client is answered normally."""
        with self._cond:
            self._muted = self._conn

    def drop_client(self):
        with self._cond:
            conn, self._conn = self._conn, None
        if conn:
            self._shut(conn)

    def _wait(self, attr, n, timeout):
        end = time.monotonic() + timeout
        with self._cond:
            while getattr(self, attr) < n:
                left = end - time.monotonic()
                if left <= 0:
                    return False
                self._cond.wait(left)
        return True

    def wait_logins(self, n, timeout=3.0):
        return self._wait("logins", n, timeout)

    def wait_failed_logins(self, n, timeout=3.0):
        return self._wait("failed_logins", n, timeout)

    def close(self):
        self._closed = True
        self.drop_client()
        try:
            self._lsock.close()
        except OSError:
            pass
