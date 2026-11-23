---
name: nodicarta-sync
description: Save a reusable project decision, verified result or reproducible limitation and its evidence in nodicarta (脉络) when finishing work in a bound workspace. Examples include “把验收结论记到脉络” and “save this decision to nodicarta”.
---

At the end of the task, consider what a fresh chat could safely rely on. An explicit request to remember is optional. Skip routine activity, suggestions, and repeated checks that add no evidence. Respect requests to keep the work local. Keep project knowledge in the working language of the project.

Confirm the project with `list_projects` and look up related entries with `search_project_knowledge(query)`. When comparing against a previous check, identify the entry that supplied the baseline. If this work changes a claim about the same subject and target, revise that entry even when the original also covered other findings. A genuinely different subject or target can have its own entry. Both the device and the client need write access.

Write the current conclusion so it makes sense on another machine. For an installation or test, name the observed device or environment and when you checked it (including the time zone). For a local check, read the machine name so a later reader can identify “this computer”. A submitter can report on another device. Separate observations from assumptions and from decisions. Prefer paths relative to the workspace or user home.

When the source establishes how a claim applies, include `validity` in the commit:

- Use `{"kind":"fact"}` for an event that already happened, such as which input a delivered design used. Keep its original evidence and pinned versions.
- For an adopted rule, use `kind="decision"` with its explicit `scope`. A new publication or suggestion alone does not establish adoption. If an independent decision explicitly replaces another in the same scope, read the predecessor's latest revision and provide its `supersedesItemId` and `supersedesVersion` in the new decision's validity. To correct the existing entry itself, use the usual top-level `itemId` and `baseVersion`.
- For measurements, reuse the original item for the same device and the same measured property. Use `kind="observation"`, the actual `subject` (device or environment), and `observedAt` copied from the measurement, including the UTC offset. Cite the measurement in `evidence`. For any change to an existing observation's title, body, evidence or asset references, explicitly include its `validity`. When correcting the record for the same measurement, keep its original observation time. An offline query, a file's publish date or a reread of old notes supplies no new measurement.

Keep distinct claims separate when their scope or lifetime differs. A source with no reliable classification can remain unclassified. There is no expiry date to enter.

If an open Codex chat cannot see newly configured MCP tools yet, run the bundled `scripts/call-knowledge.cjs` from the chat's working directory, using the script path relative to this skill. Pass the tool name as the only argument and its JSON parameters on standard input. It asks Codex about the project bound to that directory without starting a model turn. If the directory is unbound, its ownership is ambiguous, or access was revoked, report the status Edge returns. Keep the submission in its original project.

Call `submit_knowledge_commit` with a fresh UUID as `requestId`, a specific `title`, a `body`, and `evidence`. For a revision, read `get_knowledge_history(itemId)` and pass the existing **itemId and its latest version as baseVersion in the tool arguments**. Omitting the ID creates a separate entry even if the text cites the old ID. The new body and evidence replace those fields in the current view, so carry forward still-valid findings and their sources. Date the change so the old observation remains intelligible. For a new entry start at `baseVersion=0`. Include `relatedTaskId` when the entry has a known task. Omit secrets, personal information, and full conversations.

If a decision refers to published project assets, pin their actual IDs and versions in the optional `assetRefs` array. Each reference takes `role`, `assetId`, and `version`, plus an optional `locator` such as "page 3, top right". Use `deliverable` for the output the decision applies to, `used` for the original actually used, and `evidence` for the supporting material. Each decision accepts up to 32 references and at most one deliverable. For example, if poster v3 uses logo v1, reference poster v3 as the deliverable and logo v1 as used, then explain the choice in the body. Check the version with `get_asset(assetId, version)` when needed. For local-only material, describe the source in `evidence`. Upload files only within the user's sharing scope.

Omitting `assetRefs` on update retains the preceding revision's references. To change them, check the history and submit the complete replacement array. Pass `[]` only to remove all references intentionally. Use `get_asset_decisions(assetId, version)` to read the decision back from its deliverable.

After a timeout, retry with identical content and the same `requestId`. If Edge returns `queuedLocally`, report the request ID as pending sync. Report the entry and version as saved after Core acknowledges them. On version conflict, read the latest history and preserve the evidence from both sides. Submit any reconciled change with a new request ID and the latest base version.
