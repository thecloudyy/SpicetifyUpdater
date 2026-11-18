#!/usr/bin/env python3
"""Publish SHA256SUMS.txt for a release, hashing the files already attached to it.

Usage: GH_TOKEN=<token> python scripts/add_checksums.py [tag]
Without a tag the latest release is used. Re-running replaces the old file.
"""

import hashlib
import json
import os
import sys
import urllib.parse
import urllib.request

REPO = "thecloudyy/SpicetifyUpdater"
CHECKSUM_NAME = "SHA256SUMS.txt"
CHECKSUM_LABEL = "SHA-256 checksums"

TOKEN = os.environ.get("GH_TOKEN", "").strip()
if not TOKEN:
    sys.exit("set GH_TOKEN to a token with write access to releases")

HEADERS = {
    "Authorization": "token " + TOKEN,
    "Accept": "application/vnd.github+json",
    "User-Agent": "SpicetifyUpdater-checksums",
}


def api(url, method="GET", data=None, ctype=None):
    headers = dict(HEADERS)
    if ctype:
        headers["Content-Type"] = ctype
    request = urllib.request.Request(url, data=data, headers=headers, method=method)
    with urllib.request.urlopen(request, timeout=600) as response:
        body = response.read()
        return response.status, (json.loads(body) if body else None)


def download(url):
    request = urllib.request.Request(url, headers={"User-Agent": HEADERS["User-Agent"]})
    with urllib.request.urlopen(request, timeout=600) as response:
        return response.read()


def main():
    tag = sys.argv[1] if len(sys.argv) > 1 else None
    path = "latest" if tag is None else "tags/" + urllib.parse.quote(tag)
    _, release = api(f"https://api.github.com/repos/{REPO}/releases/{path}")
    tag = release["tag_name"]
    print("release:", tag)

    assets = release["assets"]
    existing = [a for a in assets if a["name"] == CHECKSUM_NAME]
    for asset in existing:
        api(f"https://api.github.com/repos/{REPO}/releases/assets/{asset['id']}", "DELETE")
        print("replaced the previous", CHECKSUM_NAME)

    lines = []
    for asset in assets:
        if asset["name"] == CHECKSUM_NAME:
            continue
        blob = download(asset["browser_download_url"])
        digest = hashlib.sha256(blob).hexdigest()
        lines.append(f"{digest}  {asset['name']}")
        print(f"  {digest}  {asset['name']}  ({len(blob)} bytes)")

    if not lines:
        sys.exit("release " + tag + " has no files to hash")

    content = ("\n".join(lines) + "\n").encode()
    url = (f"https://uploads.github.com/repos/{REPO}/releases/{release['id']}/assets"
           f"?name={urllib.parse.quote(CHECKSUM_NAME)}&label={urllib.parse.quote(CHECKSUM_LABEL)}")
    status, uploaded = api(url, "POST", content, "text/plain")
    print("uploaded:", status, uploaded["name"], uploaded["size"], "bytes", uploaded["state"])

    _, again = api(f"https://api.github.com/repos/{REPO}/releases/{path}")
    print("assets on", again["tag_name"] + ":", ", ".join(a["name"] for a in again["assets"]))


if __name__ == "__main__":
    main()
