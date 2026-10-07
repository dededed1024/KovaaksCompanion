// Converts a benchmark list in the source layout (array of benchmarks with difficulties/categories/subcategories)
// into this app's own compact layout and writes src/KovaaksCompanion.Core/Benchmarks/playlists.json.
// Usage: node scripts/convert-benchmarks.mjs <source.json>
import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const src = process.argv[2];
if (!src) { console.error("usage: node scripts/convert-benchmarks.mjs <source.json>"); process.exit(1); }
const out = fileURLToPath(new URL("../src/KovaaksCompanion.Core/Benchmarks/playlists.json", import.meta.url));

const playlists = JSON.parse(readFileSync(src, "utf8")).map(b => ({
  name: b.benchmarkName,
  abbr: b.abbreviation,
  color: b.color,
  levels: b.difficulties.map(d => ({
    level: d.difficultyName,
    id: d.kovaaksBenchmarkId,
    code: d.sharecode,
    tierColors: d.rankColors,
    groups: d.categories.map(c => ({
      name: c.categoryName,
      color: c.color,
      parts: c.subcategories.map(s => ({ name: s.subcategoryName, count: s.scenarioCount, color: s.color })),
    })),
  })),
}));

writeFileSync(out, JSON.stringify({ version: 1, playlists }));
console.log(`${playlists.length} playlists -> ${out}`);
