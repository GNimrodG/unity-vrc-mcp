# Changelog

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
