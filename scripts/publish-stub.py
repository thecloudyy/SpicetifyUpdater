"""Local stand-in for api.github.com's publish call, used by scripts/publish-probe.ps1.

  PATCH /repos/x/releases/ok     -> 200, the release is public (draft=false)
  PATCH /repos/x/releases/broken -> 422, like a rejected publish
  PATCH /repos/x/releases/draft  -> 200, draft=true (the call was ignored)
  PATCH /repos/x/releases/lost   -> the release is published and then the connection is dropped,
                                    so the caller sees a lost response for one that did go public

GET /repos/x/releases/<id> returns that release's current draft flag, DELETE removes it, and
GET /stats and GET /deletes return how many PATCH and DELETE calls each id received.

Usage: publish-stub.py [port]
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer

releases = {}
patches = {}
deletes = {}


class Handler(BaseHTTPRequestHandler):
    def _send(self, code, body):
        data = json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _id(self):
        return self.path.split("?")[0].rstrip("/").rsplit("/", 1)[-1]

    def do_GET(self):
        if self.path.startswith("/repos/"):
            rid = self._id()
            if rid not in releases:
                self._send(404, {"message": "Not Found"})
                return
            self._send(200, {
                "id": rid,
                "draft": releases[rid],
                "html_url": "https://example.invalid/releases/" + rid,
            })
        elif self.path.rstrip("/") == "/deletes":
            self._send(200, deletes)
        else:
            self._send(200, patches)

    def do_PATCH(self):
        length = int(self.headers.get("Content-Length") or 0)
        self.rfile.read(length)

        rid = self._id()
        patches[rid] = patches.get(rid, 0) + 1

        if rid == "broken":
            # The draft exists; it is the publish that was rejected, so it stays unpublished.
            releases[rid] = True
            self._send(422, {"message": "Validation Failed"})
            return

        releases[rid] = rid == "draft"

        if rid == "lost":
            # Published for real, but the answer never arrives: the caller is left not knowing.
            self.close_connection = True
            try:
                self.connection.close()
            except OSError:
                pass
            return

        self._send(200, {
            "id": rid,
            "draft": releases[rid],
            "html_url": "https://example.invalid/releases/" + rid,
        })

    def do_DELETE(self):
        rid = self._id()
        deletes[rid] = deletes.get(rid, 0) + 1
        releases.pop(rid, None)
        self.send_response(204)
        self.end_headers()

    def log_message(self, *args):
        pass


port = int(sys.argv[1]) if len(sys.argv) > 1 else 8899
print("listening on %d" % port, flush=True)
HTTPServer(("127.0.0.1", port), Handler).serve_forever()
