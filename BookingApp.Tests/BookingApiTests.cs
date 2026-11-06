using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BookingApp.Tests;

public sealed class BookingApiTests
{
    private static readonly DateTime DayStart = DateTime.UtcNow.Date.AddDays(2);
    private static readonly string Day = DayStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static readonly string NextDay = DayStart.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static readonly string DayAfterNext = DayStart.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Rooms_are_seeded_and_bookings_are_filtered_by_room_and_utc_day()
    {
        using var factory = new BookingFactory();
        using var client = factory.CreateClient();

        var rooms = await client.GetFromJsonAsync<RoomPayload[]>("/api/rooms");
        Assert.NotNull(rooms);
        Assert.Equal(new[] { 1, 2, 3 }, rooms.Select(room => room.Id).ToArray());
        Assert.Equal(new[] { "Room A", "Room B", "Room C" }, rooms.Select(room => room.Name).ToArray());

        var first = await CreateAsync(client, NewBooking(1, "Moe", "10:00", "11:00"));
        var otherRoom = await CreateAsync(client, NewBooking(2, "Ava", "10:00", "11:00"));
        var otherDay = await CreateAsync(client, new BookingInput(
            1, "Sam", $"{NextDay}T10:00:00Z", $"{NextDay}T11:00:00Z"));

        Assert.Equal(first.Id, Assert.Single(await GetBookingsAsync(client, 1, Day)).Id);
        Assert.Equal(otherRoom.Id, Assert.Single(await GetBookingsAsync(client, 2, Day)).Id);
        Assert.Equal(otherDay.Id, Assert.Single(await GetBookingsAsync(client, 1, NextDay)).Id);
        Assert.Empty(await GetBookingsAsync(client, 3, Day));

        using var missingRoom = await client.GetAsync($"/api/rooms/99/bookings?date={Day}");
        Assert.Equal(HttpStatusCode.NotFound, missingRoom.StatusCode);
        using var invalidDate = await client.GetAsync("/api/rooms/1/bookings?date=11-05-2026");
        Assert.Equal(HttpStatusCode.BadRequest, invalidDate.StatusCode);

        Assert.Equal(3, (await ReadRowsAsync(factory.DatabasePath)).Count);
    }

    [Fact]
    public async Task Overlap_is_rejected_but_adjacent_and_other_room_bookings_are_saved()
    {
        using var factory = new BookingFactory();
        using var client = factory.CreateClient();

        await CreateAsync(client, NewBooking(1, "First", "10:00", "11:00"));
        using var overlap = await client.PostAsJsonAsync(
            "/api/bookings", NewBooking(1, "Overlap", "10:30", "11:30"));
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
        await CreateAsync(client, NewBooking(1, "Adjacent", "11:00", "12:00"));
        await CreateAsync(client, NewBooking(2, "Other room", "10:00", "11:00"));

        var rows = await ReadRowsAsync(factory.DatabasePath);
        Assert.Equal(3, rows.Count);
        var roomOne = rows.Where(row => row.RoomId == 1).OrderBy(row => row.StartTicks).ToArray();
        Assert.Equal(2, roomOne.Length);
        Assert.Equal(roomOne[0].EndTicks, roomOne[1].StartTicks);
        var roomTwo = Assert.Single(rows, row => row.RoomId == 2);
        Assert.Equal(roomOne[0].StartTicks, roomTwo.StartTicks);
        Assert.Equal(roomOne[0].EndTicks, roomTwo.EndTicks);
        Assert.All(rows, row => Assert.Null(row.CancelledTicks));
    }

