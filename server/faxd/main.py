"""Daemon wiring: builds every component, runs them, shuts down cleanly."""
from __future__ import annotations

import argparse
import logging
import os
import signal
import sys
import threading
import time
from typing import Callable, Mapping, Optional

from . import __version__
from .ami import AmiClient, AmiError, AmiProgress
from .api import Api, CancelRefused
from .arm import ArmController
from .config import DEFAULT_ENV_FILE, Config, load
from .db import Registry, TERMINAL_STATES
from .enqueue import enqueue_pdf
from .events import EventBus, utcnow_iso
from .spool import SpoolObserver
from .testpage import make_test_pdf

log = logging.getLogger("faxd.main")

REG_CACHE_SECONDS = 10.0
PROGRESS_INTERVAL = 2.0
WATCHDOG_INTERVAL = 2.0
EXIT_THREAD_DIED = 3   # non-zero: systemd Restart=on-failure brings the daemon back
QUEUE_STATES = ("queued", "dialing", "sending")


def build_status_fn(cfg: Config, reg: Registry, ami: AmiClient) -> Callable[[], dict]:
    """Return a function producing the /status payload (registrations cached 10 s)."""
    started = utcnow_iso()
    cache = {"at": None, "value": [], "refreshing": False}
    lock = threading.Lock()

    def registrations() -> list[dict]:
        if not ami.connected():
            return []
        # The lock only guards the cache: the AMI round trip (up to 5 s) runs outside it, and
        # while one caller refreshes, everyone else gets the stale value instead of waiting.
        with lock:
            now = time.monotonic()
            fresh = cache["at"] is not None and now - cache["at"] < REG_CACHE_SECONDS
            if fresh or cache["refreshing"]:
                return list(cache["value"])
            cache["refreshing"] = True
        value: list[dict] = []
        try:
            resp = ami.action("PJSIPShowRegistrationsOutbound")
            value = [{"name": e.get("ObjectName", ""), "status": e.get("Status", "")}
                     for e in resp.get("list", [])
                     if e.get("Event") == "OutboundRegistrationDetail"]
        except AmiError as e:
            log.debug("registration query failed: %s", e)
        finally:
            with lock:
                cache["at"], cache["value"], cache["refreshing"] = time.monotonic(), value, False
        return list(value)

    def status() -> dict:
        by_state = reg.count_by_state(direction="out")
        counts = {s: by_state.get(s, 0) for s in QUEUE_STATES}
        sent = reg.list_jobs(direction="out", states=["sent"], limit=1)
        recv = reg.list_jobs(direction="in", states=["received"], limit=1)
        return {
            "daemon": {"version": __version__, "started_at": started,
                       "ami_connected": ami.connected()},
            "receiving": False,   # a full status resets the partial AMI `receiving` flag
            "queue": counts,
            "registrations": registrations(),
            "last_sent": sent[0] if sent else None,
            "last_received": recv[0] if recv else None,
        }

    return status


def _unlink(path):
    try:
        os.unlink(path)
        return True
    except OSError:
        return False


def cancel_job(cfg, reg, bus, progress, ami, job_id) -> Optional[dict]:
    """Stop a job the user no longer wants sent. Returns the cancelled row.

    queued: the pipeline has not dialled yet -- mark the row first (so a concurrent
    pickup cannot win), then remove the outbox/queue-meta files that would feed the
    pipeline. dialing/sending: the call is up -- hang up its channel through AMI, then
    mark the row; the receipt the pipeline writes afterwards finds a terminal row and
    changes nothing.
    """
    row = reg.get_job(job_id)
    if row is None:
        return None
    state = row.get("state")
    if state in TERMINAL_STATES:
        raise CancelRefused(f"the fax already finished ({state})")
    if state in ("dialing", "sending"):
        chan = progress.channel_for(job_id)
        if not chan:
            raise CancelRefused("the call is being placed; try again in a few seconds")
        try:
            resp = ami.action("Hangup", Channel=chan)
        except AmiError as e:
            raise CancelRefused(f"the PBX could not hang up the call: {e}") from e
        if resp.get("Response") != "Success":
            raise CancelRefused("the PBX refused to hang up the call")
        new = reg.set_state_if(job_id, ("dialing", "sending"), "cancelled",
                               error="cancelled before finishing")
    else:
        new = reg.set_state_if(job_id, ("queued",), "cancelled", error="cancelled before sending")
        if new is None:
            now = reg.get_job(job_id)
            raise CancelRefused(f"the fax already moved on ({(now or row).get('state')})")
        name = row.get("filename")
        if name:
            _unlink(os.path.join(cfg.outbox, name))
            _unlink(os.path.join(cfg.sending, name))
            _unlink(os.path.join(cfg.queue_meta, name + ".json"))
        if row.get("ast_jobid"):
            _unlink(os.path.join(cfg.outqueue, row["ast_jobid"] + ".env"))
    if new is None or new.get("state") != "cancelled":
        # the pipeline finished it between our read and the guarded write
        raise CancelRefused(f"the fax already moved on ({(new or row).get('state')})")
    bus.publish("job", **new)
    log.info("job %s cancelled by request (was %s)", job_id, state)
    return new


