# Changelog

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
