"""The model review window: the user marks the model in 3D, the agent reads the marks as text.

python review.py open <dir> [--no-browser]           show the window for an asset (starts its server in the background)
python review.py notes <dir> [--all]                  the marks to act on (open), or all of them with their threads
python review.py reply <dir> <id> <status|-> <text>   answer a mark: done | question | later | declined | open
python review.py pin <dir> <x,y,z> <text> [object]    the agent's own mark: a question to the user at a point
python review.py serve <dir> [port]                   the server itself, in the foreground (open starts it)

<dir> is the asset's review folder: publish.py fills it (model.glb, report.json, state.json); the window adds
notes.json and the user's reference (ref.png / ref.jpg / ref.webp, ref.glb). Points are Blender's world axes in metres.
Statuses: open - the user wants a change; done - the agent made it, the user checks; question - the agent asks;
later, declined - with the reason in the reply; accepted - the user closed it. Only the user accepts or reopens.
"""
import json
import os
import re
import socket
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
PORTS = range(7330, 7340)
IDLE = 20 * 60  # an open page polls every 1.5 s: this long without a request means nobody looks, the server stops
FILES = re.compile(r"^(model(\.prev)?\.glb|report(\.prev)?\.json|ref\.(glb|png|jpg|jpeg|webp))$")
REF = re.compile(r"^ref\.(glb|png|jpg|jpeg|webp)$")
STATUSES = ("open", "done", "question", "later", "declined", "accepted")
TYPES = {".glb": "model/gltf-binary", ".json": "application/json", ".png": "image/png", ".jpg": "image/jpeg",
         ".jpeg": "image/jpeg", ".webp": "image/webp", ".html": "text/html; charset=utf-8"}
# Blender's views by the side a normal faces: -Y is what the Front view (numpad 1) looks at.
SIDES = {(0, 1): "+X (Right view)", (0, -1): "-X (Left view)", (1, 1): "+Y (Back view)", (1, -1): "-Y (Front view)",
         (2, 1): "+Z (Top view)", (2, -1): "-Z (Bottom view)"}
lock = threading.Lock()


def read_json(path, default):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return default


