"""Test a Linux Edge CLI, optionally creating the original state with a previous CLI."""
import datetime
from concurrent.futures import ThreadPoolExecutor
import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import secrets
import sqlite3
import subprocess
import sys
import tempfile
import threading
import uuid


def tokens():
    expiry = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(hours=2)
    return dict(deviceId=str(uuid.uuid4()), edgeId=str(uuid.uuid4()), accessToken=secrets.token_hex(32),
                refreshToken=secrets.token_hex(32), expiresAt=expiry.isoformat())


original, replacement = tokens(), tokens()
project = dict(projectId=str(uuid.uuid4()), bindingId=str(uuid.uuid4()), name="Linux QA", accessMode="readOnly")


class Core(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def respond(self, code, obj):
        data = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(data)

    def do_POST(self):
        if self.path.endswith("/redeem"):
            self.server.redeemed += 1
            self.respond(200, original)
        elif self.path.endswith("/claim"):
            self.respond(200, replacement)
        elif self.path.endswith("/confirm"):
            self.respond(200 if self.server.confirm else 503, replacement)
        else:
            self.respond(404, {})

    def do_GET(self):
        valid = ["Bearer " + t["accessToken"] for t in (original, replacement)]
        self.respond(200 if self.path == "/v1/projects" and self.headers.get("Authorization") in valid else 403,
                     {"items": [project]})


current_exe = str(Path(sys.argv[1]).resolve(strict=True))
exe = str(Path(sys.argv[2]).resolve(strict=True)) if len(sys.argv) > 2 else current_exe
with tempfile.TemporaryDirectory() as tmp, ThreadingHTTPServer(("127.0.0.1", 0), Core) as core:
    core.confirm = False
    core.redeemed = 0
    threading.Thread(target=core.serve_forever, daemon=True).start()
    uri = "http://127.0.0.1:" + str(core.server_port)
    scratch = Path(tmp)
    env = dict(os.environ, HOME=tmp, XDG_STATE_HOME=str(scratch / "state"))
    for variable in ("NODICARTA_EDGE_STATE", "PROJECTHUB_EDGE_STATE"):
        env.pop(variable, None)

    def run(*args, code=None, ok=True):
        child = subprocess.run([exe, *args], input=code, text=True, capture_output=True, env=env, timeout=20)
        assert (child.returncode == 0) == ok, (args, child.returncode, child.stderr)
        if ok:
            return json.loads(child.stdout)

    state = scratch / "state" / "Nodicarta" / "Edge"
    code = str(uuid.uuid4()) + "." + secrets.token_hex(16)
    run("bindings", "--local")
    bad = state / ".credentials.key"
    bad.write_bytes(b"incomplete")
    bad.chmod(0o600)
    run("pair", "--core", uri, code=code, ok=False)
    assert core.redeemed == 0
    bad.unlink()
    assert run("pair", "--core", uri, code=code)["deviceId"] == original["deviceId"]
    assert run("projects") == [project]
    mode = lambda path: path.stat().st_mode & 0o7777
    assert mode(state) == 0o700
    assert mode(state / "edge.sqlite3") == mode(state / ".credentials.key") == 0o600
    with sqlite3.connect(state / "edge.sqlite3") as db:
        secret = db.execute("SELECT credentials FROM identity").fetchone()[0]
    assert original["refreshToken"].encode() not in secret and original["accessToken"].encode() not in secret
    for name in ("Work", "work"):
        folder = scratch / name
        folder.mkdir()
        run("bind", "--project", project["projectId"], "--path", str(folder))
    exe = current_exe
    assert core.redeemed == 1 and run("status")["deviceId"] == original["deviceId"]
    assert run("projects") == [project]
    assert len(run("bindings", "--local")) == 2
    unsafe = scratch / "unsafe"
    unsafe.mkdir()
    unsafe.chmod(0o755)
    run("bindings", "--local", "--state", str(unsafe), ok=False)
    assert not (unsafe / "edge.sqlite3").exists()
    link = scratch / "link"
    link.symlink_to(state, target_is_directory=True)
    run("status", "--state", str(link), ok=False)
    key = state / ".credentials.key"
    key.chmod(0o644)
    run("status", ok=False)
    key.chmod(0o600)
    code = "U1." + str(uuid.uuid4()) + "." + secrets.token_hex(16)
    run("pair", "--core", uri, "--replace", code=code, ok=False)
    attempt, = state.glob(".edge-core-*/device-update.attempt")
    assert mode(attempt) == 0o600 and code.split(".")[-1].encode() not in attempt.read_bytes()
    assert replacement["refreshToken"].encode() not in attempt.read_bytes()
    core.confirm = True
    assert run("pair", "--core", uri, "--replace", code=code)["deviceId"] == replacement["deviceId"]
    assert not attempt.exists() and run("status")["deviceId"] == replacement["deviceId"]

    # Ubuntu user sessions can inherit umask 0002. Use a fresh XDG default, then reproduce
    # the existing group-writable startup path left by preview.1 on Kubuntu.
    original_mask = os.umask(0o002)
    try:
        env.pop("XDG_STATE_HOME")
        home = scratch / "fresh"
        home.mkdir(mode=0o750)
        env["HOME"] = str(home)
        canonical = home / ".local" / "state" / "Nodicarta" / "Edge"
        assert run("bindings", "--local") == []
        assert all(mode(p) == 0o700 for p in [home / ".local", canonical.parent.parent, canonical.parent, canonical])
        home = scratch / "existing"
        home.mkdir(mode=0o750)
        env["HOME"] = str(home)
        canonical = home / ".local" / "state" / "Nodicarta" / "Edge"
        fallback = home / ".Nodicarta" / "Edge"
        canonical.mkdir(parents=True, mode=0o700)
        parents = [home / ".local", canonical.parent.parent, canonical.parent]
        for p in parents:
            p.chmod(0o775)
        marker = canonical / "existing-user-data"
        marker.write_text("keep")
        run("bindings", "--local", ok=False)
        assert marker.read_text() == "keep" and not fallback.exists()
        marker.unlink()  # The failed preview.1 first run left just the empty leaf.
        with ThreadPoolExecutor(4) as pool:
            assert list(pool.map(lambda _: run("bindings", "--local"), range(4))) == [[]] * 4
        assert list(canonical.iterdir()) == [] and all(mode(p) == 0o775 for p in parents) and mode(home) == 0o750
        assert mode(fallback) == mode(fallback.parent) == 0o700 and mode(fallback / "edge.sqlite3") == 0o600
        assert run("pair", "--core", uri, code=str(uuid.uuid4()) + "." + secrets.token_hex(16))["deviceId"] == original["deviceId"]
        for p in parents:
            p.chmod(0o755)
        assert run("status")["deviceId"] == original["deviceId"]  # Formerly unsafe XDG is now safe.
        marker.write_text("keep")
        run("bindings", "--local", ok=False)
        assert run("status", "--state", str(fallback))["deviceId"] == original["deviceId"]
        for p in parents:
            p.chmod(0o775)
        env["XDG_STATE_HOME"] = str(canonical.parent.parent)
        run("bindings", "--local", ok=False)
        assert marker.read_text() == "keep"
        env.pop("XDG_STATE_HOME")
        linked_home = scratch / "linked"
        linked_home.mkdir(mode=0o750)
        env["HOME"] = str(linked_home)
        (linked_home / ".local").symlink_to(home / ".local", target_is_directory=True)
        run("bindings", "--local", ok=False)
        assert not (linked_home / ".Nodicarta").exists()
    finally:
        os.umask(original_mask)
    core.shutdown()
    print("PASS Linux CLI: XDG and private fallback, concurrent startup, umask, permissions, credentials, bindings, restart and device replacement")
