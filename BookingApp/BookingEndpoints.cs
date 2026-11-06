using System.Globalization;
using Microsoft.Data.Sqlite;

namespace BookingApp;

public static class BookingEndpoints
{
    private const string UtcTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private const long TicksPerDay = TimeSpan.TicksPerDay;
    private const int SqliteConstraintTrigger = 1811;

    public static void MapBookingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/rooms", GetRoomsAsync);
        app.MapGet("/api/rooms/{roomId:int}/bookings", GetBookingsAsync);
        app.MapPost("/api/bookings", CreateBookingAsync);
        app.MapDelete("/api/bookings/{id:long}", CancelBookingAsync);
    }

    private static async Task<IResult> GetRoomsAsync(
        BookingDatabase database,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM rooms ORDER BY id;";

        var rooms = new List<RoomResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rooms.Add(new RoomResponse(reader.GetInt32(0), reader.GetString(1)));
        }

        return Results.Ok(rooms);
    }

    private static async Task<IResult> GetBookingsAsync(
        int roomId,
        string? date,
        BookingDatabase database,
        CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(
                date,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var day))
        {
            return Results.BadRequest(new ApiError("date must be a UTC date in YYYY-MM-DD format."));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        if (!await RoomExistsAsync(connection, roomId, cancellationToken))
        {
            return Results.NotFound(new ApiError("Room not found."));
        }

        var dayStartTicks = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).Ticks;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, room_id, booker_name, start_utc_ticks, end_utc_ticks
            FROM bookings
            WHERE room_id = $roomId
              AND cancelled_utc_ticks IS NULL
              AND start_utc_ticks < $nextDayStart
              AND end_utc_ticks > $dayStart
            ORDER BY start_utc_ticks, id;
            """;
        command.Parameters.AddWithValue("$roomId", roomId);
        command.Parameters.AddWithValue("$dayStart", dayStartTicks);
        command.Parameters.AddWithValue("$nextDayStart", dayStartTicks + TicksPerDay);

        var bookings = new List<BookingResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            bookings.Add(new BookingResponse(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetString(2),
                FormatUtc(reader.GetInt64(3)),
                FormatUtc(reader.GetInt64(4))));
        }

        return Results.Ok(bookings);
    }

    private static async Task<IResult> CreateBookingAsync(
        CreateBookingRequest request,
        BookingDatabase database,
        CancellationToken cancellationToken)
    {
        if (request.RoomId <= 0)
        {
            return Results.BadRequest(new ApiError("roomId must be a positive room ID."));
        }

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100)
        {
            return Results.BadRequest(new ApiError("name must contain 1 to 100 characters."));
        }

        if (!TryParseUtc(request.StartUtc, out var start))
        {
            return Results.BadRequest(new ApiError("startUtc must use YYYY-MM-DDTHH:mm:ssZ in UTC."));
        }

        if (!TryParseUtc(request.EndUtc, out var end))
        {
            return Results.BadRequest(new ApiError("endUtc must use YYYY-MM-DDTHH:mm:ssZ in UTC."));
        }

        if (!IsQuarterHour(start) || !IsQuarterHour(end))
        {
            return Results.BadRequest(new ApiError("Start and end must be on 15-minute UTC boundaries."));
        }

        if (end <= start)
        {
            return Results.BadRequest(new ApiError("endUtc must be after startUtc."));
        }

        var sameDay = start.Ticks / TicksPerDay == end.Ticks / TicksPerDay;
        var nextMidnight = end.Ticks / TicksPerDay == start.Ticks / TicksPerDay + 1
            && end.Ticks % TicksPerDay == 0;
        if (!sameDay && !nextMidnight)
        {
            return Results.BadRequest(new ApiError(
                "A booking must stay within one UTC calendar day; only next-day 00:00Z is allowed as its exclusive end."));
        }

        if (start < DateTime.UtcNow)
        {
            return Results.BadRequest(new ApiError("startUtc must not be in the past."));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        if (!await RoomExistsAsync(connection, request.RoomId, cancellationToken))
        {
            return Results.NotFound(new ApiError("Room not found."));
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO bookings (room_id, booker_name, start_utc_ticks, end_utc_ticks)
            VALUES ($roomId, $name, $start, $end)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$roomId", request.RoomId);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$start", start.Ticks);
        command.Parameters.AddWithValue("$end", end.Ticks);

        try
        {
            var id = (long)(await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Booking insert returned no ID."));
            return Results.Json(
                new BookingResponse(id, request.RoomId, name, FormatUtc(start.Ticks), FormatUtc(end.Ticks)),
                statusCode: StatusCodes.Status201Created);
        }
        catch (SqliteException exception) when (
            exception.SqliteExtendedErrorCode == SqliteConstraintTrigger
            && exception.Message.Contains("booking_conflict", StringComparison.Ordinal))
        {
            return Results.Conflict(new ApiError("This room is already booked for that time."));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Results.Json(
                new ApiError("The booking database is busy. Please retry."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> CancelBookingAsync(
        long id,
        BookingDatabase database,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE bookings
            SET cancelled_utc_ticks = $cancelledAt
            WHERE id = $id AND cancelled_utc_ticks IS NULL;
            """;
        command.Parameters.AddWithValue("$cancelledAt", DateTime.UtcNow.Ticks);
        command.Parameters.AddWithValue("$id", id);

        var changed = await command.ExecuteNonQueryAsync(cancellationToken);
        return changed == 1
            ? Results.NoContent()
            : Results.NotFound(new ApiError("Active booking not found."));
    }

    private static async Task<bool> RoomExistsAsync(
        SqliteConnection connection,
        int roomId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM rooms WHERE id = $roomId);";
        command.Parameters.AddWithValue("$roomId", roomId);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L) == 1L;
    }

    private static bool TryParseUtc(string? input, out DateTime value)
    {
        value = default;
        if (input is null || !DateTime.TryParseExact(
                input,
                UtcTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        value = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }

    private static bool IsQuarterHour(DateTime time) => time.Minute % 15 == 0 && time.Second == 0;

    private static string FormatUtc(long ticks) =>
        new DateTime(ticks, DateTimeKind.Utc).ToString(UtcTimestampFormat, CultureInfo.InvariantCulture);

    public sealed record CreateBookingRequest(int RoomId, string? Name, string? StartUtc, string? EndUtc);
    private sealed record RoomResponse(int Id, string Name);
    private sealed record BookingResponse(long Id, int RoomId, string Name, string StartUtc, string EndUtc);
    private sealed record ApiError(string Error);
}
