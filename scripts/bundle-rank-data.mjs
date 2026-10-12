// Downloads every difficulty's scenario list and rank thresholds (rank_maxes) from KovaaK's public benchmark API and writes
// src/KovaaksCompanion.Core/Benchmarks/rank-data.json, which is embedded in the app so tiers need no server round trip.
// Scores in the response are ignored; any Steam ID works.
// Usage: node scripts/bundle-rank-data.mjs <steamId>
import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const steam = process.argv[2];
if (!steam) { console.error("usage: node scripts/bundle-rank-data.mjs <steamId>"); process.exit(1); }
const dir = new URL("../src/KovaaksCompanion.Core/Benchmarks/", import.meta.url);
const playlists = JSON.parse(readFileSync(fileURLToPath(new URL("playlists.json", dir)), "utf8")).playlists;
const ids = [...new Set(playlists.flatMap(p => p.levels.map(l => l.id)))];

async function get(id) {
  const url = `https://kovaaks.com/webapp-backend/benchmarks/player-progress-rank-benchmark?benchmarkId=${id}&steamId=${steam}&page=0&max=100`;
  for (let attempt = 0; attempt < 4; attempt++) {
    try {
      const res = await fetch(url);
      if (res.ok) return await res.json();
      if (res.status === 404 || res.status === 400) return null;
    } catch { /* retry */ }
    await new Promise(r => setTimeout(r, 1000 * (attempt + 1)));
  }
  throw new Error(`benchmark ${id}: request failed`);
}

const out = {};
const failed = [];
let next = 0;
await Promise.all(Array.from({ length: 4 }, async () => {
  while (next < ids.length) {
    const id = ids[next++];
    try {
      const j = await get(id);
      if (!j || j.error || !j.categories) { failed.push(id); continue; }
      out[id] = {
        r: (j.ranks ?? []).map(x => x.name.trim()),
        c: Object.entries(j.categories).map(([name, c]) => ({
          n: name.trim(),
          s: Object.entries(c.scenarios).map(([sn, s]) => [sn, Number(s.leaderboard_id ?? 0) || 0, (s.rank_maxes ?? []).map(Number)]),
        })),
      };
    } catch (e) { failed.push(id); console.error(e.message); }
  }
}));

writeFileSync(fileURLToPath(new URL("rank-data.json", dir)), JSON.stringify({ version: 1, difficulties: out }));
console.log(`wrote ${Object.keys(out).length}/${ids.length} difficulties`, failed.length ? `failed: ${failed.join(", ")}` : "");
