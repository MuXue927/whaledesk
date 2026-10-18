// P0: freeze the render contract — fetch the pet spec and the atlas from the DSH loopback API.
import fs from 'node:fs';
import path from 'node:path';

const ROOT = 'C:/Users/whxnt/OneDrive/文档/deepseek-harness/default-workspace/whaledesk';
const ASSETS = path.join(ROOT, 'assets');
fs.mkdirSync(ASSETS, { recursive: true });
fs.mkdirSync(path.join(ROOT, 'build'), { recursive: true });
const BASE = 'http://127.0.0.1:19387';
const PET = 'whale-girl-refined';

const pets = await (await fetch(`${BASE}/api/pet/pets`)).json();
const spec = pets.find((p) => p.id === PET);
if (!spec) throw new Error(`pet ${PET} not found in registry`);
fs.writeFileSync(path.join(ASSETS, 'pet-spec.json'), JSON.stringify(spec, null, 2));

const atlasRes = await fetch(BASE + spec.atlasUrl);
if (!atlasRes.ok) throw new Error(`atlas HTTP ${atlasRes.status}`);
const atlas = Buffer.from(await atlasRes.arrayBuffer());
fs.writeFileSync(path.join(ASSETS, 'atlas.webp'), atlas);

const manifestRes = await fetch(BASE + spec.manifestUrl);
fs.writeFileSync(path.join(ASSETS, 'pet.json'), Buffer.from(await manifestRes.arrayBuffer()));

// --- assertions: the whole renderer depends on these invariants ---
const problems = [];
if (spec.renderer !== 'sprite2d') problems.push(`renderer is ${spec.renderer}, expected sprite2d`);
const trackNames = Object.keys(spec.tracks);
if (trackNames.length !== spec.atlasRows) problems.push(`tracks(${trackNames.length}) != atlasRows(${spec.atlasRows})`);
if (spec.rows.length !== spec.atlasRows) problems.push(`rows length(${spec.rows.length}) != atlasRows(${spec.atlasRows})`);
trackNames.forEach((name, i) => {
  const t = spec.tracks[name];
  const declared = spec.rows[i];
  if (t.frames.length !== declared) problems.push(`row ${i} (${name}): frames ${t.frames.length} != rows[i] ${declared}`);
  if (t.frames.length !== t.durations.length) problems.push(`row ${i} (${name}): frames ${t.frames.length} != durations ${t.durations.length}`);
  const sorted = [...t.frames].sort((a, b) => a - b);
  if (sorted.some((v, k) => v !== k)) problems.push(`row ${i} (${name}): frame indices not 0..n-1 -> ${t.frames}`);
  if (t.frames.some((f) => f >= spec.columns)) problems.push(`row ${i} (${name}): frame index >= columns`);
});
if (spec.rows.some((n) => n > spec.columns)) problems.push('some row has more frames than columns');

const report = {
  petId: spec.id,
  displayName: spec.displayName,
  renderer: spec.renderer,
  cell: spec.cell,
  columns: spec.columns,
  atlasRows: spec.atlasRows,
  rows: spec.rows,
  tracks: trackNames.map((n, i) => ({
    row: i, name: n, frames: spec.tracks[n].frames.length,
    durations: spec.tracks[n].durations, loop: spec.tracks[n].loop,
    fallback: spec.tracks[n].fallback ?? null,
    totalMs: spec.tracks[n].durations.reduce((a, b) => a + b, 0),
  })),
  atlasBytes: atlas.length,
  expectedAtlasSize: [spec.cell.width * spec.columns, spec.cell.height * spec.atlasRows],
  problems,
};
fs.writeFileSync(path.join(ROOT, 'build', 'p0-contract-report.json'), JSON.stringify(report, null, 2), { flag: 'w' });
// ensure build dir exists before the write above on first run
console.log('expected atlas size:', report.expectedAtlasSize.join('x'));
console.log('atlas bytes:', atlas.length);
console.log('tracks:');
for (const t of report.tracks) console.log(`  row ${t.row}  ${t.name.padEnd(14)} frames=${t.frames}  total=${t.totalMs}ms  loop=${t.loop}  fallback=${t.fallback}`);
console.log('PROBLEMS:', problems.length === 0 ? 'NONE (all invariants hold)' : problems.join(' | '));
