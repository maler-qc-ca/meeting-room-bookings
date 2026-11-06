import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { JSDOM } from "jsdom";

const html = readFileSync(
  fileURLToPath(new URL("../BookingApp/wwwroot/index.html", import.meta.url)),
  "utf8"
);
const appScript = readFileSync(
  fileURLToPath(new URL("../BookingApp/wwwroot/app.js", import.meta.url)),
  "utf8"
);

async function waitFor(predicate, description) {
  for (let attempt = 0; attempt < 100; attempt += 1) {
    if (predicate()) return;
    await new Promise((resolve) => setImmediate(resolve));
  }
  throw new Error(`Timed out waiting for ${description}`);
}

function response(status, body) {
  return {
    ok: status >= 200 && status < 300,
    status,
    async json() { return body; }
  };
}

function freezeWindowClock(window, isoTimestamp) {
  const NativeDate = window.Date;
  const nowMs = NativeDate.parse(isoTimestamp);
  window.Date = class extends NativeDate {
    constructor(...args) {
      super(...(args.length === 0 ? [nowMs] : args));
    }

    static now() { return nowMs; }
  };
}

test("a user books the last UTC slot and cancels it", async () => {
  const dom = new JSDOM(html, {
    url: "http://localhost/",
    runScripts: "outside-only"
  });
  const { window } = dom;
  const { document } = window;
  freezeWindowClock(window, "2026-11-05T12:00:00Z");
  let activeBookings = [];
  let postedBooking;
  let deletedPath;

  window.fetch = async (path, options = {}) => {
    const requestUrl = new URL(path, "http://localhost");
    const method = options.method ?? "GET";

    if (method === "GET" && requestUrl.pathname === "/api/rooms") {
      return response(200, [
        { id: 1, name: "Room A" },
        { id: 2, name: "Room B" },
        { id: 3, name: "Room C" }
      ]);
    }

    const roomMatch = /^\/api\/rooms\/(\d+)\/bookings$/.exec(requestUrl.pathname);
    if (method === "GET" && roomMatch) {
      const roomId = Number(roomMatch[1]);
      const date = requestUrl.searchParams.get("date");
      return response(200, activeBookings.filter(
        (booking) => booking.roomId === roomId && booking.startUtc.slice(0, 10) === date
      ));
    }

    if (method === "POST" && requestUrl.pathname === "/api/bookings") {
      postedBooking = JSON.parse(options.body);
      const created = { id: 42, ...postedBooking };
      activeBookings.push(created);
      return response(201, created);
    }

    if (method === "DELETE" && requestUrl.pathname === "/api/bookings/42") {
      deletedPath = requestUrl.pathname;
      activeBookings = [];
      return response(204, null);
    }

    throw new Error(`Unexpected API request: ${method} ${requestUrl.pathname}`);
  };

  try {
    window.eval(appScript);
    await waitFor(
      () => document.querySelectorAll(".room-card").length === 3
        && !document.getElementById("book-button").disabled,
      "rooms to load"
    );

    const dateInput = document.getElementById("selected-date");
    dateInput.value = "2026-11-05";
    dateInput.dispatchEvent(new window.Event("change", { bubbles: true }));

    const startSelect = document.getElementById("start-time");
    startSelect.value = "1380";
    startSelect.dispatchEvent(new window.Event("change", { bubbles: true }));
    const endSelect = document.getElementById("end-time");
    assert.deepEqual([...endSelect.options].map((option) => option.value),
      ["1395", "1410", "1425", "1440"],
      "23:00 can end at midnight but not later on the next date");

    startSelect.value = "1425";
    startSelect.dispatchEvent(new window.Event("change", { bubbles: true }));
    endSelect.value = "1440";
    assert.equal(endSelect.value, "1440");
    assert.equal(endSelect.selectedOptions[0].textContent, "00:00 next day");

    document.getElementById("booker-name").value = "Ava";
    document.getElementById("booking-form").dispatchEvent(
      new window.Event("submit", { bubbles: true, cancelable: true })
    );

    await waitFor(
      () => postedBooking && document.querySelector(".booking-item")?.textContent.includes("Ava"),
      "the new booking to appear"
    );
    assert.deepEqual(postedBooking, {
      roomId: 1,
      name: "Ava",
      startUtc: "2026-11-05T23:45:00Z",
      endUtc: "2026-11-06T00:00:00Z"
    });
    assert.match(document.querySelector(".booking-item").textContent, /23:45–00:00 next day UTC/);

    document.querySelector(".cancel-button").click();
    await waitFor(
      () => deletedPath === "/api/bookings/42"
        && document.querySelector(".booking-list")?.textContent.includes("No bookings for this day."),
      "the cancelled booking to disappear"
    );
    assert.equal(document.getElementById("feedback").textContent, "Booking cancelled.");
  } finally {
    window.close();
  }
});

