using System.Text.Json;
using System.Text.Json.Nodes;
using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Library;
using Microsoft.Data.Sqlite;

namespace KovaaksCompanion.Core;

/// <summary>
/// The archive: what only this PC knows and no server gives back (run statistics, cloud scores older than the few the server still lists, session ids).
/// One SQLite file (WAL, synchronous FULL), so a write is a transaction on the rows it changes: a crash or a lock never leaves a half-written file, and a
/// failed read never turns into an overwrite. Each run row keeps its data as a JSON payload (rows written by earlier versions are signed and masked, see <see cref="SignedFile"/>, and are still read). Failures other than a corrupt file surface as <see cref="IOException"/>, which callers already handle.
/// A corrupt file is set aside as <c>.corrupt-*</c> and replaced by the last <c>.bak</c> (see <see cref="Backup"/>); nothing is ever deleted.
/// </summary>
internal sealed class ArchiveDb(string file)
{
    public const string FileName = "archive.db";
    const int Corrupt = 11, NotADatabase = 26;

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ArchiveDb> Handles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The handle of the archive file: every caller of one file shares it, so no part of the app owns the file alone.</summary>
    public static ArchiveDb At(string file) => Handles.GetOrAdd(file, f => new ArchiveDb(f));

    static readonly object Gate = new();
    static readonly HashSet<string> Ready = new(StringComparer.OrdinalIgnoreCase);
    static bool _initialized;

    readonly string _dir = Path.GetDirectoryName(file)!;

    record RowPayload(string Scenario, DateTime Time, double Score, RunRecord? Record);

    // ---- connection and setup ------------------------------------------------------------------------------------

    SqliteConnection Open()
    {
        EnsureReady();
        return Connect();
    }

