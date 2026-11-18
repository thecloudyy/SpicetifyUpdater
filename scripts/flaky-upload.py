"""Local stand-in for uploads.github.com, used by scripts/retry-probe.ps1.

  name=flaky  -> 503, 503, then 201
  name=fatal  -> 401 every time (must not be retried)
  name=dead   -> 503 every time (must give up after three attempts)
  name=landed -> 503 every time, while the release already lists it (must not resend)

GET /stats returns the attempt count per name. Usage: flaky-upload.py [port]
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import parse_qs, urlparse

attempts = {}


class Handler(BaseHTTPRequestHandler):
    def _send(self, code, body):
        data = json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        self._send(200, attempts)

    def do_POST(self):
        name = parse_qs(urlparse(self.path).query).get("name", ["?"])[0]
        length = int(self.headers.get("Content-Length") or 0)
        self.rfile.read(length)
        attempts[name] = attempts.get(name, 0) + 1

        if name == "fatal":
            self._send(401, {"message": "Bad credentials"})
        elif name in ("dead", "landed"):
            self._send(503, {"message": "unavailable"})
        elif attempts[name] < 3:
            self._send(503, {"message": "try again"})
        else:
            self._send(201, {"name": name, "state": "uploaded", "size": length})

    def log_message(self, *args):
        pass


port = int(sys.argv[1]) if len(sys.argv) > 1 else 8899
print("listening on %d" % port, flush=True)
HTTPServer(("127.0.0.1", port), Handler).serve_forever()