test("a conflict refreshes the room to show a booking made in another tab", async () => {
  const dom = new JSDOM(html, {
    url: "http://localhost/",
    runScripts: "outside-only"
  });
  const { window } = dom;
  const { document } = window;
  freezeWindowClock(window, "2026-11-05T09:00:00Z");
  let activeBookings = [];
  let attemptedBooking;
  let roomAReads = 0;

  window.fetch = async (path, options = {}) => {
    const requestUrl = new URL(path, "http://localhost");
    const method = options.method ?? "GET";

    if (method === "GET" && requestUrl.pathname === "/api/rooms") {
      return response(200, [
        { id: 1, name: "Room A" },
        { id: 2, name: "Room B" },
        { id: 3, name: "Room C" }
      ]);
    }

    const roomMatch = /^\/api\/rooms\/(\d+)\/bookings$/.exec(requestUrl.pathname);
    if (method === "GET" && roomMatch) {
      const roomId = Number(roomMatch[1]);
      if (roomId === 1) roomAReads += 1;
      const date = requestUrl.searchParams.get("date");
      return response(200, activeBookings.filter(
        (booking) => booking.roomId === roomId && booking.startUtc.slice(0, 10) === date
      ));
    }

    if (method === "POST" && requestUrl.pathname === "/api/bookings") {
      attemptedBooking = JSON.parse(options.body);
      // The first tab booked this slot after the second tab loaded its empty list.
      activeBookings = [{ id: 77, ...attemptedBooking, name: "First tab" }];
      return response(409, { error: "This room is already booked for that time." });
    }

    throw new Error(`Unexpected API request: ${method} ${requestUrl.pathname}`);
  };

  try {
    window.eval(appScript);
    await waitFor(
      () => document.querySelectorAll(".booking-list").length === 3
        && [...document.querySelectorAll(".booking-list")].every(
          (list) => list.textContent.includes("No bookings for this day.")
        ),
      "the initially empty room lists"
    );
    assert.equal(roomAReads, 1);

    document.getElementById("booker-name").value = "Second tab";
    document.getElementById("booking-form").dispatchEvent(
      new window.Event("submit", { bubbles: true, cancelable: true })
    );

    await waitFor(
      () => attemptedBooking && roomAReads >= 2
        && document.querySelector(".room-card .booking-item")?.textContent.includes("First tab"),
      "Room A to refresh after the conflict"
    );
    assert.equal(document.getElementById("feedback").textContent,
      "This room is already booked for that time.");
    assert.match(document.querySelector(".room-card .booking-item").textContent,
      /10:00–11:00 UTC · First tab/);
  } finally {
    window.close();
  }
});

test("the UTC clock and booking controls exclude past dates and start times", async () => {
  const dom = new JSDOM(html, {
    url: "http://localhost/",
    runScripts: "outside-only"
  });
  const { window } = dom;
  const { document } = window;
  const NativeDate = window.Date;
  let nowMs = NativeDate.parse("2026-11-05T10:04:30Z");
  let clockTick;

  window.Date = class extends NativeDate {
    constructor(...args) {
      super(...(args.length === 0 ? [nowMs] : args));
    }

    static now() { return nowMs; }
  };
  window.setInterval = (callback) => {
    clockTick = callback;
    return 1;
  };

  window.fetch = async (path, options = {}) => {
    const requestUrl = new URL(path, "http://localhost");
    const method = options.method ?? "GET";
    if (method === "GET" && requestUrl.pathname === "/api/rooms") {
      return response(200, [
        { id: 1, name: "Room A" },
        { id: 2, name: "Room B" },
        { id: 3, name: "Room C" }
      ]);
    }
    if (method === "GET" && /^\/api\/rooms\/\d+\/bookings$/.test(requestUrl.pathname)) {
      return response(200, []);
    }
    throw new Error(`Unexpected API request: ${method} ${requestUrl.pathname}`);
  };

  try {
    window.eval(appScript);
    await waitFor(
      () => document.querySelectorAll(".booking-list").length === 3
        && [...document.querySelectorAll(".booking-list")].every(
          (list) => list.textContent.includes("No bookings for this day.")
        ),
      "room listings to load"
    );

    const dateInput = document.getElementById("selected-date");
    const startSelect = document.getElementById("start-time");
    const bookButton = document.getElementById("book-button");
    const clock = document.getElementById("current-utc-time");
    assert.ok(clock, "current UTC time should be visible");
    assert.match(clock.textContent, /10:04.*UTC/);
    assert.equal(dateInput.min, "2026-11-05");
    assert.equal(dateInput.value, "2026-11-05");
    assert.ok(clockTick, "the clock should update automatically");
    assert.equal(startSelect.value, "615");
    assert.ok([...startSelect.options].every((option) => Number(option.value) >= 615));

    dateInput.value = "2026-11-04";
    dateInput.dispatchEvent(new window.Event("change", { bubbles: true }));
    assert.equal(dateInput.value, "2026-11-05", "a past UTC day must not remain selected");

    dateInput.value = "2026-11-06";
    dateInput.dispatchEvent(new window.Event("change", { bubbles: true }));
    assert.equal(dateInput.value, "2026-11-06");
    assert.equal(startSelect.options[0].value, "0", "all start times are valid on a future day");
    assert.equal(startSelect.value, "600", "a new date uses the 10:00 default");

    dateInput.value = "2026-11-05";
    dateInput.dispatchEvent(new window.Event("change", { bubbles: true }));
    nowMs = NativeDate.parse("2026-11-05T10:16:00Z");
    await clockTick();
    assert.match(clock.textContent, /10:16.*UTC/);
    assert.equal(startSelect.options[0].value, "630", "expired quarter-hour must disappear");

    nowMs = NativeDate.parse("2026-11-05T23:46:00Z");
    await clockTick();
    assert.equal(startSelect.options.length, 0, "no start times remain today");
    assert.equal(bookButton.disabled, true);

    nowMs = NativeDate.parse("2026-11-06T00:01:00Z");
    await clockTick();
    assert.equal(dateInput.min, "2026-11-06");
    assert.equal(dateInput.value, "2026-11-06", "the selected day advances at UTC midnight");
    assert.equal(startSelect.options[0].value, "15");
  } finally {
    window.close();
  }
});
