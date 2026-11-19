# Unity VRC MCP

An [MCP](https://modelcontextprotocol.io) server for Claude Code (or any MCP client) that inspects and drives the Unity
editor. It is aimed at VRChat avatars: debugging animators, expression parameters and menus, clip bindings and the live
state of the Av3Emulator in play mode, and running the VRChat SDK's validation, builds and uploads.

## Install

**[Add to VCC / ALCOM](https://gnimrodg.github.io/unity-vrc-mcp/)** (one click on that page), or add the repository by
hand in ALCOM or the VRChat Creator Companion under **Settings → Packages → Add Repository**:

```
https://raw.githubusercontent.com/GNimrodG/unity-vrc-mcp/vpm/index.json
```

Then:

1. Add **Unity VRC MCP** to your avatar project.
2. Open the project in Unity. The package adds a `unity` server to the project's `.mcp.json` by itself (the menu
   **Tools → Unity VRC MCP → Register in .mcp.json** does it again).
3. Start Claude Code in the project folder and approve the `unity` MCP server.

Requirements: Unity 2022.3, the VRChat SDK 3.4+, Node.js 18+ on `PATH`, and an MCP client such as Claude Code. The
Av3Emulator tools need [Av3Emulator](https://github.com/lyuma/Av3Emulator) 3.4+.

<details>
<summary>Installing from a private copy of this repository</summary>

A private repository's listing and release files need a GitHub token. Add the repository with the header
`Authorization` = `Bearer <token>`, using a fine-grained token limited to that repository with read-only **Contents**
access. The listing tells ALCOM/VCC to send `Accept: application/octet-stream` when it downloads the zips.
</details>

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
| VRChat SDK | `vrc_sdk_status`, `vrc_validate`, `vrc_performance`, `vrc_avatar_info`, `vrc_my_avatars`, `vrc_build`, `vrc_upload`, `vrc_update_avatar` |
| Animator fixes | `edit_animator` (transitions, conditions, states, write defaults, parameters, layers) |
| Code and batches | `execute_code` (C# without a script reload), `batch_execute` |
| Background jobs | `job_status`, `job_cancel` |

Typical Av3 debugging flow:

1. `play_mode {state: "play"}`
2. `av3_state` to see inputs, parameters and the current state of every animator layer
3. `av3_set {params: {GestureLeft: "Fist"}, observeSeconds: 2}` or
   `av3_record {seconds: 3, only: ["Horns*", "FX/Horns"]}` to log what changes
4. `inspect_animator {path: "<avatar>", avatarLayer: "FX", layer: "Horns", detail: "full"}`

In play mode the emulator runs the controllers that VRCFury (and optimizers such as d4rkAvatarOptimizer) build, so layer
and parameter names can differ from the source controllers (`[VFnn]` layer prefixes, `VFnn_` parameters).

## VRChat SDK tools

These need the VRChat Avatars SDK (`com.vrchat.avatars` 3.4+); without it they are left out. They use the SDK's own
builder, validation and API client, so they behave like the SDK Control Panel:

- `vrc_sdk_status`: SDK versions, build target, login state, Control Panel state, scene avatars and their blueprint ids.
- `vrc_validate`: the SDK's pre-upload checks (the errors, warnings and stats the Control Panel lists) for the current
  build target.
- `vrc_performance`: PC and mobile performance ratings with the raw stats.
- `vrc_avatar_info`, `vrc_my_avatars`: avatar records from the VRChat API (release status, versions, platforms, dates).
- `vrc_build`: a local build, or "Build & Test", for the current build target. Runs VRCFury and other build hooks.
  Nothing is uploaded.
- `vrc_upload`, `vrc_update_avatar`: upload a new version (or a new avatar), or change name, description, tags or
  release status. Without `confirm: true` they only return a preview of what would happen.

The tools never handle credentials. When the SDK isn't logged in yet, they log in with the credentials the SDK saved,
exactly as the Control Panel does when it opens, so the panel doesn't need to be open. If nothing is saved (or it
expired), log in once in the SDK Control Panel. API calls, builds and uploads run as background jobs: the tool waits
for them, and `job_status` / `job_cancel` handle long ones.

### Copyright agreement

The SDK asks you to confirm VRChat's ownership agreement once per avatar per Unity session. The `vrc_upload` preview
says whether it will ask and shows the agreement's text. You can answer it in the chat: when you agree there, the agent
passes `agreeCopyright: true` and the agreement is recorded with VRChat exactly as the SDK's OK button does, so Unity
doesn't ask. If you'd rather click it in Unity, or the avatar is brand new (it only gets an id during the upload), the
job reports that it is waiting for you, and the agent tells you to click OK in the SDK Control Panel.

### Several platforms

Without `platforms`, `vrc_upload` uploads for the editor's current build target. With `platforms` (`["all"]`, or any of
`windows`, `android`, `ios`) it runs the SDK's own multi-platform upload, like the Control Panel's platform selector:

- The preview lists which object is built per platform, including the avatar's per-platform overrides (for example a
  separate Quest version), and whether Unity's build support for each platform is installed.
- Unity switches its build target for every other platform. That reloads scripts, and Android/iOS may re-import
  assets for a long time. At the end it switches back to the platform you started on. Keep the SDK Control Panel open.
- The job (`vrc_upload_multi-…`) keeps its id across those reloads. When the chain ends it asks the VRChat API which
  platforms got a new bundle and reports each one.

## Fixing animators, running code

- `edit_animator` makes targeted, undoable edits to a controller: set or add or remove transitions (conditions such as
  `"Toggle"`, `"!Toggle"`, `"Param > 0.5"`, exit time, duration, Can Transition To Self, interruption), states (motion,
  write defaults, speed and time parameters), write defaults for a layer or the whole controller, parameters and layer
  settings. It refuses generated controllers (VRCFury build output, optimizer copies, package files) because those edits
  would be lost on the next build, rejects `==` on Float parameters, and won't remove a parameter that transitions still
  use. New Any State transitions start with Can Transition To Self off.
- `execute_code` compiles a C# snippet with the compiler Unity ships and runs it in the editor. Nothing is written to
  `Assets/` and scripts don't reload, so it also works during play mode and Av3Emulator sessions. The snippet is a
  method body returning a value; `Mcp.Find(path)`, `Mcp.PathOf(obj)` and `Mcp.Log(x)` help with scene objects. Unity's
  bundled compiler only understands older C# (about C# 7).
- `batch_execute` runs up to 50 tool calls in one request.

All tools carry MCP annotations (`readOnlyHint`, `destructiveHint`, `openWorldHint`), so clients can tell the
read-only ones from those that change the project or VRChat.

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

## Credits

`execute_code`'s compile approach is adapted from [MCP for Unity](https://github.com/CoplayDev/unity-mcp) by CoplayDev
(MIT); see `THIRD_PARTY_NOTICES.md`.
