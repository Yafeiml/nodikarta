# Public source snapshot

`public-files.txt` lists the exact paths approved for the public repository. Keep the installer, its packaged documentation, build inputs, and their regression tests together when adding files.

1. Review the files intended for release and their licenses. Add new public paths to the list explicitly.
2. Run `python tools/release/test_export_source.py` and `python tools/release/export_source.py` from the checkout. Validation errors report paths and line numbers without quoting the matching text.
3. Run `python tools/release/export_source.py --destination ../nodicarta-public` with a fresh destination. All files are validated before writing. The export uses the exact bytes that were checked, and becomes visible in the requested directory when complete.
4. Review the snapshot, build the release from it, and stage it in a clean public checkout. On Windows, stage Linux entry points with their executable Git mode where direct execution is required, e.g. `git update-index --chmod=+x -- path/to/script.sh`.
5. Run `python tools/release/export_source.py --verify ../nodicarta-public` from the source checkout just before publishing. This compares all exported bytes and checks for unexpected files, excluding only the checkout's own Git metadata. Keep build output outside this final worktree.

The scanner checks recognizable credentials, private keys and local development references. An allow-listed binary still needs a provenance review, and a new text fixture needs a human review for private names and content. Keep developer archives, credentials, private workspaces and historical test output outside the public snapshot.