class Daemon:
    """All components plus their threads. start()/stop() are idempotent-ish."""

    def __init__(self, cfg: Config):
        self.cfg = cfg
        self.reg = Registry(cfg.db_path)
        self.bus = EventBus()
        self.observer = SpoolObserver(cfg, self.reg, self.bus)
        self.arm = ArmController(cfg, self.bus)
        self.progress: Optional[AmiProgress] = None
        self.ami = AmiClient(cfg.ami_host, cfg.ami_port, cfg.ami_user, cfg.ami_secret,
                             lambda ev: self.progress.handle(ev) if self.progress else None,
                             logging.getLogger("faxd.ami"),
                             on_connect=lambda: self.progress.reset() if self.progress else None)
        self.progress = AmiProgress(self.reg, self.bus, self.ami)
        self.status_fn = build_status_fn(cfg, self.reg, self.ami)

        def enqueue(pdf, to, *, subject="", origin="api", client="", client_key=None):
            return enqueue_pdf(cfg, self.reg, self.bus, pdf, to, subject=subject,
                               origin=origin, client=client, client_key=client_key)

        def cancel(job_id):
            return cancel_job(cfg, self.reg, self.bus, self.progress, self.ami, job_id)

        self.api = Api(cfg, self.reg, self.bus, self.arm, self.status_fn, enqueue, make_test_pdf,
                       cancel_fn=cancel)
        self.port = self.api.port
        self._stop = threading.Event()
        self._threads: list[threading.Thread] = []

    def _progress_loop(self) -> None:
        # FAXSessions polling must not run on the AMI reader thread.
        while not self._stop.wait(PROGRESS_INTERVAL):
            try:
                if self.progress.active():
                    self.progress.poll_sessions()
            except Exception:  # noqa: BLE001
                log.exception("progress poll failed")

    def start(self) -> None:
        for name, target in (("spool", lambda: self.observer.run(self._stop)),
                             ("api", self.api.serve_forever),
                             ("progress", self._progress_loop)):
            t = threading.Thread(target=target, name=name, daemon=True)
            t.start()
            self._threads.append(t)
        self.ami.start()

    def dead_threads(self) -> list[str]:
        """Names of worker threads that should be running but are not."""
        dead = [t.name for t in self._threads if not t.is_alive()]
        if self._threads and not self.ami.alive():
            dead.append("ami")
        return dead

    def stop(self) -> None:
        self._stop.set()
        self.ami.stop()
        self.api.shutdown()
        for t in self._threads:
            t.join(2.0)
        # The Registry stays open: HTTP request threads are daemon threads that may still be
        # finishing, and the process exits right after stop() anyway.


def _setup_logging() -> None:
    level = getattr(logging, os.environ.get("FAXD_LOG_LEVEL", "INFO").upper(), logging.INFO)
    logging.basicConfig(stream=sys.stdout, level=level,
                        format="%(asctime)s %(levelname)s %(name)s: %(message)s")


def check_once(daemon: Daemon) -> Optional[int]:
    """One watchdog pass: EXIT_THREAD_DIED if a worker thread died, else None."""
    dead = daemon.dead_threads()
    if dead:
        log.critical("worker thread(s) died: %s; exiting so systemd restarts faxd", ", ".join(dead))
        return EXIT_THREAD_DIED
    return None


def run_loop(daemon: Daemon, stop: threading.Event, interval: float = WATCHDOG_INTERVAL) -> int:
    """Run until `stop` is set (0) or a worker thread dies (EXIT_THREAD_DIED)."""
    while not stop.wait(interval):
        rc = check_once(daemon)
        if rc is not None:
            return rc
    return 0


def main(argv=None, environ: Optional[Mapping[str, str]] = None) -> int:
    parser = argparse.ArgumentParser(prog="faxd")
    parser.add_argument("--env-file", default=DEFAULT_ENV_FILE)
    args = parser.parse_args(argv)
    _setup_logging()
    cfg = load(args.env_file, os.environ if environ is None else environ)
    if not cfg.token:
        log.error("FAXD_TOKEN is empty; refusing to start (set it in %s)", args.env_file)
        return 2
    daemon = Daemon(cfg)
    stop = threading.Event()
    for sig in (signal.SIGINT, signal.SIGTERM):
        signal.signal(sig, lambda *_: stop.set())
    log.info("faxd %s starting: bind=%s:%d share=%s db=%s", __version__,
             cfg.http_bind, daemon.port, cfg.share_root, cfg.db_path)
    daemon.start()
    try:
        rc = run_loop(daemon, stop)
    finally:
        log.info("shutting down")
        daemon.stop()
    return rc
