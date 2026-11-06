# Meeting room booking

A small booking app for three meeting rooms. Users can view a room's active bookings for a UTC date, create a booking, and cancel one. An ASP.NET Core 10 Minimal API serves the HTML/CSS/JavaScript frontend and stores bookings in SQLite.

## Run locally

Use a terminal with Docker Compose running Linux containers. These commands were tested in Ubuntu under WSL 2 with Docker Desktop's Ubuntu integration enabled. Run them from the root of your cloned repository. No host .NET or Node.js installation is needed.

```bash
docker version
docker compose up -d --build app
curl http://localhost:8080/health
```

Open <http://localhost:8080> to use the app. `GET /health` returns `{"status":"ok"}` when it is ready. To stop it:

```bash
docker compose down
```

SQLite lives at `/data/bookings.db` in the Compose `bookings-data` named volume. `docker compose down` leaves that volume intact, so bookings survive a container restart or rebuild. Startup creates the schema and seeds Room A, Room B, and Room C (IDs 1, 2, and 3) if they are absent.

## Run the tests

From the same Ubuntu directory:

```bash
docker compose run --rm api-tests
docker compose run --rm ui-tests
```

The API suite uses xUnit integration tests with `WebApplicationFactory` and a separate temporary SQLite file per test. It exercises every endpoint, database state, validation (including past starts and end times after the allowed midnight boundary), cancellation and rebooking, and two competing overlapping requests. The Node.js/jsdom UI tests cover booking and cancelling the last UTC slot, refreshing a stale room list after a conflict, and excluding past dates and times while updating the UTC clock. The last container runs passed 8/8 API tests and 3/3 UI tests.

## API

Times use the exact UTC format `YYYY-MM-DDTHH:mm:ssZ`. Error responses use `{"error":"message"}`.

| Action | Request | Success | Other results |
| --- | --- | --- | --- |
| List rooms | `GET /api/rooms` | `200` with room IDs and names | — |
| List one room's active bookings for a day | `GET /api/rooms/{roomId}/bookings?date=YYYY-MM-DD` | `200` with bookings ordered by start time | `400` invalid date; `404` unknown room |
| Create booking | `POST /api/bookings` with JSON `roomId`, `name`, `startUtc`, `endUtc` | `201` with the created booking | `400` invalid input; `404` unknown room; `409` overlap; `503` database busy |
| Cancel booking | `DELETE /api/bookings/{id}` | `204` | `404` missing or already cancelled booking |

A booking object contains `id`, `roomId`, `name`, `startUtc`, and `endUtc`. The room-scoped listing returns active bookings that **overlap** the selected UTC date. A booking ending exactly at midnight appears only on its start date. For example:

```bash
curl -X POST http://localhost:8080/api/bookings \
  -H 'Content-Type: application/json' \
  -d '{"roomId":1,"name":"Alex","startUtc":"2030-01-15T10:00:00Z","endUtc":"2030-01-15T10:30:00Z"}'

curl 'http://localhost:8080/api/rooms/1/bookings?date=2030-01-15'
# Use the ID returned by POST:
curl -X DELETE http://localhost:8080/api/bookings/1
```

The final command's `1` is only an example booking ID; use the ID returned by your POST request. The final slot of a UTC day can be booked from `23:45` to `00:00 next day`; that midnight is an exclusive end, so the booking occupies only its start date.

### Booking rules

- `name` is trimmed and must contain 1–100 characters. The room must exist.
- Start and end must be on 15-minute UTC boundaries, with zero seconds: for example `10:00`, `10:15`, `10:30`, or `10:45`. The shortest booking is 15 minutes.
- The start must not be before the server's current UTC time. The UI shows the current UTC time, prevents selecting past dates, and offers only remaining start times today. Its choices update as time passes; the API enforces the rule even if a client bypasses the UI.
- The end must follow the start and stay within one UTC calendar day. Exactly `00:00Z` on the next date is allowed as an exclusive end; `23:00–02:00 next day` is invalid. The selected UI date is the booking's start date.
- Intervals are half-open, `[start, end)`. Adjacent bookings such as `10:00–11:00` and `11:00–12:00` are allowed. Overlapping active bookings in the same room are rejected; different rooms can be booked at the same time.
- Cancellation sets a cancellation timestamp. Cancelled rows remain in SQLite but disappear from active listings, and their time can be booked again.

