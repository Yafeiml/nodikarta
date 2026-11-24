"""Download an Edge draft for local qualification, then sign and promote the accepted bytes."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
from urllib.error import HTTPError
from urllib.parse import quote, urlencode, urlsplit
from urllib.request import build_opener, HTTPRedirectHandler, Request


OFFICIAL = "Yafeiml/nodikarta"
SANDBOX = "Yafeiml/nodikarta-release-rehearsal"
ROOT = Path(__file__).resolve().parents[2]
SEMVER = r"(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def tag_details(repo, tag):
    require(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9][A-Za-z0-9._-]*", repo), "Invalid repository.")
    stable = re.fullmatch(r"v(" + SEMVER + ")", tag, re.ASCII)
    rehearsal = re.fullmatch(r"edge-rehearsal-v(" + SEMVER + r")-[0-9]+", tag, re.ASCII)
    require(stable or rehearsal, "Expected a stable or edge-rehearsal version tag.")
    if repo != OFFICIAL:
        require(repo == SANDBOX and rehearsal, "Sandbox operations require the designated private rehearsal repository and tag.")
    return (stable or rehearsal)[1], bool(rehearsal)


def token(repo):
    for var in ("GH_TOKEN", "GITHUB_TOKEN"):
        if os.environ.get(var):
            return os.environ[var]
    if shutil.which("gh"):
        found = subprocess.run(["gh", "auth", "token", "--hostname", "github.com"], capture_output=True, text=True)
        if found.returncode == 0 and found.stdout.strip():
            return found.stdout.strip()
    found = subprocess.run(["git", "-c", "credential.interactive=false", "credential", "fill"],
                           input=f"protocol=https\nhost=github.com\npath={repo}.git\n\n", capture_output=True, text=True,
                           env={**os.environ, "GIT_TERMINAL_PROMPT": "0", "GCM_INTERACTIVE": "never"})
    fields = dict(line.split("=", 1) for line in found.stdout.splitlines() if "=" in line)
    require(found.returncode == 0 and fields.get("password"), "Authenticate to GitHub using gh or Git Credential Manager.")
    return fields["password"]


class AssetRedirect(HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        require(request.get_method() == "GET" and urlsplit(newurl).scheme == "https", "Unexpected GitHub API redirect.")
        target = super().redirect_request(request, fp, code, msg, headers, newurl)
        if target is not None and urlsplit(newurl).netloc != urlsplit(request.full_url).netloc:
            target.remove_header("Authorization")
        return target


class GitHub:
    def __init__(self, repo):
        self.base = "https://api.github.com/repos/" + repo
        self.bearer = token(repo)
        self.client = build_opener(AssetRedirect())

    def open(self, path, method="GET", data=None, media=None, binary=False):
        url = path if path.startswith("https://") else self.base + path
        require(urlsplit(url).netloc in {"api.github.com", "uploads.github.com"}, "Unexpected API host.")
        headers = {"User-Agent": "Nodicarta-Edge-Release", "Authorization": "Bearer " + self.bearer,
                   "Accept": "application/octet-stream" if binary else "application/vnd.github+json",
                   "X-GitHub-Api-Version": "2022-11-28", "Cache-Control": "no-cache"}
        if isinstance(data, dict):
            data, media = json.dumps(data).encode(), "application/json"
        if media:
            headers["Content-Type"] = media
        return self.client.open(Request(url, data=data, headers=headers, method=method), timeout=180)

    def api(self, path, **options):
        with self.open(path, **options) as response:
            return json.load(response)

    def release(self, tag):
        page = 1
        while True:
            batch = self.api(f"/releases?per_page=100&page={page}")
            for item in batch:
                if item["tag_name"] == tag:
                    return item
            if len(batch) < 100:
                raise ValueError(f"Release for {tag} was not found. Check draft access and the GitHub build run.")
            page += 1

    def check_tag(self, tag, commit):
        obj = self.api("/git/ref/tags/" + quote(tag, safe=""))["object"]
        for _ in range(8):
            if obj["type"] != "tag":
                break
            obj = self.api("/git/tags/" + obj["sha"])["object"]
        require(obj["type"] == "commit" and obj["sha"] == commit, "The remote tag does not point to the accepted commit.")

    def latest(self):
        try:
            return self.api("/releases/latest")
        except HTTPError as exc:
            if exc.code == 404:
                return None
            raise


def candidates(version):
    return {f"Nodicarta-Edge-{version}-win-x64-setup.exe", f"Nodicarta-Edge-{version}-linux-x64.tar.gz",
            f"nodicarta-edge_{version}_amd64.deb"}


def assets_for(api, release, expected, optional=()):
    items = api.api(f"/releases/{release['id']}/assets?per_page=100")
    found = {item["name"]: item for item in items}
    extra = set(found) - expected
    require(len(found) == len(items) and expected <= found.keys() and extra.issubset(optional), "Unexpected set of release assets.")
    for item in found.values():
        require(item["state"] == "uploaded" and item["size"] > 0 and
                re.fullmatch(r"sha256:[0-9a-f]{64}", item.get("digest") or ""), "Missing GitHub asset digest.")
    return found


def asset_receipt(found, names):
    return [{"name": n, "id": found[n]["id"], "size": found[n]["size"], "sha256": found[n]["digest"][7:]}
            for n in sorted(names)]


def json_bytes(value):
    return (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode()


def file_hash(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def download(api, entry, dest):
    path = dest / entry["name"]
    if path.exists():
        require(path.is_file() and path.stat().st_size == entry["size"] and file_hash(path) == entry["sha256"],
                f"Existing download differs: {path.name}. Use a fresh directory.")
        return
    with tempfile.TemporaryDirectory(dir=dest) as temp:
        partial = Path(temp) / "download"
        with api.open(f"/releases/assets/{entry['id']}", binary=True) as response, partial.open("wb") as out:
            shutil.copyfileobj(response, out)
        require(partial.stat().st_size == entry["size"] and file_hash(partial) == entry["sha256"],
                f"Downloaded bytes do not match GitHub: {path.name}.")
        partial.replace(path)


def fetch(args, api, release, names):
    require(release["draft"], "Fetch requires a draft release.")
    previous = api.latest()
    require(not previous or previous["id"] != release["id"], "The candidate must be separate from Latest.")
    path = args.dir / "receipt.json"
    channel = "rehearsal" if args.tag.startswith("edge-rehearsal-") else "stable"
    feed = f"nodicarta-edge-{channel}-win-x64.json"
    found = assets_for(api, release, names, {feed, feed + ".sig"} if path.exists() else ())
    receipt = {"repository": args.repo, "tag": args.tag, "commit": args.commit, "releaseId": release["id"],
               "previousLatest": None if previous is None else {"id": previous["id"], "tag": previous["tag_name"]},
               "assets": asset_receipt(found, names)}
    data = json_bytes(receipt)
    require(not path.exists() or path.read_bytes() == data, "This directory contains a different qualification receipt.")
    args.dir.mkdir(parents=True, exist_ok=True)
    for entry in receipt["assets"]:
        download(api, entry, args.dir)
    path.write_bytes(data)
    print(f"Verified three draft packages. After local acceptance, pass --accept {hashlib.sha256(data).hexdigest()}.")


def descriptor(repo, tag, version, channel, exe):
    return {"schema": 1, "product": "nodicarta-edge", "channel": channel, "rid": "win-x64", "version": version,
            "url": f"https://github.com/{repo}/releases/download/{tag}/{exe['name']}", "sha256": exe["sha256"],
            "size": exe["size"]}


def signed_files(args, version, rehearsal, exe, feed):
    # Keep signatures across retries: ECDSA can produce a different valid signature over identical bytes.
    out = args.dir / "signed"
    sandbox = args.repo != OFFICIAL
    spec = descriptor(args.repo, args.tag, version, "rehearsal" if rehearsal else "stable", exe)
    crypto = Path(__file__).with_name("edge_manifest.mjs")
    key = ROOT / "release/edge-update-public.pem"

    def check(folder):
        msg = folder / feed
        require(json.loads(msg.read_bytes()) == spec, "Signed update manifest does not describe the accepted installer.")
        subprocess.run(["node", crypto, "verify", msg, folder / (feed + ".sig"),
                        folder / "sandbox-public.pem" if sandbox else key], check=True)

    if not out.exists():
        with tempfile.TemporaryDirectory(dir=args.dir) as temp:
            stage = Path(temp) / "signed"
            stage.mkdir()
            if sandbox:
                (stage / feed).write_bytes(json_bytes(spec))
                subprocess.run(["node", crypto, "sign-sandbox", stage / feed, stage / (feed + ".sig"),
                                stage / "sandbox-public.pem"], check=True)
            else:
                cmd = ["pwsh", "-NoProfile", "-NonInteractive", "-File", ROOT / "deploy/windows/Sign-EdgeRelease.ps1",
                       "-Installer", args.dir / exe["name"], "-Version", version, "-OutputDirectory", stage]
                if rehearsal:
                    cmd.extend(["-RehearsalTag", args.tag])
                subprocess.run(cmd, check=True)
            check(stage)
            stage.rename(out)
    check(out)
    return out


def prior_latest(api, receipt, version, stable):
    latest = api.latest()
    state = None if latest is None else {"id": latest["id"], "tag": latest["tag_name"]}
    require(state == receipt["previousLatest"], "Latest changed after download. Review a fresh qualification.")
    if stable and latest:
        old = re.fullmatch("v(" + SEMVER + ")", latest["tag_name"], re.ASCII)
        require(old and tuple(map(int, old[1].split("."))) < tuple(map(int, version.split("."))),
                "The proposed stable version must be newer than the existing Latest.")


def promote(args, api, release, version, rehearsal, names):
    path = args.dir / "receipt.json"
    require(path.is_file() and file_hash(path) == args.accept, "--accept must match the locally tested receipt.")
    receipt = json.loads(path.read_bytes())
    require((receipt["repository"], receipt["tag"], receipt["commit"], receipt["releaseId"]) ==
            (args.repo, args.tag, args.commit, release["id"]), "Receipt belongs to another release.")
    sandbox = args.repo != OFFICIAL
    publish = not rehearsal or args.publish_sandbox
    require(not args.publish_sandbox or (sandbox and rehearsal), "Sandbox publication is forbidden on the production repository.")
    require(release["draft"] or publish, "An official rehearsal must remain a draft.")
    if release["draft"]:
        prior_latest(api, receipt, version, not rehearsal)
    channel = "rehearsal" if rehearsal else "stable"
    feed = f"nodicarta-edge-{channel}-win-x64.json"
    signatures = {feed, feed + ".sig"}
    found = assets_for(api, release, names, signatures)
    require(asset_receipt(found, names) == receipt["assets"], "GitHub packages changed after they were tested.")
    for item in receipt["assets"]:
        local = args.dir / item["name"]
        require(local.is_file() and local.stat().st_size == item["size"] and file_hash(local) == item["sha256"],
                f"Locally tested asset is missing or changed: {local.name}.")
    exe = next(item for item in receipt["assets"] if item["name"].endswith(".exe"))
    signed = signed_files(args, version, rehearsal, exe, feed)
    signed_hashes = {name: file_hash(signed / name) for name in signatures}
    for name in sorted(signatures):
        if name not in found:
            require(release["draft"], "Published release is missing its signed assets.")
            url = release["upload_url"].split("{")[0] + "?" + urlencode({"name": name})
            found[name] = api.api(url, method="POST", data=(signed / name).read_bytes(),
                                  media="application/json" if name == feed else "application/octet-stream")
        require(found[name]["digest"] == "sha256:" + signed_hashes[name], f"Remote signature asset differs: {name}.")
    api.check_tag(args.tag, args.commit)
    again = assets_for(api, release, names | signatures)
    require(asset_receipt(again, names) == receipt["assets"] and
            all(again[n]["digest"] == "sha256:" + h for n, h in signed_hashes.items()),
            "Draft assets changed during signing.")
    current = api.api(f"/releases/{release['id']}")
    require(current["tag_name"] == args.tag and current["target_commitish"] == args.commit, "Release target changed.")
    if current["draft"]:
        prior_latest(api, receipt, version, not rehearsal)
        if publish:
            current = api.api(f"/releases/{release['id']}", method="PATCH",
                              data={"draft": False, "prerelease": False, "make_latest": "true"})
    if publish:
        latest = api.latest()
        require(not current["draft"] and latest and latest["id"] == release["id"], "Could not verify new Latest.")
        print(f"Published and verified Latest: {current['html_url']}")
    else:
        require(current["draft"], "Rehearsal is no longer a draft.")
        print(f"Rehearsal verified. Five signed assets are in draft {release['id']}; Latest is unchanged.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    subcommands = parser.add_subparsers(dest="action", required=True)
    for action in ("fetch", "promote"):
        sub = subcommands.add_parser(action)
        sub.add_argument("--repo", default=OFFICIAL)
        sub.add_argument("--tag", required=True)
        sub.add_argument("--commit", required=True, help="full locally tested Git commit")
        sub.add_argument("--dir", required=True, type=Path, help="directory for the original downloads and receipt")
        if action == "promote":
            sub.add_argument("--accept", required=True, help="SHA-256 of receipt.json after local qualification")
            sub.add_argument("--publish-sandbox", action="store_true", help="exercise Latest in a separate private rehearsal repository")
    args = parser.parse_args()
    require(re.fullmatch(r"[0-9a-f]{40}", args.commit), "Pass the full locally tested commit SHA.")
    args.dir = args.dir.resolve()
    version, rehearsal = tag_details(args.repo, args.tag)
    api = GitHub(args.repo)
    if args.repo != OFFICIAL:
        require(api.api("")["private"] is True, "A sandbox rehearsal must use a private repository.")
    api.check_tag(args.tag, args.commit)
    release = api.release(args.tag)
    require(release["target_commitish"] == args.commit, "Draft was not created for the locally tested commit.")
    if args.action == "fetch":
        fetch(args, api, release, candidates(version))
    else:
        promote(args, api, release, version, rehearsal, candidates(version))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        print(error, file=sys.stderr)
        sys.exit(1)
