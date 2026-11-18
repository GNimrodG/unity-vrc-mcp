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
console.log(`wrote ${Object.keys(versions).length} version(s) to ${outDir} (${isPrivate ? 'private' : 'public'} repo)`);
