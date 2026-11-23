import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import export_source


class ExportChecks(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.base = Path(temp.name)
        self.root = self.base / "source"
        self.root.mkdir()
        self.manifest = self.root / "manifest.txt"
        self.manifest.write_text("README.md\n", encoding="utf-8")
        self.readme = self.root / "README.md"
        self.readme.write_bytes("A public example.\n".encode())
        self.output = self.base / "public"
        self.enterContext(patch.object(export_source, "ROOT", self.root))
        self.enterContext(patch.object(export_source, "MANIFEST", self.manifest))

    def test_export_uses_only_listed_paths_and_preserves_bytes(self):
        (self.root / ".env").write_text("canary", encoding="utf-8")
        export_source.run(self.output)
        self.assertEqual([self.output / "README.md"], list(self.output.iterdir()))
        self.assertEqual(self.readme.read_bytes(), (self.output / "README.md").read_bytes())
        with self.assertRaisesRegex(ValueError, "already exists"):
            export_source.run(self.output)

    def test_refuses_private_path_and_credentials_before_export(self):
        for name in ("../outside", "artifacts/session.md", "release/private.key", ".env"):
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, "invalid public path"):
                export_source.checked_path(name)
        secret = "ghp_" + "x" * 36
        self.readme.write_text("public\n" + secret, encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "README.md:2: GitHub credential") as error:
            export_source.run(self.output)
        self.assertNotIn(secret, str(error.exception))
        self.assertFalse(self.output.exists())

    def test_linked_sources_are_refused(self):
        linked = self.root / "linked.md"
        try:
            linked.symlink_to(self.readme)
        except OSError:
            self.skipTest("Creating symlinks requires a Windows privilege")
        self.manifest.write_text("linked.md\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "traverses a link"):
            export_source.run(self.output)
        self.assertFalse(self.output.exists())

    def test_unlisted_build_input_prevents_incomplete_export(self):
        sources = self.root / "src"
        sources.mkdir()
        (sources / "NewUpdateClient.cs").write_text("class NewUpdateClient {}", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "build input missing from the public manifest"):
            export_source.run(self.output)
        self.assertFalse(self.output.exists())

    def test_verify_allows_git_metadata_and_rejects_extras_or_drift(self):
        export_source.run(self.output)
        (self.output / ".git").mkdir()
        (self.output / ".git" / "HEAD").write_text("ref: refs/heads/main", encoding="utf-8")
        export_source.run(verify=self.output)
        extra = self.output / ".env"
        extra.write_text("canary", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "unlisted file"):
            export_source.run(verify=self.output)
        extra.unlink()
        (self.output / "README.md").write_text("drifted", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "differs from approved"):
            export_source.run(verify=self.output)


if __name__ == "__main__":
    unittest.main()
