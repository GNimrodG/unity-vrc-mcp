# Changelog

## 1.2.1 - 2026-11-19

- `avatar_report` is compact by default (about 800 tokens instead of 5,000 for a large avatar); `sections` adds
  parameters, components, VRCFury components, per-mesh detail or materials. Without `path` and with several avatars it
  returns one summary line each (`all: true` for full reports). Triangle counts now count what is on when that avatar
  is worn, so inactive avatars in the scene no longer show 0. `listParameters` still works.
- Tool results are capped at 40,000 characters (`UNITY_MCP_MAX_CHARS` overrides), and big results drop their
  indentation before anything is cut.
- VRChat SDK tools log in with the SDK's saved credentials when needed, instead of reporting "not logged in" until the
  Control Panel has been opened.
- Fixed: `vrc_upload` always failed with "Failed to upload file" (since 1.1.0). The job's progress handler read Unity's
  editor clock on the SDK's upload thread, which throws and aborted the SDK's file upload. Job timing now uses a
  thread-safe clock, and builder event handlers never throw into the SDK.
- VRChat's copyright agreement can be answered in the chat: the `vrc_upload` preview says whether the SDK will ask
  (once per avatar per Unity session) and shows the agreement text. With `agreeCopyright: true` (only after the user
  agreed in the conversation) it is recorded with VRChat exactly like the SDK's OK button, so Unity doesn't ask.
  Otherwise the job reports that it is waiting for the user to click OK in Unity, and the MCP server hands that back
  at once instead of waiting silently. A native Unity dialog that freezes the editor is detected within seconds.
- Builds and uploads now capture the SDK's own error and warning logs (HTTP failures, the failing step) into the job's
  log (`errorDetails` when the job fails, `consoleErrors` when it still succeeds); pre-signed upload URLs are shortened
  to host and path.

## 1.2.0 - 2026-11-19

- `execute_code`: compile and run a C# snippet in the editor without a script reload, so it works during play mode
  and Av3Emulator sessions (compile approach adapted from MCP for Unity, MIT; see THIRD_PARTY_NOTICES.md).
- `edit_animator`: undoable edits to source animator controllers (transitions and conditions, Can Transition To Self,
  states, write defaults, parameters, layers); refuses VRCFury/optimizer output and package files.
- `batch_execute`: up to 50 tool calls in one request.
- MCP tool annotations (read-only / destructive / open-world hints) on every tool.

## 1.1.0 - 2026-11-18

- VRChat SDK tools (only when com.vrchat.avatars 3.4+ is installed): SDK/login status, the SDK's own avatar validation,
  PC and mobile performance ratings, avatar records and the user's avatar list from the VRChat API, local builds and
  Build & Test, uploads and avatar info updates (preview by default, act only with `confirm: true`).
- Multi-platform uploads (`vrc_upload` with `platforms`): runs the SDK's multi-platform chain with per-platform
  overrides, follows it across the platform-switch script reloads, and verifies each platform's new bundle on the
  VRChat API at the end.
- Background jobs (`job_status`, `job_cancel`) for API calls, builds and uploads; the MCP server waits for them and
  reports when a Unity dialog blocks the editor.
- The VPM listing branch also carries a landing page with a one-click "Add to VCC / ALCOM" link (for GitHub Pages).
- `inspect_animator` flags Any State transitions that can restart their own state (already in 1.0.0, now documented).

## 1.0.0 - 2026-11-18

First release.

- Unity editor bridge (loopback HTTP, per-project token) and zero-dependency MCP server.
- Editor tools: status, console, hierarchy, object/asset inspection, property and blendshape editing, asset search,
  selection, menu items, play mode, asset refresh with compile results, static method invocation, view capture.
- Avatar tools: avatar report with issue list, animator analysis (write defaults, parameter checks, clip bindings),
  clip inspection.
- Av3Emulator tools: live state, setting inputs and parameters, expression menu map, change recorder.
- Writes the `unity` server entry into the project's `.mcp.json` automatically.