## Concurrency and implementation

The overlap check is a SQLite `BEFORE INSERT` trigger on active bookings. It checks `existing.start < new.end` and `new.start < existing.end` for the same room. SQLite serializes writes, so concurrent inserts cannot both pass the trigger and commit an overlapping active booking. The app enables WAL mode at startup, opens a connection per request, and uses a bounded busy timeout. The concurrency integration test starts two overlapping POST requests through separate clients and verifies one `201`, one `409`, and one saved row.

The frontend uses the four API routes, shows all three rooms' bookings that overlap the selected UTC day, and offers quarter-hour start/end choices. The single End selector includes `00:00 next day` as its final boundary. It builds UTC timestamps directly from the selected date. After a conflict, the room's bookings are reloaded so reservations made in another tab become visible.

## Limitations

- There is no login or booking-owner identity. **Anyone can cancel any active booking**, in the UI or by calling `DELETE /api/bookings/{id}`. The entered name is unverified. Owner-only cancellation requires authentication and server-side authorization; hiding the button would not protect the API.
- SQLite is suitable for this small local app, but its single-writer model limits write throughput.
- The app does not support recurring bookings, editing, notifications, or local-time-zone conversion. All displayed and transmitted times are UTC.

## Next improvements

In priority order:

1. **Registration and login:** Give users accounts and enforce booking ownership on the server, so only the owner or an administrator can cancel or change a booking.
2. **Booking UI:** Add a visual room schedule, clearer availability, and better mobile and keyboard support. Refresh bookings automatically when another user makes a reservation.
3. **Local time zones:** Let users view and enter times in their own time zone while storing bookings in UTC, including daylight saving changes.
4. **Manage bookings:** Allow users to reschedule bookings and create recurring reservations, with conflict checks for every occurrence.
5. **Notifications:** Send confirmations, reminders, and cancellation notices.
6. **Production readiness:** Add database migrations, audit logs, monitoring, and rate limits. Consider PostgreSQL with a database-enforced range exclusion rule if usage grows beyond SQLite's single-writer capacity.

## AI use and personal review

Libraries used: `Microsoft.Data.Sqlite` for persistence, xUnit and `Microsoft.AspNetCore.Mvc.Testing` for API integration tests, and jsdom for the frontend interaction test. Their versions are pinned in the project and npm lock files.

OpenAI Codex helped design, implement, test, and document this app. It drafted and revised the backend, frontend, Docker setup, tests, and this README, ran the containerized test suites, and checked the browser flows. I directed the Ubuntu/Docker workflow, quarter-hour choices, and rejection of past bookings. I changed the booking-listing requirement from date-only retrieval to retrieval by both room ID and date; Codex implemented `GET /api/rooms/{roomId}/bookings?date=YYYY-MM-DD`. I also suggested handling simultaneous booking conflicts in the database; Codex implemented that approach with the SQLite overlap trigger and concurrent-request test.

I reviewed the project files and ran both the API integration and frontend UI test suites during development. During my review, I found issues and made these design decisions with Codex:

- In two tabs, a conflicting booking showed the error but did not show the reservation created in the other tab. Codex changed the UI to refresh that room after a conflict and added a regression test.
- The app allowed bookings in the past. I requested server-side rejection, a live UTC clock, and controls that prevent choosing past days or elapsed start times. Codex implemented these changes and added tests.
- I explored allowing 23:00–02:00 overnight bookings, then caught the conflict with the assessment's one-calendar-day rule. I chose to restore the single-day limit; Codex updated the API, UI, tests, and documentation.
- Anyone could cancel another person's booking. I asked for this to be stated as a limitation; owner-only cancellation remains outside this no-login assessment scope.

The code fixes above were made with Codex. My contributions were the design decisions, file review, running the tests, and identifying and reporting the issues.
