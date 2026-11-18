# Unity VRC MCP

An [MCP](https://modelcontextprotocol.io) server for Claude Code (or any MCP client) that inspects and drives the Unity
editor. It is aimed at debugging VRChat avatars: animators, expression parameters and menus, clip bindings, and the live
state of the Av3Emulator in play mode.

## Install

1. In ALCOM (or the VRChat Creator Companion): **Settings → Packages → Add Repository** and add
   `https://raw.githubusercontent.com/GNimrodG/unity-vrc-mcp/vpm/index.json`.
   While the repository is private, add a header to it there: `Authorization` = `Bearer <token>`, using a
   fine-grained GitHub token limited to this repository with read-only **Contents** access. The listing itself tells
   ALCOM to send `Accept: application/octet-stream` when it downloads the zips.
2. Add **Unity VRC MCP** to your avatar project.
3. Open the project in Unity once. The package writes an `unity` entry into the project's `.mcp.json`
   (menu **Tools → Unity VRC MCP → Register in .mcp.json** does it again).
4. Start Claude Code in the project folder and approve the `unity` MCP server.

Requirements: Unity 2022.3, the VRChat SDK (any 3.4+), Node.js 18+ on `PATH`. The Av3Emulator tools need
[Av3Emulator](https://github.com/lyuma/Av3Emulator) 3.4+.

## How it works

- `Editor/` is an editor-only assembly that runs a small HTTP bridge inside Unity on `127.0.0.1:47811`. Requests need
  the random token in `Library/UnityVrcMcp/token`, which the bridge creates on first load. Because the bridge is its own
  assembly, it keeps running and reports the errors when the project's other scripts fail to compile.
- `Server~/server.mjs` is a dependency-free MCP server (stdio). Claude Code starts it from `.mcp.json`. It forwards tool
  calls to the bridge and waits through script compiles and domain reloads.

Unity only compiles changed scripts after its window has had focus at least once, so click into Unity after installing
or updating the package.

## Tools

| Area | Tools |
|---|---|
| Editor | `unity_status`, `console_logs`, `refresh_assets`, `play_mode`, `execute_menu_item`, `invoke_static_method` |
| Scene and assets | `hierarchy`, `find_objects`, `inspect_object`, `set_property`, `blendshapes`, `find_assets`, `select`, `capture_view` |
| Avatar (edit mode) | `avatar_report`, `inspect_animator` (write defaults, condition/parameter checks, clip bindings vs. the avatar), `inspect_clip`, `av3_menu` |
| Av3Emulator (play mode) | `av3_state`, `av3_set`, `av3_record`, `av3_menu` (with live values) |

Typical Av3 debugging flow:

1. `play_mode {state: "play"}`
2. `av3_state` to see inputs, parameters and the current state of every animator layer
3. `av3_set {params: {GestureLeft: "Fist"}, observeSeconds: 2}` or
   `av3_record {seconds: 3, only: ["Horns*", "FX/Horns"]}` to log what changes
4. `inspect_animator {path: "<avatar>", avatarLayer: "FX", layer: "Horns", detail: "full"}`

In play mode the emulator runs the controllers that VRCFury (and optimizers such as d4rkAvatarOptimizer) build, so layer
and parameter names can differ from the source controllers (`[VFnn]` layer prefixes, `VFnn_` parameters).

## Several projects at once

Each open Unity project needs its own port. Put the number in `ProjectSettings/UnityVrcMcpPort.txt`; the MCP server
reads the same file. `UNITY_MCP_PORT` and `UNITY_PROJECT_DIR` override the port and project folder.

## Security

The bridge listens on loopback only, checks the `Host` header and requires the per-project token, so web pages can't
call it. Anything that can read the project's `Library` folder can, and the bridge can run editor code
(`invoke_static_method`, `execute_menu_item`), so treat that token like a password.

## Releasing

Bump `version` in `package.json`, add a `CHANGELOG.md` entry, then push a tag `v<version>`. The `Release` workflow
builds the zip and creates the GitHub release; the `Listing` workflow then rebuilds `index.json` from all releases and
force-pushes it to the `vpm` branch. If the repository becomes public, run `Listing` once (Actions → Listing → Run
workflow) so the zip URLs switch to plain download links that need no token.