    SqliteConnection Connect()
    {
        lock (Gate)
            if (!_initialized) { SQLitePCL.Batteries_V2.Init(); _initialized = true; }
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false, DefaultTimeout = 5 }.ToString());
        try
        {
            c.Open();
            Exec(c, "PRAGMA synchronous=FULL");
            return c;
        }
        catch
        {
            c.Dispose(); // a corrupt file must not stay open, or it cannot be moved aside
            throw;
        }
    }

    static void Exec(SqliteConnection c, string sql, params (string Name, object Value)[] args)
    {
        using var m = c.CreateCommand();
        m.CommandText = sql;
        foreach (var (n, v) in args) m.Parameters.AddWithValue(n, v);
        m.ExecuteNonQuery();
    }

    /// <summary>One statement parsed once and run for many rows.</summary>
    sealed class Stmt : IDisposable
    {
        readonly SqliteCommand _m;
        readonly SqliteParameter[] _p;

        public Stmt(SqliteConnection c, string sql, params string[] names)
        {
            _m = c.CreateCommand();
            _m.CommandText = sql;
            _p = names.Select(n => { var p = _m.CreateParameter(); p.ParameterName = n; _m.Parameters.Add(p); return p; }).ToArray(); // no declared type: each value binds as its own
        }

        public void Run(params object[] values)
        {
            for (var i = 0; i < _p.Length; i++) _p[i].Value = values[i];
            _m.ExecuteNonQuery();
        }

        public void Dispose() => _m.Dispose();
    }

    static T Run<T>(Func<T> body)
    {
        try { return body(); }
        catch (SqliteException e) { throw new IOException("archive database: " + e.Message, e); }
    }

    void EnsureReady()
    {
        lock (Gate)
        {
            if (Ready.Contains(file)) return;
            Directory.CreateDirectory(_dir);
            try { Prepare(); }
            catch (SqliteException e) when (e.SqliteErrorCode is Corrupt or NotADatabase) { SetAside(); Prepare(); }
            catch (InvalidDataException) { SetAside(); Prepare(); }
            Ready.Add(file);
        }
    }

    void Prepare()
    {
        using var c = Connect();
        using (var m = c.CreateCommand())
        {
            m.CommandText = "PRAGMA quick_check";
            if (m.ExecuteScalar()?.ToString() is not "ok") throw new InvalidDataException("archive database failed its check");
        }
        Exec(c, "PRAGMA journal_mode=WAL");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS runs(scenario TEXT NOT NULL COLLATE NOCASE, time INTEGER NOT NULL, score REAL NOT NULL, has_record INTEGER NOT NULL, payload BLOB NOT NULL, PRIMARY KEY(scenario, time, score));
            CREATE TABLE IF NOT EXISTS fetched(scenario TEXT NOT NULL COLLATE NOCASE PRIMARY KEY, at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS sessions(key TEXT NOT NULL PRIMARY KEY, id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS bests(scenario TEXT NOT NULL COLLATE NOCASE PRIMARY KEY, score REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS meta(key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS favorites(kind TEXT NOT NULL, key TEXT NOT NULL, seq INTEGER NOT NULL, PRIMARY KEY(kind, key));
            CREATE TABLE IF NOT EXISTS replays(id TEXT NOT NULL PRIMARY KEY, scenario TEXT NOT NULL COLLATE NOCASE, started INTEGER NOT NULL, ended INTEGER NOT NULL, info TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS replays_run ON replays(scenario, ended);
            CREATE INDEX IF NOT EXISTS replays_ended ON replays(ended);
            """);
        ImportLegacy(c);
        RenameStoredDriftKey(c);
    }

    // ---- run record key rename ---------------------------------------------------------------------------------------------

    const string DriftRenamedKey = "renamed-run-drift";

    /// <summary>Older payloads keep a run's accuracy drift under "Perf"; it is "Drift" now. Renames the key in every run record of the JSON (any depth), leaving the rest as it is.</summary>
    internal static byte[] RenameDriftKey(byte[] json)
    {
        try
        {
            if (JsonNode.Parse(json) is not { } root) return json;
            var changed = false;
            void Walk(JsonNode? node)
            {
                if (node is JsonObject o)
                {
                    if (o.ContainsKey("Scenario") && o.ContainsKey("Score") && o.ContainsKey("End") && o["Perf"] is { } perf && !o.ContainsKey("Drift"))
                    {
                        o.Remove("Perf");
                        o["Drift"] = perf;
                        changed = true;
                    }
                    foreach (var child in o.Select(p => p.Value).ToList()) Walk(child);
                }
                else if (node is JsonArray a)
                    foreach (var child in a) Walk(child);
            }
            Walk(root);
            return changed ? System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()) : json;
        }
        catch (JsonException) { return json; }
    }

    /// <summary>A row's JSON: the payload itself, or its content when an earlier version stored it signed and masked.</summary>
    static byte[] PayloadJson(byte[] stored) => SignedFile.UnpackMasked(stored) ?? stored;

    /// <summary>Rewrites the stored run records once (in one transaction) so their drift is read under the new key.</summary>
    static void RenameStoredDriftKey(SqliteConnection c)
    {
        using (var m = c.CreateCommand())
        {
            m.CommandText = "SELECT 1 FROM meta WHERE key=$k";
            m.Parameters.AddWithValue("$k", DriftRenamedKey);
            if (m.ExecuteScalar() != null) return;
        }
        InTransaction(c, () =>
        {
            var rows = new List<(string Scenario, long Time, double Score, byte[] Payload)>();
            using (var m = c.CreateCommand())
            {
                m.CommandText = "SELECT scenario, time, score, payload FROM runs WHERE has_record=1";
                using var r = m.ExecuteReader();
                while (r.Read()) rows.Add((r.GetString(0), r.GetInt64(1), r.GetDouble(2), (byte[])r[3]));
            }
            using var update = new Stmt(c, "UPDATE runs SET payload=$p WHERE scenario=$s AND time=$t AND score=$sc", "$p", "$s", "$t", "$sc");
            foreach (var (scenario, time, score, payload) in rows)
            {
                var json = PayloadJson(payload);
                var renamed = RenameDriftKey(json);
                if (ReferenceEquals(renamed, json)) continue;
                update.Run(renamed, scenario, time, score);
            }
            Exec(c, "INSERT OR REPLACE INTO meta VALUES($k, '1')", ("$k", DriftRenamedKey));
        });
    }

    /// <summary>Moves a corrupt database (and its WAL files) aside under a new name, then restores the last backup when there is one.</summary>
    void SetAside()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(file + suffix)) File.Move(file + suffix, $"{file}.corrupt-{stamp}{suffix}");
        Diagnostics.AppLog.Write("archive", "database was corrupt; set aside as " + Path.GetFileName(file) + ".corrupt-" + stamp);
        if (File.Exists(file + ".bak")) File.Copy(file + ".bak", file);
    }

    // ---- legacy files (runs.dat, sessions.dat) ------------------------------------------------------------------------

    /// <summary>Imports the earlier signed files once. A file that cannot be read is left alone and tried again next time; after an import it is renamed <c>*.migrated</c>, never deleted.</summary>
    void ImportLegacy(SqliteConnection c)
    {
        ImportOnce(c, "runs", Path.Combine(_dir, StatsStore.RunsFile), path =>
        {
            if (SignedFile.TryReadMasked(path) is not { } b) return false;
            var all = JsonSerializer.Deserialize<Dictionary<string, CachedRuns>>(RenameDriftKey(b)) ?? [];
            WriteBatch(c, all.Keys, (n, old) => new(StatsStore.Merge(old?.Runs ?? [], all[n].Runs), all[n].FetchedAt > (old?.FetchedAt ?? DateTime.MinValue) ? all[n].FetchedAt : old?.FetchedAt ?? DateTime.MinValue));
            return true;
        });
        ImportOnce(c, "sessions", Path.Combine(_dir, "sessions.dat"), path =>
        {
            if (RunSessionIds.ReadLegacy(path) is not { } map) return false;
            UpsertSessions(c, map);
            return true;
        });
    }

    void ImportOnce(SqliteConnection c, string name, string legacy, Func<string, bool> import)
    {
        var key = "imported-" + name;
        using (var m = c.CreateCommand())
        {
            m.CommandText = "SELECT 1 FROM meta WHERE key=$k";
            m.Parameters.AddWithValue("$k", key);
            if (m.ExecuteScalar() != null) return;
        }
        // After a restore from .bak the first import marker is already in the backup; a database made anew re-imports from the renamed file.
        var source = File.Exists(legacy) ? legacy : File.Exists(legacy + ".migrated") ? legacy + ".migrated" : null;
        if (source == null) { Exec(c, "INSERT OR REPLACE INTO meta VALUES($k, '1')", ("$k", key)); return; }
        try
        {
            if (!import(source)) { Diagnostics.AppLog.Write("archive", "legacy " + Path.GetFileName(source) + " is not readable yet; kept for the next start"); return; }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { Diagnostics.AppLog.Write("archive", "legacy import of " + Path.GetFileName(source) + " failed: " + e.Message); return; }
        Exec(c, "INSERT OR REPLACE INTO meta VALUES($k, '1')", ("$k", key));
        if (source == legacy)
        {
            File.Move(legacy, MigratedName(legacy));
        }
    }

    /// <summary>Where an imported file or folder is moved to: its name plus <c>.migrated</c>, with a time stamp when that name is taken.</summary>
    internal static string MigratedName(string path)
    {
        var to = path + ".migrated";
        return File.Exists(to) || Directory.Exists(to) ? $"{path}.{DateTime.Now:yyyyMMdd-HHmmss}.migrated" : to;
    }

    // ---- runs ------------------------------------------------------------------------------------------------------------

    /// <summary>Every scenario's runs and the time its cloud scores were last asked for.</summary>
    public Dictionary<string, CachedRuns> ReadAll() => Run(() =>
    {
        using var c = Open();
        return Load(c, null);
    });

    public CachedRuns? Read(string scenario) => Run(() =>
    {
        using var c = Open();
        return Load(c, scenario).GetValueOrDefault(scenario);
    });

    /// <summary>The scenario and end time of every run the archive keeps statistics of.</summary>
    public HashSet<(string Scenario, DateTime End)> ArchivedKeys() => Run(() =>
    {
        using var c = Open();
        using var m = c.CreateCommand();
        m.CommandText = "SELECT scenario, time FROM runs WHERE has_record=1";
        using var r = m.ExecuteReader();
        var set = new HashSet<(string, DateTime)>();
        while (r.Read()) set.Add((r.GetString(0).ToLowerInvariant(), new DateTime(r.GetInt64(1))));
        return set;
    });

    static Dictionary<string, CachedRuns> Load(SqliteConnection c, string? scenario)
    {
        var rows = new Dictionary<string, List<CachedRun>>(StringComparer.OrdinalIgnoreCase);
        var fetched = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        using (var m = c.CreateCommand())
        {
            m.CommandText = "SELECT scenario, time, score, payload FROM runs" + (scenario == null ? "" : " WHERE scenario=$s");
            if (scenario != null) m.Parameters.AddWithValue("$s", scenario);
            using var r = m.ExecuteReader();
            while (r.Read())
            {
                var (name, time, score) = (r.GetString(0), r.GetInt64(1), r.GetDouble(2));
                if (Decode(PayloadJson((byte[])r[3])) is not { } p) continue; // unreadable row: ignored, kept
                if (!rows.TryGetValue(name, out var list)) rows[name] = list = [];
                list.Add(new CachedRun(p.Time, score, p.Record)); // the payload keeps the time zone kind the column drops
            }
        }
        using (var m = c.CreateCommand())
        {
            m.CommandText = "SELECT scenario, at FROM fetched" + (scenario == null ? "" : " WHERE scenario=$s");
            if (scenario != null) m.Parameters.AddWithValue("$s", scenario);
            using var r = m.ExecuteReader();
            while (r.Read()) fetched[r.GetString(0)] = new DateTime(r.GetInt64(1), DateTimeKind.Local);
        }
        var res = new Dictionary<string, CachedRuns>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in rows.Keys.Concat(fetched.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            res[name] = new(rows.GetValueOrDefault(name)?.OrderBy(x => x.Time).ToList() ?? [], fetched.GetValueOrDefault(name));
        return res;
    }

    static RowPayload? Decode(byte[] json)
    {
        try { return JsonSerializer.Deserialize<RowPayload>(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Changes the given scenarios in one transaction: each one's current rows go through <paramref name="change"/> and the result replaces them. Other writers wait
    /// for the transaction, so nothing is lost between the read and the write.
    /// </summary>
    public void Update(IEnumerable<string> scenarios, Func<string, CachedRuns?, CachedRuns> change) => Run(() =>
    {
        using var c = Open();
        WriteBatch(c, scenarios, change);
        return 0;
    });

    static void WriteBatch(SqliteConnection c, IEnumerable<string> scenarios, Func<string, CachedRuns?, CachedRuns> change)
        => InTransaction(c, () =>
        {
            using var insert = new Stmt(c, "INSERT OR REPLACE INTO runs VALUES($s, $t, $sc, $h, $p)", "$s", "$t", "$sc", "$h", "$p");
            foreach (var n in scenarios)
            {
                var old = Load(c, n).GetValueOrDefault(n);
                var next = change(n, old);
                // Only the rows that differ are written. Unreadable rows are not in <old>, so they are never deleted here.
                var had = (old?.Runs ?? []).ToDictionary(r => (r.Time.Ticks, r.Score), r => r.Record);
                var keep = new HashSet<(long, double)>();
                foreach (var r in next.Runs)
                {
                    var key = (r.Time.Ticks, r.Score);
                    keep.Add(key);
                    if (had.TryGetValue(key, out var hadRecord) && Equals(hadRecord, r.Record)) continue;
                    var payload = JsonSerializer.SerializeToUtf8Bytes(new RowPayload(n, r.Time, r.Score, r.Record));
                    insert.Run(n, r.Time.Ticks, r.Score, r.Record != null ? 1 : 0, payload);
                }
                foreach (var (t, sc) in had.Keys.Where(k => !keep.Contains(k)))
                    Exec(c, "DELETE FROM runs WHERE scenario=$s AND time=$t AND score=$sc", ("$s", n), ("$t", t), ("$sc", sc));
                if (old == null || old.FetchedAt != next.FetchedAt)
                    Exec(c, "INSERT OR REPLACE INTO fetched VALUES($s, $a)", ("$s", n), ("$a", next.FetchedAt.Ticks));
            }
        });

    // ---- sessions --------------------------------------------------------------------------------------------------------

    public Dictionary<string, Guid> LoadSessions() => Run(() =>
    {
        using var c = Open();
        using var m = c.CreateCommand();
        m.CommandText = "SELECT key, id FROM sessions";
        using var r = m.ExecuteReader();
        var map = new Dictionary<string, Guid>();
        while (r.Read()) if (Guid.TryParse(r.GetString(1), out var id)) map[r.GetString(0)] = id;
        return map;
    });

    /// <summary>Stores session ids; <paramref name="replace"/> false keeps the id already stored for a run.</summary>
    public void SaveSessions(IEnumerable<KeyValuePair<string, Guid>> entries, bool replace) => Run(() =>
    {
        using var c = Open();
        UpsertSessions(c, entries, replace);
        return 0;
    });

    static void UpsertSessions(SqliteConnection c, IEnumerable<KeyValuePair<string, Guid>> entries, bool replace = false) =>
        InTransaction(c, () =>
        {
        {
            using var upsert = new Stmt(c, (replace ? "INSERT OR REPLACE" : "INSERT OR IGNORE") + " INTO sessions VALUES($k, $i)", "$k", "$i");
            foreach (var (k, id) in entries) upsert.Run(k, id.ToString());
        }
        });

    // ---- cloud bests -------------------------------------------------------------------------------------------------------

    const string BestsSyncedKey = "cloud-bests-synced";

    /// <summary>The best score the server reported per scenario; the runs table only holds the few plays the server still lists.</summary>
    public Dictionary<string, double> ReadBests() => Run(() =>
    {
        using var c = Open();
        using var m = c.CreateCommand();
        m.CommandText = "SELECT scenario, score FROM bests";
        using var r = m.ExecuteReader();
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        while (r.Read()) map[r.GetString(0)] = r.GetDouble(1);
        return map;
    });

    /// <summary>Keeps the higher score per scenario and marks the first download as done.</summary>
    public void SaveBests(IReadOnlyDictionary<string, double> bests) => Run(() =>
    {
        using var c = Open();
        InTransaction(c, () =>
        {
            using var upsert = new Stmt(c, "INSERT INTO bests VALUES($s, $c) ON CONFLICT(scenario) DO UPDATE SET score=MAX(score, excluded.score)", "$s", "$c");
            foreach (var (n, v) in bests) upsert.Run(n, v);
            Exec(c, "INSERT OR REPLACE INTO meta VALUES($k, '1')", ("$k", BestsSyncedKey));
        });
        return 0;
    });

    /// <summary>True once the server's bests were downloaded.</summary>
    public bool BestsSynced() => Run(() =>
    {
        using var c = Open();
        using var m = c.CreateCommand();
        m.CommandText = "SELECT 1 FROM meta WHERE key=$k";
        m.Parameters.AddWithValue("$k", BestsSyncedKey);
        return m.ExecuteScalar() != null;
    });

    // ---- favorites -------------------------------------------------------------------------------------------------------

    const string FavoritesKey = "favorites-migrated";

    /// <summary>Favorite keys per kind (see <see cref="UiState"/>), oldest first; null until the favorites were first stored here.</summary>
    public Dictionary<string, List<string>>? ReadFavorites() => Run(() =>
    {
        using var c = Open();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT 1 FROM meta WHERE key=$k";
            q.Parameters.AddWithValue("$k", FavoritesKey);
            if (q.ExecuteScalar() == null) return null;
        }
        using var m = c.CreateCommand();
        m.CommandText = "SELECT kind, key FROM favorites ORDER BY seq";
        using var r = m.ExecuteReader();
        var map = new Dictionary<string, List<string>>();
        while (r.Read())
        {
            if (!map.TryGetValue(r.GetString(0), out var list)) map[r.GetString(0)] = list = [];
            list.Add(r.GetString(1));
        }
        return map;
    });

    /// <summary>Replaces all favorites with <paramref name="favorites"/> (kind to keys, oldest first) in one transaction.</summary>
    public void SaveFavorites(IReadOnlyDictionary<string, IReadOnlyList<string>> favorites) => Run(() =>
    {
        using var c = Open();
        InTransaction(c, () =>
        {
            Exec(c, "DELETE FROM favorites");
            using var insert = new Stmt(c, "INSERT OR IGNORE INTO favorites VALUES($kind, $key, $seq)", "$kind", "$key", "$seq");
            var seq = 0;
            foreach (var (kind, keys) in favorites)
                foreach (var key in keys) insert.Run(kind, key, seq++);
            Exec(c, "INSERT OR REPLACE INTO meta VALUES($k, '1')", ("$k", FavoritesKey));
        });
        return 0;
    });

    // ---- replays ---------------------------------------------------------------------------------------------------------

    /// <summary>The info JSON of every replay (see <see cref="Replays.ReplayInfo"/>), newest first.</summary>
    public List<string> ReplayInfos() => Run(() =>
    {
        using var c = Open();
        using var m = c.CreateCommand();
        m.CommandText = "SELECT info FROM replays ORDER BY started DESC";
        using var r = m.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    });

    /// <summary>The info JSON of the replay that ended last at or before <paramref name="time"/>, or null.</summary>
    public string? LastReplayEndedBy(DateTime time) => Run(() =>
    {
        using var c = Open();
        using var m = c.CreateCommand();
        m.CommandText = "SELECT info FROM replays WHERE ended<=$t ORDER BY ended DESC LIMIT 1";
        m.Parameters.AddWithValue("$t", time.Ticks);
        return m.ExecuteScalar() as string;
    });

    /// <summary>The info JSON of the replay of the scenario's run that started at <paramref name="start"/> and ended at <paramref name="end"/>, or null.</summary>
    public string? FindReplay(string scenario, DateTime start, DateTime end) => Run(() =>
    {
        using var c = Open();
        using var m = c.CreateCommand();
        m.CommandText = "SELECT info FROM replays WHERE scenario=$s AND started=$a AND ended=$e";
        m.Parameters.AddWithValue("$s", scenario);
        m.Parameters.AddWithValue("$a", start.Ticks);
        m.Parameters.AddWithValue("$e", end.Ticks);
        return m.ExecuteScalar() as string;
    });

    /// <summary>Adds the replay, or replaces the row of the same id.</summary>
    public void PutReplay(Guid id, string scenario, DateTime start, DateTime end, string infoJson) => Run(() =>
    {
        using var c = Open();
        Exec(c, "INSERT OR REPLACE INTO replays VALUES($i, $s, $a, $e, $j)", ("$i", id.ToString("N")), ("$s", scenario), ("$a", start.Ticks), ("$e", end.Ticks), ("$j", infoJson));
        return 0;
    });

    static void InTransaction(SqliteConnection c, Action body)
    {
        Exec(c, "BEGIN IMMEDIATE");
        try
        {
            body();
            Exec(c, "COMMIT");
        }
        catch
        {
            try { Exec(c, "ROLLBACK"); } catch (SqliteException) { }
            throw;
        }
    }

    // ---- backup ----------------------------------------------------------------------------------------------------------

    /// <summary>A consistent copy next to the database (<c>.bak</c>), at most once per <paramref name="every"/>; the copy replaces the previous one only when it is complete.</summary>
    public void Backup(TimeSpan every) => Run(() =>
    {
        var bak = file + ".bak";
        if (File.Exists(bak) && DateTime.UtcNow - File.GetLastWriteTimeUtc(bak) < every) return 0;
        using var c = Open();
        var tmp = bak + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        Exec(c, "VACUUM INTO $f", ("$f", tmp));
        File.Move(tmp, bak, overwrite: true);
        return 0;
    });
}
