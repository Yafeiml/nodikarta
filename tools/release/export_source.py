"""Audit and export the reviewed public source files, without copying local workspace history."""

import argparse
from pathlib import Path, PurePosixPath
import os
import re
import shutil
import stat
import sys
import tempfile


ROOT = Path(__file__).resolve().parents[2]
MANIFEST = Path(__file__).with_name("public-files.txt")
CHECKS = {
    "private key": r"-----BEGIN (?:[A-Z0-9 ]+ )?PRIVATE KEY-----",
    "GitHub credential": r"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})\b",
    "cloud or API credential": r"\b(?:AKIA[A-Z0-9]{16}|sk-[A-Za-z0-9_-]{32,}|AIza[A-Za-z0-9_-]{35})\b",
    "messaging credential": r"\bxox[baprs]-[A-Za-z0-9-]{20,}\b",
    "literal JSON credential": r'(?i)"(?:api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret)"\s*:\s*"[A-Za-z0-9_+/=.\-]{20,}"',
    "literal environment credential": r"""(?m)^(?:export[ \t]+)?[A-Z0-9_]*(?:PASSWORD|API_KEY|ACCESS_TOKEN|SECRET|PAT)[ \t]*=[ \t]*["']?[A-Za-z0-9_+/=.\-]{20,}["']?[ \t]*$""",
    "signed bearer token": r"\beyJ[A-Za-z0-9_-]{12,}\.eyJ[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{16,}",
    "literal pairing credential": r"\b(?:P1|U1)\.[0-9a-fA-F-]{36}\.[0-9a-fA-F]{24,}",
    "private chat link": r"codex://threads/[0-9a-fA-F-]{36}",
    "local user profile": r"(?i)(?:[A-Z]:[\\/]+Users[\\/]+(?!Public\b|Default\b)[\w.-]+|/Users/[\w.-]+/)",
    "machine-specific tools directory": r"(?i)\b[A-Z]:[\\/]+app[\\/]+devtools\b",
}
RULES = {label: re.compile(pattern) for label, pattern in CHECKS.items()}
REPARSE = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
# New build inputs must be explicitly listed. Otherwise a locally built release
# can contain code that someone building the public snapshot cannot reproduce.
INPUT_TYPES = {
    "src": {".cs", ".csproj", ".cshtml", ".axaml", ".c", ".h", ".props", ".targets",
            ".css", ".js", ".resx", ".xaml", ".svg", ".ico", ".png", ".manifest"},
    "deploy/windows": {".ps1", ".psm1", ".cmd", ".iss", ".nsi", ".nsh", ".wxs", ".md"},
    "deploy/public": {".sh", ".sql", ".yaml", ".yml", ".md"},
    "docs/public": {".md"},
    ".github/workflows": {".yaml", ".yml"},
    "skills": {".md", ".cjs", ".js"},
    "tests": {".cs", ".ps1", ".cjs", ".py"},
    "tools/release": {".ps1", ".sh", ".py", ".mjs"},
    "release/licenses": {".md", ".txt"},
}


def checked_path(name):
    parts = PurePosixPath(name).parts
    if (not parts or name != "/".join(parts) or ".." in parts or ":" in name or "\\" in name
            or any(part.casefold() in {".git", ".codex", ".agents", "bin", "obj", "artifacts",
                                       "node_modules", "__pycache__", "test-results"} for part in parts)
            or any(part.startswith(".env") and part != ".env.example" for part in parts)
            or Path(name).suffix.lower() in {".pfx", ".p12", ".key", ".sqlite", ".db", ".log"}):
        raise ValueError(f"{name}: invalid public path")
    current = ROOT
    for part in parts:
        current = current / part
        details = current.lstat()
        if stat.S_ISLNK(details.st_mode) or getattr(details, "st_file_attributes", 0) & REPARSE:
            raise ValueError(f"{name}: source traverses a link")
    if not stat.S_ISREG(details.st_mode):
        raise ValueError(f"{name}: expected a regular file")
    return current