    [Fact]
    public async Task Invalid_booking_requests_do_not_write_rows()
    {
        using var factory = new BookingFactory();
        using var client = factory.CreateClient();

        var valid = NewBooking(1, "Moe", "10:00", "11:00");
        var invalidRequests = new[]
        {
            valid with { RoomId = 0 },
            valid with { Name = "  " },
            valid with { StartUtc = $"{Day}T10:04:00Z" },
            valid with { EndUtc = $"{Day}T11:10:00Z" },
            valid with { EndUtc = $"{Day}T10:00:00Z" },
            valid with { EndUtc = $"{Day}T09:45:00Z" },
            valid with { StartUtc = $"{Day}T10:00:00+00:00" },
            valid with { EndUtc = $"{NextDay}T00:15:00Z" },
            valid with { EndUtc = $"{DayAfterNext}T00:00:00Z" }
        };

        foreach (var request in invalidRequests)
        {
            using var response = await client.PostAsJsonAsync("/api/bookings", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var unknownRoom = await client.PostAsJsonAsync(
            "/api/bookings", valid with { RoomId = 99 });
        Assert.Equal(HttpStatusCode.NotFound, unknownRoom.StatusCode);
        Assert.Empty(await ReadRowsAsync(factory.DatabasePath));
    }

    [Fact]
    public async Task Past_utc_start_times_are_rejected_without_writing_rows()
    {
        using var factory = new BookingFactory();
        using var client = factory.CreateClient();

        var now = DateTime.UtcNow;
        var previousDayStart = now.Date.AddDays(-1).AddHours(10);
        var previousQuarter = (int)(now.TimeOfDay.TotalMinutes / 15) * 15 - 15;
        var earlierTodayStart = now.Date.AddMinutes(Math.Max(0, previousQuarter));

        foreach (var start in new[] { previousDayStart, earlierTodayStart })
        {
            using var response = await client.PostAsJsonAsync("/api/bookings", new BookingInput(
                1,
                "Past booking",
                FormatUtc(start),
                FormatUtc(start.AddMinutes(15))));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<ErrorPayload>();
            Assert.NotNull(error);
            Assert.Contains("past", error.Error, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(await ReadRowsAsync(factory.DatabasePath));
    }

    [Fact]
    public async Task Midnight_end_is_allowed_and_belongs_only_to_the_start_day()
    {
        using var factory = new BookingFactory();
        using var client = factory.CreateClient();

        var booking = await CreateAsync(client, new BookingInput(
            1, "Last slot", $"{Day}T23:45:00Z", $"{NextDay}T00:00:00Z"));

        Assert.Equal(booking.Id, Assert.Single(await GetBookingsAsync(client, 1, Day)).Id);
        Assert.Empty(await GetBookingsAsync(client, 1, NextDay));
        var row = Assert.Single(await ReadRowsAsync(factory.DatabasePath));
        Assert.Equal(DayStart.AddHours(23).AddMinutes(45).Ticks, row.StartTicks);
        Assert.Equal(DayStart.AddDays(1).Ticks, row.EndTicks);
    }

    [Fact]
    public async Task Overnight_booking_is_rejected_even_when_shorter_than_24_hours()
    {
        using var factory = new BookingFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/bookings", new BookingInput(
            1, "Overnight", $"{Day}T23:00:00Z", $"{NextDay}T02:00:00Z"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorPayload>();
        Assert.NotNull(error);
        Assert.Contains("one UTC calendar day", error.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await GetBookingsAsync(client, 1, Day));
        Assert.Empty(await GetBookingsAsync(client, 1, NextDay));
        Assert.Empty(await ReadRowsAsync(factory.DatabasePath));
    }

    [Fact]
    public async Task Cancelling_retains_history_and_frees_the_slot_for_rebooking()
    {
        using var factory = new BookingFactory();
        using var client = factory.CreateClient();

        var first = await CreateAsync(client, NewBooking(1, "First", "10:00", "11:00"));
        using var cancelled = await client.DeleteAsync($"/api/bookings/{first.Id}");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        using var alreadyCancelled = await client.DeleteAsync($"/api/bookings/{first.Id}");
        Assert.Equal(HttpStatusCode.NotFound, alreadyCancelled.StatusCode);
        Assert.Empty(await GetBookingsAsync(client, 1, Day));

        var replacement = await CreateAsync(client, NewBooking(1, "Replacement", "10:00", "11:00"));
        Assert.Equal(replacement.Id, Assert.Single(await GetBookingsAsync(client, 1, Day)).Id);

        var rows = await ReadRowsAsync(factory.DatabasePath);
        Assert.Equal(2, rows.Count);
        Assert.NotNull(Assert.Single(rows, row => row.Id == first.Id).CancelledTicks);
        Assert.Null(Assert.Single(rows, row => row.Id == replacement.Id).CancelledTicks);
        Assert.Equal(rows[0].StartTicks, rows[1].StartTicks);
        Assert.Equal(rows[0].EndTicks, rows[1].EndTicks);
    }

    [Fact]
    public async Task Concurrent_overlapping_requests_allow_exactly_one_saved_booking()
    {
        using var factory = new BookingFactory();
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        using var barrier = new Barrier(2);

        Task<HttpResponseMessage> SendAsync(HttpClient client, string name) => Task.Run(async () =>
        {
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Competing requests did not start together.");
            }

            return await client.PostAsJsonAsync(
                "/api/bookings", NewBooking(1, name, "10:00", "11:00"));
        });

        var firstTask = SendAsync(firstClient, "First");
        var secondTask = SendAsync(secondClient, "Second");
        using var first = await firstTask;
        using var second = await secondTask;

        Assert.Equal(
            new[] { HttpStatusCode.Created, HttpStatusCode.Conflict },
            new[] { first.StatusCode, second.StatusCode }.OrderBy(status => (int)status).ToArray());
        Assert.Single(await GetBookingsAsync(firstClient, 1, Day));
        Assert.Single(await ReadRowsAsync(factory.DatabasePath));
    }

    private static BookingInput NewBooking(int roomId, string name, string start, string end) =>
        new(roomId, name, $"{Day}T{start}:00Z", $"{Day}T{end}:00Z");

    private static string FormatUtc(DateTime value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static async Task<BookingPayload> CreateAsync(HttpClient client, BookingInput request)
    {
        using var response = await client.PostAsJsonAsync("/api/bookings", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<BookingPayload>()
            ?? throw new InvalidOperationException("A successful booking had no response body.");
    }

    private static async Task<BookingPayload[]> GetBookingsAsync(HttpClient client, int roomId, string day) =>
        await client.GetFromJsonAsync<BookingPayload[]>($"/api/rooms/{roomId}/bookings?date={day}")
            ?? throw new InvalidOperationException("The booking list had no response body.");

    private static async Task<List<StoredBooking>> ReadRowsAsync(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, room_id, start_utc_ticks, end_utc_ticks, cancelled_utc_ticks
            FROM bookings ORDER BY id;
            """;
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<StoredBooking>();
        while (await reader.ReadAsync())
        {
            rows.Add(new StoredBooking(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        }

        return rows;
    }

    private sealed class BookingFactory : WebApplicationFactory<Program>
    {
        public string DatabasePath { get; } = Path.Combine(
            Path.GetTempPath(), $"riva-booking-test-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var appRoot = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "BookingApp"));
            builder.UseContentRoot(appRoot);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Bookings:DatabasePath"] = DatabasePath
                }));
        }
    }

    private sealed record BookingInput(int RoomId, string? Name, string? StartUtc, string? EndUtc);
    private sealed record RoomPayload(int Id, string Name);
    private sealed record BookingPayload(long Id, int RoomId, string Name, string StartUtc, string EndUtc);
    private sealed record ErrorPayload(string Error);
    private sealed record StoredBooking(long Id, int RoomId, long StartTicks, long EndTicks, long? CancelledTicks);
}
