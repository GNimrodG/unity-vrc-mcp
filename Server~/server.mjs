#!/usr/bin/env node
// Zero-dependency MCP server (stdio, JSON-RPC 2.0) that forwards tool calls to the
// Unity VRC MCP bridge running inside the Unity editor (package com.data-unknown.unity-vrc-mcp). Node 18+.
//
// Project dir: $UNITY_PROJECT_DIR, else the nearest folder above this file that holds Assets/ and ProjectSettings/.
// Port: $UNITY_MCP_PORT, else <project>/ProjectSettings/UnityVrcMcpPort.txt, else 47811.
// Auth: token from <project>/Library/UnityVrcMcp/token (written by the bridge).

import { existsSync, readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';
import readline from 'node:readline';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const findProject = (start) => {
  for (let dir = start; ; dir = path.dirname(dir)) {
    if (existsSync(path.join(dir, 'Assets')) && existsSync(path.join(dir, 'ProjectSettings'))) return dir;
    if (path.dirname(dir) === dir) return process.cwd();
  }
};
const projectDir = path.resolve(process.env.UNITY_PROJECT_DIR || findProject(here));
const VERSION = (() => {
  try { return createRequire(import.meta.url)('../package.json').version; } catch { return '0.0.0'; }
})();
const MAX_TEXT = 90_000;

const port = () => {
  if (process.env.UNITY_MCP_PORT) return Number(process.env.UNITY_MCP_PORT);
  const f = path.join(projectDir, 'ProjectSettings', 'UnityVrcMcpPort.txt');
  if (existsSync(f)) {
    const p = Number.parseInt(readFileSync(f, 'utf8').trim(), 10);
    if (p > 0 && p < 65536) return p;
  }
  return 47811;
};

const token = () => {
  const f = path.join(projectDir, 'Library', 'UnityVrcMcp', 'token');
  return existsSync(f) ? readFileSync(f, 'utf8').trim() : null;
};

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const norm = (p) => path.resolve(p).toLowerCase();

class BridgeDown extends Error {}

async function http(method, pathname, body, timeoutMs = 130_000) {
  const tok = token();
  if (!tok) throw new BridgeDown(`No bridge token at ${projectDir}/Library/UnityVrcMcp/token. Open the project in Unity so Assets/UnityVrcMcp compiles.`);
  let res;
  try {
    res = await fetch(`http://127.0.0.1:${port()}${pathname}`, {
      method,
      headers: { 'X-Unity-Vrc-Mcp-Token': tok, 'Content-Type': 'application/json' },
      body: body ? JSON.stringify(body) : undefined,
      signal: AbortSignal.timeout(timeoutMs),
    });
  } catch (e) {
    if (e.name === 'TimeoutError') throw new Error(`Unity did not answer within ${timeoutMs / 1000}s.`);
    throw new BridgeDown(`Unity bridge not reachable on 127.0.0.1:${port()} (${e.cause?.code || e.message}). Unity may be closed, compiling or reloading scripts.`);
  }
  const text = await res.text();
  let json;
  try { json = JSON.parse(text); } catch { throw new Error(`Bad response (HTTP ${res.status}): ${text.slice(0, 500)}`); }
  if (res.status === 401) throw new Error('Bridge rejected the token (another Unity project on this port?).');
  return json;
}

async function health() {
  const h = await http('GET', '/health', null, 5000);
  if (h.project && norm(h.project) !== norm(projectDir))
    throw new Error(`Port ${port()} is served by a different Unity project (${h.project}). Set ProjectSettings/UnityVrcMcpPort.txt per project.`);
  return h;
}

/** Waits until the editor answers and is neither compiling nor importing (domain reloads included). */
async function waitIdle(maxMs = 180_000, minMs = 1500) {
  const start = Date.now();
  let stable = 0;
  let last = 'unreachable';
  while (Date.now() - start < maxMs) {
    try {
      const h = await health();
      last = h.isCompiling ? 'compiling' : h.isUpdating ? 'importing' : 'idle';
      stable = last === 'idle' ? stable + 1 : 0;
      if (stable >= 3 && Date.now() - start >= minMs) return h;
    } catch (e) {
      if (!(e instanceof BridgeDown)) throw e;
      stable = 0;
      last = 'unreachable';
    }
    await sleep(500);
  }
  throw new Error(`Unity did not become idle within ${maxMs / 1000}s (last state: ${last}).`);
}

async function call(tool, args) {
  const attempt = async () => {
    await health();
    const r = await http('POST', '/call', { tool, args });
    if (!r.ok) {
      const err = new Error(r.error + (r.stack ? `\n${r.stack}` : ''));
      err.unity = true;
      if (/reloading scripts/.test(r.error)) err.retry = true;
      throw err;
    }
    return r.result;
  };
  try {
    return await attempt();
  } catch (e) {
    if (!(e instanceof BridgeDown) && !e.retry) throw e;
    await waitIdle(30_000, 0); // domain reload / compile in progress: wait and retry once
    return await attempt();
  }
}

// ---------------------------------------------------------------- tools

const target = {
  path: { type: 'string', description: "Scene GameObject path like 'Dergal/Armature/Hips', a unique object name, or '#instanceId'." },
  assetPath: { type: 'string', description: "Asset path like 'Assets/Dergal/x.prefab' (instead of path)." },
  subAsset: { type: 'string', description: 'Name of a sub-asset inside assetPath (e.g. a mesh inside an FBX).' },
  childPath: { type: 'string', description: 'For a prefab/model assetPath: child object path inside it.' },
  component: { type: 'string', description: 'Component type name (e.g. SkinnedMeshRenderer, VRCPhysBone, VRCAvatarDescriptor). Base types match too.' },
  index: { type: 'integer', description: 'Which component when several match (default 0).' },
};
const obj = (properties, required = []) => ({ type: 'object', properties, required });

const tools = [
  {
    name: 'unity_status',
    description: 'Editor state: project, Unity version, play mode, compiling, open scenes and their root objects, selection, console error/warning counts and current C# compile errors.',
    inputSchema: obj({}),
  },
  {
    name: 'console_logs',
    description: 'Read the Unity Console (newest entries, returned oldest-first), with stack traces for errors/warnings. Can also clear it.',
    inputSchema: obj({
      types: { type: 'string', description: "Comma list of error,warning,log (default all)." },
      limit: { type: 'integer', description: 'Max entries (default 30).' },
      contains: { type: 'string', description: 'Only entries containing this text.' },
      stackTrace: { type: 'boolean', description: 'Include stack traces for errors/warnings (default true).' },
      collapse: { type: 'boolean', description: 'Merge identical messages with a count (default true).' },
      clear: { type: 'boolean', description: 'Clear the console instead of reading it.' },
    }),
  },
  {
    name: 'hierarchy',
    description: 'GameObject tree of the open scenes (and prefab stage), or of one object.',
    inputSchema: obj({
      path: target.path,
      depth: { type: 'integer', description: 'Levels below the start (default 2).' },
      components: { type: 'boolean', description: 'List component types per object.' },
      includeInactive: { type: 'boolean', description: 'Default true.' },
      maxNodes: { type: 'integer', description: 'Default 400.' },
    }),
  },
  {
    name: 'find_objects',
    description: "Find GameObjects by name substring and/or component type (use component 'missing' for missing scripts).",
    inputSchema: obj({
      name: { type: 'string' },
      component: { type: 'string' },
      under: { type: 'string', description: 'Only search below this GameObject path.' },
      limit: { type: 'integer', description: 'Default 100.' },
    }),
  },
  {
    name: 'inspect_object',
    description: 'Inspect a GameObject (transform, components, children) or dump the serialized properties of a component or asset (materials, VRC components, ScriptableObjects, ...).',
    inputSchema: obj({
      ...target,
      property: { type: 'string', description: "Dump only this serialized property path (e.g. 'm_Materials' or 'parameters.Array.data[3]')." },
      filter: { type: 'string', description: 'Only top-level properties whose name contains this.' },
      depth: { type: 'integer', description: 'Nesting depth (default 4).' },
      maxArray: { type: 'integer', description: 'Array elements shown (default 32).' },
      includeHidden: { type: 'boolean', description: 'Include hidden serialized fields.' },
    }),
  },
  {
    name: 'set_property',
    description: 'Set a serialized property on a component or asset (undoable, not saved). Values: number, bool, string, enum name, [x,y,z], [r,g,b,a] or "#hex", object reference as asset path ("Assets/a.fbx::MeshName" for sub-assets) or scene path, null. An int on an array property sets its size.',
    inputSchema: obj({ ...target, property: { type: 'string' }, value: {} }, ['property', 'value']),
  },
  {
    name: 'blendshapes',
    description: 'List (and optionally set) blendshape weights of a SkinnedMeshRenderer.',
    inputSchema: obj({
      path: target.path,
      filter: { type: 'string' },
      nonZeroOnly: { type: 'boolean' },
      limit: { type: 'integer' },
      set: { type: 'object', description: 'Map of blendshape name -> weight (0-100) to set (undoable).' },
    }, ['path']),
  },
  {
    name: 'find_assets',
    description: "AssetDatabase search, e.g. 't:AnimatorController', 't:Material Body', 'l:label', 't:AnimationClip Tongue'.",
    inputSchema: obj({ filter: { type: 'string' }, folder: { type: 'string', description: "e.g. 'Assets/Dergal'" }, limit: { type: 'integer' } }, ['filter']),
  },
  {
    name: 'select',
    description: 'Select and ping an object or asset in the editor (optionally frame it in the Scene view).',
    inputSchema: obj({ ...target, ping: { type: 'boolean' }, frame: { type: 'boolean' } }),
  },
  {
    name: 'avatar_report',
    description: 'VRChat avatar overview + automatic issue list: descriptor (view position, lip sync, playable layers, expression menu/parameters with synced bit cost), root Animator/rig, component counts, missing scripts, VRCFury components, skinned meshes (tris, blendshapes, bones, materials), shaders, PhysBone/contact counts.',
    inputSchema: obj({
      path: { type: 'string', description: 'Avatar root; omit for every VRCAvatarDescriptor in the open scenes.' },
      listParameters: { type: 'boolean', description: 'List every expression parameter (default true).' },
    }),
  },
  {
    name: 'inspect_animator',
    description: 'Analyze an AnimatorController: parameters, layers, states (motion, write defaults, behaviours), transitions, and issues (mixed WD, undefined/mistyped condition parameters, blend tree params, clips with bindings missing on the avatar). In play mode with a live Animator: current parameter values and states.',
    inputSchema: obj({
      assetPath: { type: 'string', description: 'Controller asset path.' },
      path: { type: 'string', description: 'GameObject with an Animator, or the avatar root together with avatarLayer.' },
      avatarLayer: { type: 'string', description: 'Read the controller from the VRCAvatarDescriptor: Base, Additive, Gesture, Action, FX, Sitting, TPose, IKPose.' },
      layer: { type: 'string', description: 'Only detail layers whose name contains this.' },
      detail: { type: 'string', enum: ['summary', 'states', 'full'], description: 'summary = layers only, states (default) = + states, full = + transitions and behaviour fields.' },
      bindingRoot: { type: 'string', description: 'Avatar root to validate clip bindings against (defaults to path).' },
      checkBindings: { type: 'boolean', description: 'Default true when a root is known.' },
    }),
  },
  {
    name: 'inspect_clip',
    description: 'List an AnimationClip\'s curves (path, component.property, values) and, with bindingRoot, flag bindings that do not resolve on the avatar (missing objects, blendshapes, material properties).',
    inputSchema: obj({
      assetPath: { type: 'string' },
      subAsset: { type: 'string', description: 'Clip name inside an FBX.' },
      bindingRoot: { type: 'string', description: 'Avatar root path in the scene.' },
      maxBindings: { type: 'integer', description: 'Default 200.' },
    }, ['assetPath']),
  },
  {
    name: 'capture_view',
    description: 'Render a PNG and return it as an image: frame an object (avatar, or a bone like Head with size) from a yaw/pitch angle, use the current Scene view angle, or a scene Camera.',
    inputSchema: obj({
      frame: { type: 'string', description: 'GameObject path to frame.' },
      yaw: { type: 'number', description: 'Degrees around the object; 0 = front, 180 = back, 90 = its left side.' },
      pitch: { type: 'number', description: 'Degrees; positive looks from above.' },
      padding: { type: 'number', description: 'Framing multiplier (default 1.05; lower = closer).' },
      size: { type: 'number', description: 'Box size (m) when framing an object without renderers, e.g. a bone (default 0.3).' },
      fov: { type: 'number', description: 'Default 30.' },
      camera: { type: 'string', description: 'Render through this scene Camera instead.' },
      width: { type: 'integer', description: 'Default 768.' },
      height: { type: 'integer', description: 'Default 768.' },
    }),
  },
  {
    name: 'play_mode',
    description: 'Enter/exit/pause play mode (Av3Emulator / Gesture Manager testing). Waits for the domain reload to finish.',
    inputSchema: obj({ state: { type: 'string', enum: ['play', 'stop', 'pause', 'resume', 'step'] } }, ['state']),
  },
  {
    name: 'refresh_assets',
    description: 'AssetDatabase.Refresh (import changed files, recompile scripts), wait until Unity is idle again, then return unity_status (incl. compile errors).',
    inputSchema: obj({ forceCompile: { type: 'boolean', description: 'Request a script recompile even if nothing changed.' } }),
  },
  {
    name: 'execute_menu_item',
    description: "Run a Unity menu item by path, e.g. 'Tools/VRCFury/...'. Avoid items that open modal dialogs (they block the editor).",
    inputSchema: obj({ menuPath: { type: 'string' } }, ['menuPath']),
  },
  {
    name: 'invoke_static_method',
    description: 'Call a static C# method by reflection (any loaded assembly; private ok) and return its result plus logs emitted during the call. For custom fixes/queries: write an editor script under Assets/.../Editor, run refresh_assets, then invoke it. Arguments are converted from JSON; UnityEngine.Object parameters accept asset or scene paths.',
    inputSchema: obj({
      type: { type: 'string', description: 'Type full name or simple name.' },
      method: { type: 'string' },
      args: { type: 'array', items: {} },
    }, ['type', 'method']),
  },

  // ---- Av3Emulator (play mode) ----
  {
    name: 'av3_state',
    description: 'PLAY MODE, Av3Emulator: live state of the emulated avatar: built-in inputs (GestureLeft/Right + weights, Viseme, Voice, IsLocal, Grounded, Seated, AFK, VRMode, TrackingType, InStation, ...), every animator/expression parameter value, and for each playable layer (Base/Additive/Gesture/Action/FX/...) its weight plus each animator layer\'s weight, current state (and "-> next" during transitions) and normalized time; tracking control state.',
    inputSchema: obj({
      path: { type: 'string', description: 'Avatar root (default: the local emulated avatar).' },
      params: { type: 'string', description: 'Only parameters whose name contains this.' },
      layer: { type: 'string', description: 'Only playables/animator layers whose name contains this (e.g. FX, or a layer name).' },
      includeParams: { type: 'boolean', description: 'Default true.' },
      includeLayers: { type: 'boolean', description: 'Default true.' },
    }),
  },
  {
    name: 'av3_set',
    description: 'PLAY MODE, Av3Emulator: set avatar inputs like VRChat would. params: built-ins (GestureLeft: "Fist"/0-7, GestureLeftWeight, Viseme, Voice, IsLocal, Grounded, Seated, AFK, VRMode, TrackingType, InStation, VelocityX, ...) and any animator/expression parameter (bool/int/float; floats also update the menu value). fields: other public LyumaAv3Runtime fields (e.g. ResetAvatar: true, EnableHeadScaling). With observeSeconds, records what changes afterwards (parameter drivers, state transitions, layer weights).',
    inputSchema: obj({
      path: { type: 'string' },
      params: { type: 'object', description: 'name -> value' },
      fields: { type: 'object', description: 'LyumaAv3Runtime field -> value' },
      observeSeconds: { type: 'number', description: 'Record changes for this long after setting (default 0 = just set).' },
      only: { type: 'array', items: { type: 'string' }, description: 'With observeSeconds: only track names matching these globs.' },
      ignore: { type: 'array', items: { type: 'string' }, description: 'With observeSeconds: globs to ignore (replaces the defaults).' },
    }),
  },
  {
    name: 'av3_menu',
    description: 'Expression menu tree of the avatar (controls, type, parameter, value, sub-parameters, submenus). In play mode includes each parameter\'s current runtime value and flags parameters missing from the runtime (a common reason a toggle "does nothing").',
    inputSchema: obj({ path: { type: 'string' }, maxDepth: { type: 'integer' } }),
  },
  {
    name: 'av3_record',
    description: 'PLAY MODE, Av3Emulator: record every change of built-ins, parameters, animator states ("Playable/Layer") and layer weights over time (sampled each editor update, so 1-frame blips can be missed). Use seconds to record for a fixed time and get the log, or action start/stop/status for manual control. Optional set applies params at t=0. By default ignores noisy names: Voice, AngularY, Velocity*, Upright, EyeHeight*, Scale*, VRCFury parameter-compressor params/layer (*_SyncData*, *_SyncIndex*, *Parameter Compressor*) and *FrameTime Counter*.',
    inputSchema: obj({
      path: { type: 'string' },
      seconds: { type: 'number', description: 'Record for this long (max 60), then stop and return the log.' },
      action: { type: 'string', enum: ['start', 'stop', 'status'] },
      set: { type: 'object', description: 'Params to apply right after recording starts.' },
      only: { type: 'array', items: { type: 'string' }, description: 'Only track names matching these globs, e.g. ["Horns*", "FX/Horns"].' },
      ignore: { type: 'array', items: { type: 'string' }, description: 'Globs to ignore (parameter, built-in or "FX/LayerName"); replaces the default list ([] = track everything).' },
    }),
  },
];

async function record(args, seconds) {
  const started = await call('av3_record', { ...args, action: 'start' });
  await sleep(Math.min(Math.max(seconds, 0.1), 60) * 1000);
  const log = await call('av3_record', { action: 'stop' });
  return { ...started, ...log };
}

async function runTool(name, args) {
  switch (name) {
    case 'av3_record':
      return args.seconds ? record(args, args.seconds) : call(name, args);
    case 'av3_set': {
      if (!args.observeSeconds) return call(name, args);
      const { observeSeconds, params, ...rest } = args;
      const r = rest.fields ? await call(name, { path: rest.path, fields: rest.fields }) : {};
      return { ...r, ...(await record({ path: rest.path, set: params, only: rest.only, ignore: rest.ignore }, observeSeconds)) };
    }
    case 'refresh_assets': {
      await call(name, args);
      await waitIdle();
      return { status: await call('unity_status', {}) };
    }
    case 'play_mode': {
      const r = await call(name, args);
      if (['play', 'stop'].includes(args.state)) {
        await sleep(1000);
        await waitIdle(120_000, 1000);
        r.isPlayingNow = (await health()).isPlaying;
      }
      return r;
    }
    default:
      return call(name, args);
  }
}

function asText(result) {
  let text = typeof result === 'string' ? result : JSON.stringify(result, null, 1);
  if (text.length > MAX_TEXT)
    text = text.slice(0, MAX_TEXT) + `\n... [truncated ${text.length - MAX_TEXT} chars; narrow the query with path/layer/filter/limit]`;
  return text;
}

async function handleToolCall(params) {
  const name = params?.name;
  const args = params?.arguments ?? {};
  if (!tools.some((t) => t.name === name)) return { isError: true, content: [{ type: 'text', text: `Unknown tool ${name}` }] };
  try {
    const result = await runTool(name, args);
    const content = [{ type: 'text', text: asText(result) }];
    if (name === 'capture_view' && result?.file && existsSync(result.file))
      content.push({ type: 'image', mimeType: 'image/png', data: readFileSync(result.file).toString('base64') });
    return { content };
  } catch (e) {
    return { isError: true, content: [{ type: 'text', text: e.message }] };
  }
}

// ---------------------------------------------------------------- JSON-RPC over stdio

const send = (msg) => process.stdout.write(JSON.stringify(msg) + '\n');

async function handle(msg) {
  const { id, method, params } = msg;
  const reply = (result) => id !== undefined && send({ jsonrpc: '2.0', id, result });
  const fail = (code, message) => id !== undefined && send({ jsonrpc: '2.0', id, error: { code, message } });
  switch (method) {
    case 'initialize':
      return reply({
        protocolVersion: params?.protocolVersion || '2025-06-18',
        capabilities: { tools: { listChanged: false } },
        serverInfo: { name: 'unity-vrc-mcp', version: VERSION },
        instructions: `Inspects and drives the Unity editor of ${projectDir} (VRChat avatar project). Start with unity_status / avatar_report; console_logs for errors.`,
      });
    case 'ping':
      return reply({});
    case 'tools/list':
      return reply({ tools });
    case 'tools/call':
      return reply(await handleToolCall(params));
    default:
      if (method?.startsWith('notifications/')) return;
      return fail(-32601, `Method not found: ${method}`);
  }
}

const rl = readline.createInterface({ input: process.stdin, terminal: false });
rl.on('line', (line) => {
  if (!line.trim()) return;
  let msg;
  try { msg = JSON.parse(line); } catch { return send({ jsonrpc: '2.0', id: null, error: { code: -32700, message: 'Parse error' } }); }
  handle(msg).catch((e) => msg.id !== undefined && send({ jsonrpc: '2.0', id: msg.id, error: { code: -32603, message: e.message } }));
});
rl.on('close', () => process.exit(0));
