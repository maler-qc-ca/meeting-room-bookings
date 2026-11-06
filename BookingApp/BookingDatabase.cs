using Microsoft.Data.Sqlite;

namespace BookingApp;

public sealed class BookingDatabase
{
    private readonly string _connectionString;

    public BookingDatabase(IConfiguration configuration)
    {
        var configuredPath = configuration["Bookings:DatabasePath"];
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException("Bookings:DatabasePath must be configured.");
        }

        var databasePath = Path.GetFullPath(configuredPath);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        // WAL allows readers to continue while a booking writer holds the SQLite write lock.
        await using (var journalCommand = connection.CreateCommand())
        {
            journalCommand.CommandText = "PRAGMA journal_mode = WAL;";
            await journalCommand.ExecuteScalarAsync(cancellationToken);
        }

        using var transaction = connection.BeginTransaction();

        await ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS rooms (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL UNIQUE
            );
            """, cancellationToken);

        await ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS bookings (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                room_id INTEGER NOT NULL REFERENCES rooms(id) ON DELETE RESTRICT,
                booker_name TEXT NOT NULL CHECK (length(trim(booker_name)) BETWEEN 1 AND 100),
                start_utc_ticks INTEGER NOT NULL,
                end_utc_ticks INTEGER NOT NULL,
                cancelled_utc_ticks INTEGER,
                CHECK (end_utc_ticks > start_utc_ticks)
            );
            """, cancellationToken);

        await ExecuteAsync(connection, transaction, """
            CREATE INDEX IF NOT EXISTS bookings_active_room_start
            ON bookings (room_id, start_utc_ticks, end_utc_ticks)
            WHERE cancelled_utc_ticks IS NULL;
            """, cancellationToken);

        await ExecuteAsync(connection, transaction, """
            CREATE TRIGGER IF NOT EXISTS bookings_prevent_overlap
            BEFORE INSERT ON bookings
            WHEN NEW.cancelled_utc_ticks IS NULL AND EXISTS (
                SELECT 1
                FROM bookings AS existing
                WHERE existing.room_id = NEW.room_id
                  AND existing.cancelled_utc_ticks IS NULL
                  AND existing.start_utc_ticks < NEW.end_utc_ticks
                  AND NEW.start_utc_ticks < existing.end_utc_ticks
            )
            BEGIN
                SELECT RAISE(ABORT, 'booking_conflict');
            END;
            """, cancellationToken);

        await ExecuteAsync(connection, transaction, """
            INSERT OR IGNORE INTO rooms (id, name) VALUES
                (1, 'Room A'),
                (2, 'Room B'),
                (3, 'Room C');
            """, cancellationToken);

        transaction.Commit();
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
