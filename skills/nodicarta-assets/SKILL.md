---
name: nodicarta-assets
description: Find, preview, download or publish versioned shared project assets in nodicarta (脉络). Use for requests like “在脉络里找旧版标志” or “download the original logo from nodicarta”.
---

Confirm the project with `list_projects` and check which tools this connection exposes. If only `get_edge_status` is available, read the working directory's access status. Directories bound to the same project share an asset library. Local file operations use the workspace resolved for this chat. When making a poster with a designated project logo, find the original first. Publishing requires both the user's request to share or publish the file and access to `publish_asset`.

**Find and download.** Use `list_assets(query)` to search names, purposes, or tags. For the next page, pass the returned `nextCursor` as `after`. Distinguish candidates by name, purpose, file type, and version. Read metadata with `get_asset(assetId, version)`. Add `includePreview=true` when a thumbnail would help and a preview is available. SVGs and PDFs can be downloaded as originals.

Call `download_asset(assetId, version, destinationPath)` for the selected version. Keep the original format and use a path relative to the receiving workspace's bound directory, such as `design/brand-logo-v2.svg`. If the destination exists, choose a new filename. Report the returned relative path, asset ID, and version as needed.

If the user refers to previous work without an asset name or ID, pass their request to `get_project_snapshot(task)` to find related decisions. If only asset tools are available, start with `list_assets`. Given a deliverable's ID and version, use `get_asset_decisions(assetId, version)` to check its basis. Explain the choice using the decision text and its evidence. The `role=used` entry in `assetRefs` pins the input version for reading or download. A `locator` can identify a page, section, or position in an image. An entry with `isCurrent=false` has a later knowledge revision.

For a reproduction, keep the old deliverable's pinned files and evidence, including the original reason from a superseded decision. For new work, follow `validity.currentDecision` on a superseded entry. Use its item ID and version to read the governing decision and check the scope and evidence before choosing the file. If `versionUseChecks` reports a newer input, its `searchQuery` can locate the adoption decision. Explain the historical version, latest release and the choice approved for this scope. If only publication is recorded, or the directory cannot access evidence of adoption, mark the choice for new work as unconfirmed.

**Publish or update.** Call `publish_asset(requestId, relativePath, name, purpose, tags?)` for the file the user selected, using a path relative to the publisher's bound directory. Supported originals are PNG, JPEG, WebP, SVG, and PDF, up to 8 MiB per file. Describe the purpose in searchable terms, such as "product logo for dark backgrounds". For a first publication, generate a new UUID for `requestId` and read the asset ID and version from Core's successful response.

To update a design, verify the asset ID and current version, then publish with the same `assetId` and the corresponding `baseVersion`. If the upload outcome is uncertain, retry with the original file, parameters, and request ID. On a version conflict, review what others published. If an update is still appropriate, submit it with a fresh request ID and the new base version. Report any required tool or project write permission that is missing.
