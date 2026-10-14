using System.Text.Json;
using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Library;
using Microsoft.Data.Sqlite;

namespace KovaaksCompanion.Core;

/// <summary>
/// The archive: what only this PC knows and no server gives back (run statistics, server scores older than the few the server still lists, session ids).
/// One SQLite file (WAL, synchronous FULL), so a write is a transaction on the rows it changes: a crash or a lock never leaves a half-written file, and a
/// failed read never turns into an overwrite. Each run row keeps its data in a signed, masked payload (<see cref="SignedFile"/>); a row whose payload no longer
/// matches its key is ignored, never deleted. Failures other than a corrupt file surface as <see cref="IOException"/>, which callers already handle.
/// A corrupt file is set aside as <c>.corrupt-*</c> and replaced by the last <c>.bak</c> (see <see cref="Backup"/>); nothing is ever deleted.
/// </summary>
internal sealed class ArchiveDb(string file)
{
    public const string FileName = "archive.db";
    const int Corrupt = 11, NotADatabase = 26;

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
            CREATE TABLE IF NOT EXISTS meta(key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);
            """);
        ImportLegacy(c);
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
            var all = JsonSerializer.Deserialize<Dictionary<string, CachedRuns>>(b) ?? [];
            WriteBatch(c, all.Keys, (n, old) => new(StatsStore.Merge(old?.Runs ?? [], all[n].Runs), all[n].FetchedAt > (old?.FetchedAt ?? DateTime.MinValue) ? all[n].FetchedAt : old?.FetchedAt ?? DateTime.MinValue));
            return true;
        });
        ImportOnce(c, "sessions", Path.Combine(_dir, "sessions.dat"), path =>
        {
            if (LiveRunLog.ReadLegacy(path) is not { } map) return false;
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
            var to = legacy + ".migrated";
            if (File.Exists(to)) to = $"{legacy}.{DateTime.Now:yyyyMMdd-HHmmss}.migrated";
            File.Move(legacy, to);
        }
    }

    // ---- runs ------------------------------------------------------------------------------------------------------------

    /// <summary>Every scenario's runs and the time its server scores were last asked for.</summary>
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
                if (SignedFile.UnpackMasked((byte[])r[3]) is not { } b || Decode(b) is not { } p
                    || !p.Scenario.Equals(name, StringComparison.OrdinalIgnoreCase) || p.Time.Ticks != time || p.Score != score) continue; // edited row: ignored, kept
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
                // Only the rows that differ are written. Rows that failed their check are not in <old>, so they are never deleted here.
                var had = (old?.Runs ?? []).ToDictionary(r => (r.Time.Ticks, r.Score), r => r.Record != null);
                var keep = new HashSet<(long, double)>();
                foreach (var r in next.Runs)
                {
                    var key = (r.Time.Ticks, r.Score);
                    keep.Add(key);
                    if (had.TryGetValue(key, out var hadRecord) && hadRecord == (r.Record != null)) continue;
                    var payload = SignedFile.PackMasked(JsonSerializer.SerializeToUtf8Bytes(new RowPayload(n, r.Time, r.Score, r.Record)));
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
