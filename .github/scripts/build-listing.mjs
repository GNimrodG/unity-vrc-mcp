// Builds a VPM repository listing (index.json) from this repo's GitHub releases.
// Each release must carry the package zip and its package.json as assets (see release.yml).
// The listing is served from the "vpm" branch through raw.githubusercontent.com.
// For a private repo, zip URLs point at the release-asset API and each version asks for
// "Accept: application/octet-stream"; ALCOM/VCC then only need an Authorization header on the repository.
// Usage: GITHUB_REPOSITORY=owner/repo [GITHUB_TOKEN=...] node build-listing.mjs <outDir>

import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';

const outDir = process.argv[2] || '_site';
const repo = process.env.GITHUB_REPOSITORY;
if (!repo) throw new Error('GITHUB_REPOSITORY is not set');
const owner = repo.split('/')[0];
const listingUrl = `https://raw.githubusercontent.com/${repo}/vpm/index.json`;
const pkg = JSON.parse(readFileSync('package.json', 'utf8'));

const headers = { Accept: 'application/vnd.github+json', 'User-Agent': 'vpm-listing-builder' };
if (process.env.GITHUB_TOKEN) headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`;

async function get(url, accept) {
  const res = await fetch(url, { headers: accept ? { ...headers, Accept: accept } : headers });
  if (!res.ok) throw new Error(`GET ${url}: HTTP ${res.status}`);
  return res;
}

const isPrivate = (await (await get(`https://api.github.com/repos/${repo}`)).json()).private === true;

const releases = [];
for (let page = 1; ; page++) {
  const batch = await (await get(`https://api.github.com/repos/${repo}/releases?per_page=100&page=${page}`)).json();
  releases.push(...batch);
  if (batch.length < 100) break;
}

async function describe(rel) {
  const manifest = rel.assets.find((a) => a.name === 'package.json');
  const zip = rel.assets.find((a) => a.name.endsWith('.zip'));
  if (!manifest || !zip) {
    console.warn(`skipping ${rel.tag_name}: needs package.json and a .zip asset`);
    return null;
  }
  const [meta, bytes] = await Promise.all([
    get(manifest.url, 'application/octet-stream').then((r) => r.json()),
    get(zip.url, 'application/octet-stream').then((r) => r.arrayBuffer()),
  ]);
  console.log(`${meta.name}@${meta.version} <- ${rel.tag_name}`);
  const entry = { ...meta, zipSHA256: createHash('sha256').update(Buffer.from(bytes)).digest('hex') };
  if (isPrivate) {
    entry.url = zip.url; // https://api.github.com/repos/<repo>/releases/assets/<id>
    entry.headers = { Accept: 'application/octet-stream' };
  } else {
    entry.url = zip.browser_download_url;
  }
  return entry;
}

const versions = {};
const described = await Promise.all(releases.filter((r) => !r.draft && !r.prerelease).map(describe));
for (const v of described) if (v) versions[v.version] = v;

const listing = {
  name: 'data-unknown VPM',
  id: 'com.data-unknown.vpm',
  url: listingUrl,
  author: pkg.author?.name ?? owner,
  packages: { [pkg.name]: { versions } },
};

mkdirSync(outDir, { recursive: true });
writeFileSync(path.join(outDir, 'index.json'), JSON.stringify(listing, null, 2) + '\n');

// Landing page with a one-click "Add to VCC / ALCOM" link (GitHub strips vcc: links from READMEs).
// Served by GitHub Pages from the vpm branch once the repo is public; .nojekyll serves files as they are.
const latest = Object.keys(versions).sort((a, b) => b.localeCompare(a, undefined, { numeric: true }))[0] ?? 'none';
const addUrl = `vcc://vpm/addRepo?url=${encodeURIComponent(listingUrl)}`;
const esc = (s) => String(s ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
writeFileSync(path.join(outDir, '.nojekyll'), '');
writeFileSync(path.join(outDir, 'index.html'), `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${esc(pkg.displayName)}</title>
<style>
  :root { color-scheme: light dark; --fg: #1d1d1f; --bg: #fafafa; --muted: #6b6b70; --accent: #3f6ad8; --card: #fff; --line: #0001; }
  @media (prefers-color-scheme: dark) { :root { --fg: #ececf1; --bg: #16171a; --muted: #a0a0aa; --accent: #7d9cf5; --card: #1f2024; --line: #fff2; } }
  body { margin: 0; font: 16px/1.55 system-ui, sans-serif; color: var(--fg); background: var(--bg); }
  main { max-width: 44rem; margin: 3rem auto; padding: 0 16px; }
  h1 { margin: 0 0 .25rem; font-size: 2rem; }
  .version { color: var(--muted); margin: 0 0 1.5rem; }
  .button { display: inline-block; padding: .7rem 1.2rem; border-radius: 8px; background: var(--accent); color: #fff; text-decoration: none; font-weight: 600; }
  .card { background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 1rem 1.2rem; margin: 1.5rem 0; }
  code { font: 14px ui-monospace, monospace; word-break: break-all; }
  a { color: var(--accent); }
</style>
</head>
<body>
<main>
  <h1>${esc(pkg.displayName)}</h1>
  <p class="version">Version ${esc(latest)} &middot; VPM package <code>${esc(pkg.name)}</code></p>
  <p>${esc(pkg.description)}</p>
  <p><a class="button" href="${esc(addUrl)}">Add to VCC / ALCOM</a></p>
  <div class="card">
    <p>The button opens VRChat Creator Companion or ALCOM. If nothing happens, add this repository by hand
    (Settings &rarr; Packages &rarr; Add Repository):</p>
    <p><code>${esc(listingUrl)}</code></p>
  </div>
  <p><a href="https://github.com/${esc(repo)}#readme">Documentation and source</a></p>
</main>
</body>
</html>
`);
console.log(`wrote ${Object.keys(versions).length} version(s) and the landing page to ${outDir} (${isPrivate ? 'private' : 'public'} repo)`);
