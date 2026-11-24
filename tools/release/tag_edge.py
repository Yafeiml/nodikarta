"""Push an Edge release tag only after local acceptance passes on its exact commit."""
import argparse
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET


def git(*args):
    return subprocess.check_output(["git", *args], text=True).strip()


def clean_head(expected):
    if git("rev-parse", "HEAD") != expected or git("status", "--porcelain"):
        raise ValueError("Run acceptance in a clean checkout of the requested commit.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--commit", required=True, help="full public main commit SHA")
    parser.add_argument("command", nargs=argparse.REMAINDER, help="-- <local acceptance command and arguments>")
    args = parser.parse_args()
    cmd = args.command[1:] if args.command[:1] == ["--"] else args.command
    version = r"(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*)){2}"
    tag = re.fullmatch(rf"v({version})", args.tag) or re.fullmatch(rf"edge-rehearsal-v({version})-[0-9]+", args.tag)
    if not tag or not re.fullmatch(r"[a-f0-9]{40}", args.commit) or not cmd:
        parser.error("Supply a stable or rehearsal tag, its full commit, and a local acceptance command.")
    if Path(git("rev-parse", "--show-toplevel")).resolve() != Path.cwd().resolve():
        raise ValueError("Run this command at the root of the public checkout.")
    ref = "refs/tags/" + args.tag
    def verify_target():
        clean_head(args.commit)
        main = git("ls-remote", "origin", "refs/heads/main").split()
        if main[:1] != [args.commit] or git("ls-remote", "origin", ref):
            raise ValueError("Expected this commit on origin/main and a new, unused tag.")
    verify_target()
    for project in ("ProjectHub.Edge.Desktop", "ProjectHub.Edge"):
        source = Path("src", project, project + ".csproj")
        if ET.parse(source).findtext("./PropertyGroup/Version") != tag.group(1):
            raise ValueError(f"{source} does not match {args.tag}.")
    subprocess.run(cmd, check=True)
    verify_target()
    # Push the tested commit directly. A failed test or failed push leaves no local tag.
    subprocess.run(["git", "push", "origin", args.commit + ":" + ref], check=True)
    print(f"Pushed {args.tag} at {args.commit} after local acceptance.")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))