def audit_bytes(name, data):
    suffix = Path(name).suffix.lower()
    if suffix in {".ico", ".png"}:
        magic = b"\x00\x00\x01\x00" if suffix == ".ico" else b"\x89PNG\r\n\x1a\n"
        if not data.startswith(magic):
            raise ValueError(f"{name}: invalid icon/image header")
        return
    try:
        content = data.decode("utf-8-sig")
    except UnicodeError:
        # Preserve the original NSIS license bytes, including its ANSI copyright sign.
        if name != "release/licenses/edge/NSIS-ModernUI-LICENSE.txt":
            raise ValueError(f"{name}: expected a UTF-8 source file") from None
        content = data.decode("cp1252")
    if "\0" in content:
        raise ValueError(f"{name}: binary content in a text source")
    if ROOT.as_posix().casefold() in content.replace("\\", "/").replace("//", "/").casefold():
        raise ValueError(f"{name}: includes the checkout's absolute path")
    for label, pattern in RULES.items():
        found = pattern.search(content)
        if found:
            line = content.count("\n", 0, found.start()) + 1
            raise ValueError(f"{name}:{line}: {label}")
    if suffix == ".pem":
        if not re.fullmatch(r"-----BEGIN PUBLIC KEY-----\s+[A-Za-z0-9+/=\s]+-----END PUBLIC KEY-----\s*", content):
            raise ValueError(f"{name}: expected only an SPKI public key")


def included_files():
    names = [line.strip() for line in MANIFEST.read_text("utf-8").splitlines()
             if line.strip() and not line.startswith("#")]
    if not names or len({name.casefold() for name in names}) != len(names):
        raise ValueError("Empty public manifest or duplicate path")
    return sorted(names)


def unlisted_inputs(names):
    included = set(names)
    for folder, extensions in INPUT_TYPES.items():
        for directory, children, files in os.walk(ROOT / folder):
            parent = Path(directory)
            children[:] = [child for child in children if not child.startswith(".")
                           and child not in {"bin", "obj", "node_modules", "__pycache__"}
                           and not (getattr((parent / child).lstat(), "st_file_attributes", 0) & REPARSE)]
            for child in files:
                item = parent / child
                if child.startswith(".") or item.suffix.lower() not in extensions:
                    continue
                name = item.relative_to(ROOT).as_posix()
                if name not in included:
                    yield name


def verify_snapshot(root, audited):
    if not root.is_dir() or root.is_symlink():
        raise ValueError("Expected an existing public worktree for verification")
    remaining = set(audited)
    for directory, children, files in os.walk(root):
        parent = Path(directory)
        if parent == root and ".git" in children:
            children.remove(".git")
        for child in children + files:
            path = parent / child
            detail = path.lstat()
            if stat.S_ISLNK(detail.st_mode) or getattr(detail, "st_file_attributes", 0) & REPARSE:
                raise ValueError("Unexpected linked path in the public worktree")
        for child in files:
            name = (parent / child).relative_to(root).as_posix()
            if name == ".git":
                continue
            if name not in remaining:
                raise ValueError("Public worktree has an unlisted file")
            if (parent / child).read_bytes() != audited[name][0]:
                raise ValueError(f"{name}: public worktree differs from approved source")
            remaining.remove(name)
    if remaining:
        raise ValueError("Public worktree is missing: " + ", ".join(sorted(remaining)))


def run(destination=None, verify=None):
    names = included_files()
    audited = {}
    errors = [f"{name}: build input missing from the public manifest" for name in sorted(unlisted_inputs(names))]
    for name in names:
        try:
            path = checked_path(name)
            data = path.read_bytes()
            audit_bytes(name, data)
            audited[name] = (data, stat.S_IMODE(path.stat().st_mode))
        except FileNotFoundError:
            errors.append(f"{name}: missing")
        except (OSError, ValueError) as error:
            errors.append(str(error))
    if errors:
        raise ValueError("\n".join(errors))
    if verify is not None:
        verify_snapshot(verify.absolute(), audited)
    if destination is not None:
        destination = destination.absolute()
        if destination.exists() or destination.is_symlink():
            raise ValueError("Choose a new export directory; the destination already exists")
        destination.parent.mkdir(parents=True, exist_ok=True)
        stage = Path(tempfile.mkdtemp(prefix=".nodicarta-public-", dir=destination.parent)).resolve()
        try:
            for name, (data, mode) in audited.items():
                target = stage / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(data)
                target.chmod(mode)
            if destination.exists() or destination.is_symlink():
                raise ValueError("Export destination appeared during staging")
            stage.rename(destination)
        finally:
            if stage.is_dir() and stage.parent == destination.parent.resolve():
                shutil.rmtree(stage)
    action = "exported" if destination else "verified" if verify else "audited"
    print(f"{len(audited)} public files {action}.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    output = parser.add_mutually_exclusive_group()
    output.add_argument("--destination", type=Path, help="write to this new directory after the complete audit")
    output.add_argument("--verify", type=Path, help="compare an existing public worktree with the audited source")
    args = parser.parse_args()
    try:
        run(args.destination, args.verify)
    except (ValueError, OSError) as err:
        print(err, file=sys.stderr)
        sys.exit(1)
