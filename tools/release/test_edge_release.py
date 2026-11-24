import contextlib
import hashlib
import io
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import edge_release as edge


def digest(blob):
    return hashlib.sha256(blob).hexdigest()


class MockGitHub:
    def __init__(self, repo, tag, commit):
        self.draft = {"id": 4, "tag_name": tag, "target_commitish": commit, "draft": True,
                      "upload_url": "https://uploads.github.com/items{?name,label}",
                      "html_url": f"https://github.com/{repo}/releases/tag/{tag}"}
        self.previous = {"id": 3, "tag_name": "v1.1.0"}
        self.assets = {}
        self.contents = {}
        for name in edge.candidates("1.1.1"):
            self.add(name, name.encode())

    def add(self, name, data):
        identifier = len(self.assets) + 10
        self.contents[identifier] = data
        self.assets[name] = {"id": identifier, "name": name, "size": len(data),
                             "digest": "sha256:" + digest(data), "state": "uploaded"}
        return self.assets[name]

    def latest(self):
        return self.previous if self.draft["draft"] else self.draft

    def api(self, path, method="GET", data=None, media=None):
        if path == "/releases/4/assets?per_page=100":
            return list(self.assets.values())
        if path.startswith("https://uploads.github.com/items?name=") and method == "POST":
            return self.add(path.partition("?name=")[2], data)
        if path == "/releases/4" and method == "PATCH":
            assert data == {"draft": False, "prerelease": False, "make_latest": "true"}
            self.draft.update(data)
        assert path == "/releases/4"
        return self.draft.copy()

    def open(self, path, binary):
        assert binary
        return io.BytesIO(self.contents[int(path.rsplit("/", 1)[1])])

    def check_tag(self, tag, commit):
        assert self.draft["tag_name"] == tag and self.draft["target_commitish"] == commit


class Checks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.enterContext(contextlib.redirect_stdout(io.StringIO()))
        self.args = SimpleNamespace(repo=edge.SANDBOX, tag="edge-rehearsal-v1.1.1-12", commit="3" * 40,
                                    dir=Path(self.temp.name), publish_sandbox=False)
        self.api = MockGitHub(self.args.repo, self.args.tag, self.args.commit)
        self.names = edge.candidates("1.1.1")
        edge.fetch(self.args, self.api, self.api.draft, self.names)
        self.args.accept = edge.file_hash(self.args.dir / "receipt.json")

    def promote(self):
        edge.promote(self.args, self.api, self.api.draft.copy(), "1.1.1", True, self.names)

    def test_real_ephemeral_signature_and_resume_on_sandbox(self):
        self.promote()
        self.assertTrue(self.api.draft["draft"])
        self.assertEqual(len(self.api.assets), 5)
        message = self.args.dir / "signed/nodicarta-edge-rehearsal-win-x64.json"
        self.assertIn(self.args.tag, message.read_text())
        self.args.publish_sandbox = True
        self.promote()
        self.promote()
        self.assertEqual(self.api.latest()["id"], 4)

    def test_bytes_reviewed_and_latest_must_match_at_promotion(self):
        self.api.previous["id"] += 1
        with self.assertRaisesRegex(ValueError, "Latest changed"):
            self.promote()
        self.api.previous["id"] -= 1
        (self.args.dir / sorted(self.names)[0]).write_bytes(b"replaced")
        with self.assertRaisesRegex(ValueError, "Locally tested asset"):
            self.promote()
        self.assertEqual(len(self.api.assets), 3)

    def test_refuses_asset_replacement_or_wrong_receipt(self):
        self.args.accept = "7" * 64
        with self.assertRaisesRegex(ValueError, "--accept"):
            self.promote()
        self.args.accept = edge.file_hash(self.args.dir / "receipt.json")
        self.api.assets[sorted(self.names)[0]]["id"] += 1
        with self.assertRaisesRegex(ValueError, "GitHub packages changed"):
            self.promote()

    def test_production_repo_rehearsal_cannot_be_published(self):
        self.args.repo = edge.OFFICIAL
        with tempfile.TemporaryDirectory() as folder:
            self.args.dir = Path(folder)
            self.args.publish_sandbox = True
            edge.fetch(self.args, self.api, self.api.draft, self.names)
            self.args.accept = edge.file_hash(self.args.dir / "receipt.json")
            with self.assertRaisesRegex(ValueError, "forbidden"):
                self.promote()
            self.args.publish_sandbox = False
            # The actual CNG signature is exercised by the release rehearsal.
            with patch.object(edge, "signed_files", side_effect=RuntimeError("CNG boundary")) as signer:
                with self.assertRaisesRegex(RuntimeError, "CNG boundary"):
                    self.promote()
                self.assertEqual(signer.call_args.args[-1], "nodicarta-edge-rehearsal-win-x64.json")
        self.assertEqual(len(self.api.assets), 3)

    def test_production_tags_and_downgrade(self):
        for repo, tag in ((edge.SANDBOX, "v1.2.0"), ("other/project", self.args.tag),
                          (edge.OFFICIAL, "v1.1.0-rc"), (edge.OFFICIAL, "v01.1.0")):
            with self.subTest(repo=repo, tag=tag), self.assertRaises(ValueError):
                edge.tag_details(repo, tag)
        self.assertEqual(edge.tag_details(edge.OFFICIAL, "v1.2.0"), ("1.2.0", False))
        with self.assertRaisesRegex(ValueError, "newer"):
            edge.prior_latest(self.api, {"previousLatest": {"id": 3, "tag": "v1.1.0"}}, "1.1.0", True)


if __name__ == "__main__":
    unittest.main()
