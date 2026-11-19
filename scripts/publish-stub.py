"""Local stand-in for api.github.com's publish call, used by scripts/publish-probe.ps1.

  PATCH /repos/x/releases/ok     -> 200, draft=false (the release is public)
  PATCH /repos/x/releases/draft  -> 200, draft=true  (the call was ignored)
  PATCH /repos/x/releases/broken -> 422, like a rejected publish

GET /stats returns the PATCH count per release id. Usage: publish-stub.py [port]
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer

patches = {}


class Handler(BaseHTTPRequestHandler):
    def _send(self, code, body):
        data = json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        self._send(200, patches)

    def do_PATCH(self):
        length = int(self.headers.get("Content-Length") or 0)
        self.rfile.read(length)

        rid = self.path.rstrip("/").rsplit("/", 1)[-1]
        patches[rid] = patches.get(rid, 0) + 1

        if rid == "broken":
            self._send(422, {"message": "Validation Failed"})
        else:
            self._send(200, {
                "id": rid,
                "draft": rid == "draft",
                "html_url": "https://example.invalid/releases/ok",
            })

    def log_message(self, *args):
        pass


port = int(sys.argv[1]) if len(sys.argv) > 1 else 8899
print("listening on %d" % port, flush=True)
HTTPServer(("127.0.0.1", port), Handler).serve_forever()