def write_json(path, data):
    with open(path + ".tmp", "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=1)
    os.replace(path + ".tmp", path)


def now():
    return time.strftime("%Y-%m-%dT%H:%M:%S")


def load(folder):
    return read_json(os.path.join(folder, "notes.json"), {"next": 1, "notes": []})


def revision(folder):
    return read_json(os.path.join(folder, "state.json"), {}).get("rev", 0)


def edit(folder, body, by):
    """One change to the marks: a new one (no id), or to one mark its text, status, a reply, or deletion. Replies are only
    ever appended, so a stale page cannot erase the agent's answer."""
    with lock:
        data, rev = load(folder), revision(folder)
        if not body.get("id"):
            note = {k: body[k] for k in ("kind", "object", "at", "normal", "area", "a", "b", "length", "delta",
                                         "target", "text") if body.get(k) is not None}
            note.update(id="n%d" % data["next"], by=by, status="open" if by == "user" else "question", rev=rev,
                        time=now(), replies=[])
            data["next"] += 1
            data["notes"].append(note)
        else:
            note = next((n for n in data["notes"] if n["id"] == body["id"]), None)
            if note is None:
                raise KeyError("no mark " + body["id"])
            if body.get("delete"):
                data["notes"].remove(note)
            else:
                if body.get("text") is not None:
                    note["text"] = body["text"]
                if body.get("reply"):
                    note["replies"].append({"by": by, "text": body["reply"], "rev": rev, "time": now()})
                if body.get("status") in STATUSES:
                    note["status"] = body["status"]
        write_json(os.path.join(folder, "notes.json"), data)
        return data, note


def page_state(folder, seen):
    files = {n: int(os.path.getmtime(os.path.join(folder, n)) * 1000) for n in sorted(os.listdir(folder))
             if FILES.match(n)}
    return {"folder": folder, "name": os.path.basename(folder), "state": read_json(os.path.join(folder, "state.json"), {}),
            "notes": load(folder)["notes"], "files": files, "page_ago": round(time.time() - seen, 1)}


def serve(folder, port):
    last, seen = [time.time()], [0.0]

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def send(self, code, body, ctype="application/json"):
            if not isinstance(body, bytes):
                body = json.dumps(body, ensure_ascii=False).encode("utf-8")
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def local(self):
            # Only this window's own page: not another site's script through a renamed host or a cross-site post.
            host = self.headers.get("Host") or ""
            origin = self.headers.get("Origin")
            return host.split(":")[0] in ("127.0.0.1", "localhost") and origin in (None, "http://" + host)

        def do_GET(self):
            last[0] = time.time()
            url = urllib.parse.urlparse(self.path)
            if not self.local():
                return self.send(403, {"error": "forbidden"})
            if url.path in ("/", "/index.html"):
                with open(os.path.join(HERE, "viewer.html"), "rb") as f:
                    return self.send(200, f.read(), TYPES[".html"])
            if url.path == "/api/state":
                state = page_state(folder, seen[0])
                if "page" in urllib.parse.parse_qs(url.query):
                    seen[0] = time.time()
                return self.send(200, state)
            name = url.path[3:] if url.path.startswith("/m/") else ""
            if FILES.match(name) and os.path.isfile(os.path.join(folder, name)):
                with open(os.path.join(folder, name), "rb") as f:
                    return self.send(200, f.read(), TYPES[os.path.splitext(name)[1]])
            self.send(404, {"error": "not found"})

        def do_POST(self):
            last[0] = time.time()
            url = urllib.parse.urlparse(self.path)
            if not self.local():
                return self.send(403, {"error": "forbidden"})
            body = self.rfile.read(int(self.headers.get("Content-Length") or 0))
            try:
                if url.path == "/api/note":
                    data, note = edit(folder, json.loads(body), "user")
                    return self.send(200, {"notes": data["notes"], "id": note["id"]})
                name = urllib.parse.parse_qs(url.query).get("name", [""])[0]
                if url.path in ("/api/ref", "/api/unref") and REF.match(name):
                    # One reference image at a time; ref.glb is the model's ghost and lives beside it.
                    for old in os.listdir(folder):
                        if REF.match(old) and (old == name or (name != "ref.glb" and old != "ref.glb")):
                            os.remove(os.path.join(folder, old))
                    if url.path == "/api/ref" and body:
                        with open(os.path.join(folder, name + ".tmp"), "wb") as f:
                            f.write(body)
                        os.replace(os.path.join(folder, name + ".tmp"), os.path.join(folder, name))
                    return self.send(200, page_state(folder, seen[0]))
            except (KeyError, ValueError) as e:
                return self.send(400, {"error": str(e)})
            self.send(404, {"error": "not found"})

    class Server(ThreadingHTTPServer):
        daemon_threads = True
        allow_reuse_address = os.name != "nt"  # on Windows it would let two servers share a port

    httpd = Server(("127.0.0.1", port), Handler)

    def idle():
        while time.time() - last[0] < IDLE:
            time.sleep(15)
        httpd.shutdown()

    threading.Thread(target=idle, daemon=True).start()
    print("serving %s at http://127.0.0.1:%d/" % (folder, port), flush=True)
    httpd.serve_forever()


def probe(port):
    try:
        with urllib.request.urlopen("http://127.0.0.1:%d/api/state" % port, timeout=0.5) as r:
            return json.load(r)
    except (OSError, ValueError):
        return None


def free(port):
    with socket.socket() as s:
        try:
            s.bind(("127.0.0.1", port))
            return True
        except OSError:
            return False


def open_window(folder, browser):
    port = None
    for p in PORTS:
        found = probe(p)
        if found and os.path.normcase(found.get("folder", "")) == os.path.normcase(folder):
            port = p
            break
        if port is None and found is None and free(p):
            port = p
    if port is None:
        raise SystemExit("no free port in %d-%d" % (PORTS[0], PORTS[-1]))
    found = probe(port)
    if not found:
        log = open(os.path.join(folder, "server.log"), "a", encoding="utf-8")
        # Detached, so the server outlives the agent's shell; it stops itself when the page has been closed a while.
        flags = 0x00000008 | 0x00000200 if os.name == "nt" else 0  # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
        subprocess.Popen([sys.executable, os.path.abspath(__file__), "serve", folder, str(port)], stdout=log,
                         stderr=log, stdin=subprocess.DEVNULL, creationflags=flags, start_new_session=os.name != "nt")
        for _ in range(50):
            time.sleep(0.1)
            found = probe(port)
            if found:
                break
        if not found:
            raise SystemExit("the server did not start: see %s" % os.path.join(folder, "server.log"))
    url = "http://127.0.0.1:%d/" % port
    if found["page_ago"] < 5:
        print("open in the browser already: " + url)
    else:
        print(url)
        if browser:
            webbrowser.open(url)


def fmt(p):
    return "(%s)" % ", ".join("%.3f" % c for c in p)


def facing(n):
    if not n:
        return ""
    axis = max(range(3), key=lambda i: abs(n[i]))
    return " facing " + SIDES[(axis, 1 if n[axis] > 0 else -1)]


def describe(n):
    kind = n.get("kind", "point")
    if kind == "area":
        a = n["area"]
        size = " x ".join("%.2f" % (hi - lo) for lo, hi in zip(a["min"], a["max"]))
        where = "%s around %s, strokes over %s m with a %.0f mm brush%s" % (
            ", ".join(a["objects"]), fmt(n.get("at") or a["center"]), size, a["radius"] * 1000, facing(n.get("normal")))
    elif kind == "measure":
        where = "%s to %s: %.0f mm" % (fmt(n["a"]), fmt(n["b"]), n["length"] * 1000)
        if n.get("delta"):
            where += " (X %.0f, Y %.0f, Z %.0f)" % tuple(abs(d) * 1000 for d in n["delta"])
        if n.get("target"):
            where += ", should be %.0f mm (%+.0f mm)" % (n["target"] * 1000, (n["target"] - n["length"]) * 1000)
    else:
        where = "%s at %s%s" % (n.get("object") or "?", fmt(n["at"]), facing(n.get("normal")))
    line = "%s %s %s %s, rev %s: %s" % (n["id"], n["status"], kind, where, n.get("rev", "?"), n.get("text") or "-")
    return [line] + ["   %s rev %s: %s" % (r["by"], r.get("rev", "?"), r["text"]) for r in n.get("replies", [])]


def main(argv):
    if len(argv) < 3 or argv[1] not in ("open", "notes", "reply", "pin", "serve"):
        print(__doc__)
        return 2
    cmd, folder = argv[1], os.path.abspath(argv[2])
    os.makedirs(folder, exist_ok=True)
    if cmd == "serve":
        serve(folder, int(argv[3]) if len(argv) > 3 else PORTS[0])
    elif cmd == "open":
        open_window(folder, "--no-browser" not in argv)
    elif cmd == "notes":
        notes = load(folder)["notes"]
        todo = [n for n in notes if "--all" in argv or n["status"] == "open"]
        counts = {}
        for n in notes:
            counts[n["status"]] = counts.get(n["status"], 0) + 1
        print("rev %s: %d marks (%s)" % (revision(folder), len(notes),
                                         ", ".join("%d %s" % (c, s) for s, c in sorted(counts.items())) or "none"))
        for n in todo:
            print("\n".join(describe(n)))
    elif cmd == "reply" and len(argv) > 4:
        status = argv[4] if argv[4] != "-" else None
        if status and status not in STATUSES:
            raise SystemExit("status: " + ", ".join(STATUSES))
        _, note = edit(folder, {"id": argv[3], "status": status, "reply": " ".join(argv[5:]) or None}, "agent")
        print("\n".join(describe(note)))
    elif cmd == "pin" and len(argv) > 4:
        at = [float(c) for c in argv[3].split(",")]
        _, note = edit(folder, {"kind": "point", "at": at, "text": argv[4],
                                "object": argv[5] if len(argv) > 5 else None}, "agent")
        print("\n".join(describe(note)))
    else:
        print(__doc__)
        return 2
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main(sys.argv))
