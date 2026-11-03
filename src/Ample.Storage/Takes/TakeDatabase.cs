using Microsoft.Data.Sqlite;

namespace Ample.Storage.Takes;

/// <summary>The SQLite schema of the takebank (plans, attempts, data, takes). The Always rule lives here as well as in the code: a trigger refuses a take without a stored plan even when foreign keys are switched off, and plans and takes can neither be changed nor deleted.</summary>
internal static class TakeDatabase
{
    internal const int SchemaVersion = 1;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS schema_parts (part TEXT PRIMARY KEY, version INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS plans (
          plan_id TEXT PRIMARY KEY,
          block_uuid TEXT NOT NULL,
          parent_plan_id TEXT NULL REFERENCES plans(plan_id),
          json TEXT NOT NULL,
          created_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS plans_block ON plans(block_uuid);
        CREATE TABLE IF NOT EXISTS attempts (
          attempt_id TEXT PRIMARY KEY,
          plan_id TEXT NOT NULL REFERENCES plans(plan_id),
          started_at TEXT NOT NULL,
          outcome TEXT NOT NULL CHECK (outcome IN ('started','completed','failed','cancelled')),
          reason TEXT NULL,
          take_id TEXT NULL);
        CREATE TABLE IF NOT EXISTS data (
          data_id TEXT PRIMARY KEY,
          size INTEGER NOT NULL,
          created_at TEXT NOT NULL,
          quarantined INTEGER NOT NULL DEFAULT 0,
          removed INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS takes (
          take_id TEXT PRIMARY KEY,
          plan_id TEXT NOT NULL REFERENCES plans(plan_id),
          created_at TEXT NOT NULL,
          meta_json TEXT NOT NULL,
          tombstoned INTEGER NOT NULL DEFAULT 0,
          tombstone_reason TEXT NULL,
          tombstone_at TEXT NULL);
        CREATE TABLE IF NOT EXISTS take_data (
          take_id TEXT NOT NULL REFERENCES takes(take_id),
          role TEXT NOT NULL,
          data_id TEXT NOT NULL REFERENCES data(data_id),
          PRIMARY KEY (take_id, role, data_id));
        CREATE TRIGGER IF NOT EXISTS takes_need_plan BEFORE INSERT ON takes
          WHEN NOT EXISTS (SELECT 1 FROM plans WHERE plan_id = NEW.plan_id)
          BEGIN SELECT RAISE(ABORT, 'AMP5220: a take needs a stored plan (the Always rule)'); END;
        CREATE TRIGGER IF NOT EXISTS takes_immutable BEFORE UPDATE OF take_id, plan_id, created_at, meta_json ON takes
          BEGIN SELECT RAISE(ABORT, 'AMP5224: takes are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS takes_no_delete BEFORE DELETE ON takes
          BEGIN SELECT RAISE(ABORT, 'AMP5224: takes are never deleted; use a tombstone'); END;
        CREATE TRIGGER IF NOT EXISTS plans_immutable BEFORE UPDATE ON plans
          BEGIN SELECT RAISE(ABORT, 'AMP5224: plans are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS plans_no_delete BEFORE DELETE ON plans
          BEGIN SELECT RAISE(ABORT, 'AMP5224: plans are never deleted'); END;
        CREATE TRIGGER IF NOT EXISTS take_data_immutable BEFORE UPDATE ON take_data
          BEGIN SELECT RAISE(ABORT, 'AMP5224: take data references are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS take_data_no_delete BEFORE DELETE ON take_data
          BEGIN SELECT RAISE(ABORT, 'AMP5224: take data references are never deleted'); END;
        INSERT OR IGNORE INTO schema_parts (part, version) VALUES ('takebank', 1);
        """;

    internal static SqliteConnection Open(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    internal static SqliteConnection OpenReadOnly(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA query_only = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    internal static void Initialize(SqliteConnection connection)
    {
        using SqliteCommand wal = connection.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode = WAL;";
        wal.ExecuteNonQuery();
        using SqliteCommand create = connection.CreateCommand();
        create.CommandText = Schema;
        create.ExecuteNonQuery();
        using SqliteCommand info = connection.CreateCommand();
        info.CommandText = "SELECT COUNT(*) FROM pragma_table_info('data') WHERE name = 'removed'";
        if ((long)info.ExecuteScalar()! == 0)
        {
            using SqliteCommand alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE data ADD COLUMN removed INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
        }
    }
}
