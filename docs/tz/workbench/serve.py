"""Локальная страница обсуждения docs/tz. Запуск: python docs/tz/workbench/serve.py."""

import argparse
import hashlib
import json
import re
import threading
import uuid
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import NamedTemporaryFile
from urllib.parse import urlsplit

HERE = Path(__file__).resolve().parent
TZ = HERE.parent
ROOT = TZ.parents[1]
MARKED = ROOT / "docs/tools/node_modules/marked/lib/marked.umd.js"
LOCK = threading.Lock()


def read_comments():
    return json.loads((HERE / "comments.json").read_text(encoding="utf-8"))


def revision(record):
    return hashlib.sha256(json.dumps(record, sort_keys=True).encode()).hexdigest()


def save_comments(state):
    with NamedTemporaryFile(dir=HERE, delete=False) as draft:
        path = Path(draft.name)
    try:
        path.write_text(json.dumps(state, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        path.replace(HERE / "comments.json")
    finally:
        path.unlink(missing_ok=True)


def current_spec():
    files = []
    for path in sorted(TZ.glob("[0-9][0-9]-*.md")):
        md = path.read_text(encoding="utf-8-sig")
        title = re.search(r"^# (.+)$", md, re.M)
        files.append({"id": path.stem, "file": path.name, "title": title[1] if title else path.stem,
                      "md": md, "items": re.findall(r"\*\*\[([A-Z]+-Q?[\d.]+)\]\*\*", md)})
    return {"files": files, "total": sum(len(f["items"]) for f in files)}


class Handler(BaseHTTPRequestHandler):
    def reply(self, status, body, content_type="application/json; charset=utf-8"):
        payload = body if isinstance(body, bytes) else json.dumps(body, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self):
        route = urlsplit(self.path)
        if route.path == "/api/comments":
            with LOCK:
                records = read_comments()["documents"]
            self.reply(200, [{**r, "revision": revision(r)} for r in records])
        elif route.path == "/data.js":
            snapshot = route.query == "claude"
            data = json.loads((HERE / "claude-snapshot.json").read_text(encoding="utf-8")) if snapshot else current_spec()
            script = "window.SPEC = " + json.dumps(data, ensure_ascii=False).replace("<", "\\u003c") + ";"
            self.reply(200, script.encode(), "text/javascript; charset=utf-8")
        else:
            paths = {"/": (HERE / "index.html", "text/html"),
                     "/index.html": (HERE / "index.html", "text/html"),
                     "/style.css": (HERE / "style.css", "text/css"),
                     "/view.js": (HERE / "view.js", "text/javascript"),
                     "/storage.js": (HERE / "storage.js", "text/javascript"),
                     "/marked.js": (MARKED, "text/javascript")}
            resource = paths.get(route.path)
            if resource and resource[0].is_file():
                content = resource[0].read_bytes()
                if resource[0].name == "index.html":
                    label = "Текущее ТЗ из docs/tz"
                    if route.query == "claude":
                        label = "Снимок Claude от 19.09.2026"
                        content = content.replace(b'src="data.js"', b'src="data.js?claude"')
                    content = content.replace(b"__SOURCE_LABEL__", label.encode())
                self.reply(200, content, resource[1] + "; charset=utf-8")
            else:
                self.reply(404, {"error": "not found"})

    def do_POST(self):
        expected_origin = "http://127.0.0.1:" + str(self.server.server_port)
        if self.headers.get("Origin") != expected_origin or self.path != "/api/comments":
            self.reply(403, {"error": "origin"})
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if not 0 < length <= 1024 * 1024:
                raise ValueError("request size")
            request = json.loads(self.rfile.read(length))
            with LOCK:
                state = read_comments()
                records = state["documents"]
                record = next((r for r in records if r["collection"] == "comments" and r["id"] == request.get("id")), None)
                action = request["action"]
                now = datetime.now(timezone.utc).isoformat()
                if action == "add":
                    data = request["data"]
                    if not isinstance(data.get("text"), str) or not data["text"].strip():
                        raise ValueError("empty comment")
                    if data.get("kind") not in ("answer", "question", "change", "ok", "note"):
                        raise ValueError("comment kind")
                    snapshot = json.loads((HERE / "claude-snapshot.json").read_text(encoding="utf-8"))
                    files = current_spec()["files"] + snapshot["files"]
                    targets = {(f["id"], i) for f in files for i in f["items"] + ["FILE-" + f["id"]]}
                    targets.add(("*", "SPEC-ALL"))
                    if (data.get("file"), data.get("item")) not in targets:
                        raise ValueError("unknown item")
                    records.append({"collection": "comments", "id": uuid.uuid4().hex,
                                    "created_at": now, "updated_at": now, "data": data})
                elif action in ("resolve", "delete"):
                    if record is None or request.get("revision") != revision(record):
                        self.reply(409, {"error": "comment changed, refresh"})
                        return
                    if action == "delete":
                        records.remove(record)
                    else:
                        if not isinstance(request["resolved"], bool):
                            raise ValueError("resolved must be boolean")
                        record["data"]["resolved"] = request["resolved"]
                        record["updated_at"] = now
                else:
                    raise ValueError("unknown action")
                save_comments(state)
            self.reply(200, {"ok": True})
        except (ValueError, KeyError, TypeError) as error:
            self.reply(400, {"error": str(error)})
        except OSError:
            self.reply(500, {"error": "could not save comments"})


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8766)
    args = parser.parse_args()
    if not MARKED.is_file():
        raise SystemExit("Установите зависимости командой npm --prefix docs/tools ci")
    print(f"ТЗ и обсуждение: http://127.0.0.1:{args.port}/", flush=True)
    ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()
