(() => {
  const dateInput = document.getElementById("selected-date");
  const roomsElement = document.getElementById("rooms");
  const roomSelect = document.getElementById("room-select");
  const startSelect = document.getElementById("start-time");
  const endSelect = document.getElementById("end-time");
  const nameInput = document.getElementById("booker-name");
  const form = document.getElementById("booking-form");
  const bookButton = document.getElementById("book-button");
  const feedback = document.getElementById("feedback");
  const currentUtcTime = document.getElementById("current-utc-time");
  const timeHint = document.getElementById("time-hint");

  const roomLists = new Map();
  const roomRequestVersions = new Map();
  let bookingInFlight = false;
  let timeChoiceKey = "";
  dateInput.value = new Date().toISOString().slice(0, 10);

  function updateBookButton() {
    bookButton.disabled = bookingInFlight || roomLists.size === 0
      || !dateInput.value || dateInput.value < dateInput.min
      || !startSelect.value || !endSelect.value;
  }

  function timeLabel(minutes) {
    const dayMinutes = minutes % 1440;
    const hours = Math.floor(dayMinutes / 60);
    const remainder = dayMinutes % 60;
    return `${String(hours).padStart(2, "0")}:${String(remainder).padStart(2, "0")}`;
  }

  function addOption(select, value, label) {
    const option = document.createElement("option");
    option.value = String(value);
    option.textContent = label;
    select.append(option);
  }

  function updateEndOptions() {
    const lastUtcDate = dateInput.value === "9999-12-31";
    const previous = Number(endSelect.value);
    endSelect.replaceChildren();
    if (!startSelect.value) {
      updateBookButton();
      return;
    }

    const start = Number(startSelect.value);
    const lastEnd = lastUtcDate ? 1425 : 1440;
    for (let minutes = start + 15; minutes <= lastEnd; minutes += 15) {
      addOption(
        endSelect,
        minutes,
        minutes === 1440 ? "00:00 next day" : timeLabel(minutes)
      );
    }

    const preferred = previous > start && previous <= lastEnd
      ? previous
      : Math.min(start + 60, lastEnd);
    endSelect.value = String(preferred);
    updateBookButton();
  }

  function updateStartOptions(now, firstMinute) {
    const selectedDate = dateInput.value;
    const today = now.toISOString().slice(0, 10);
    const previous = startSelect.value || "600";
    const firstStart = selectedDate === today ? firstMinute : 0;
    const lastStart = selectedDate === "9999-12-31" ? 1410 : 1425;
    startSelect.replaceChildren();

    if (selectedDate && selectedDate >= today) {
      for (let minutes = firstStart; minutes <= lastStart; minutes += 15) {
        addOption(startSelect, minutes, timeLabel(minutes));
      }
    }

    if (startSelect.options.length > 0) {
      const preferred = [...startSelect.options].find((option) => option.value === previous);
      startSelect.value = preferred ? previous : startSelect.options[0].value;
    }

    timeHint.textContent = selectedDate === today && startSelect.options.length === 0
      ? "No start times remain today. Choose a future UTC date."
      : "";
    updateEndOptions();
  }

  function tickUtcClock() {
    const now = new Date();
    const iso = now.toISOString();
    const today = iso.slice(0, 10);
    currentUtcTime.dateTime = iso;
    currentUtcTime.textContent = `${iso.slice(0, 19).replace("T", " ")} UTC`;
    dateInput.min = today;

    const dateRolledOver = Boolean(dateInput.value && dateInput.value < today);
    if (dateRolledOver) {
      dateInput.value = today;
      startSelect.replaceChildren();
      endSelect.replaceChildren();
      clearMessage();
    }

    const elapsedMs = now.getUTCHours() * 60 * 60 * 1000
      + now.getUTCMinutes() * 60 * 1000
      + now.getUTCSeconds() * 1000
      + now.getUTCMilliseconds();
    const firstMinute = Math.ceil(elapsedMs / (15 * 60 * 1000)) * 15;
    const key = `${dateInput.value}|${today}|${firstMinute}`;
    if (key !== timeChoiceKey) {
      updateStartOptions(now, firstMinute);
      timeChoiceKey = key;
    }

    if (dateRolledOver) void refreshAllRooms();
  }

  function showMessage(text, kind) {
    feedback.textContent = text;
    feedback.className = `feedback ${kind}`;
  }

  function clearMessage() {
    feedback.textContent = "";
    feedback.className = "feedback";
  }

  async function apiRequest(path, options) {
    const response = await fetch(path, options);
    if (!response.ok) {
      let error;
      try {
        error = await response.json();
      } catch {
        // The status remains useful if the response has no JSON body.
      }
      const requestError = new Error(error?.error || `Request failed (${response.status}).`);
      requestError.status = response.status;
      throw requestError;
    }
    return response.status === 204 ? null : response.json();
  }

  function emptyItem(text) {
    const item = document.createElement("li");
    item.className = "empty-state";
    item.textContent = text;
    return item;
  }

  function displayTime(timestamp, selectedDate) {
    const clock = timestamp.slice(11, 16);
    const bookingDate = timestamp.slice(0, 10);
    if (bookingDate === selectedDate) return clock;
    return `${clock} ${bookingDate < selectedDate ? "previous" : "next"} day`;
  }

  async function refreshRoom(roomId) {
    const list = roomLists.get(roomId);
    if (!list) return;

    const version = (roomRequestVersions.get(roomId) || 0) + 1;
    roomRequestVersions.set(roomId, version);
    const selectedDate = dateInput.value;
    if (!selectedDate) {
      list.replaceChildren(emptyItem("Select a UTC date to view bookings."));
      return;
    }
    list.replaceChildren(emptyItem("Loading bookings..."));

    try {
      const bookings = await apiRequest(
        `/api/rooms/${roomId}/bookings?date=${encodeURIComponent(selectedDate)}`
      );
      if (roomRequestVersions.get(roomId) !== version) return;

      list.replaceChildren();
      if (bookings.length === 0) {
        list.append(emptyItem("No bookings for this day."));
        return;
      }

      for (const booking of bookings) {
        const item = document.createElement("li");
        item.className = "booking-item";

        const details = document.createElement("span");
        details.className = "booking-details";
        details.textContent =
          `${displayTime(booking.startUtc, selectedDate)}–` +
          `${displayTime(booking.endUtc, selectedDate)} UTC · ${booking.name}`;

        const cancelButton = document.createElement("button");
        cancelButton.type = "button";
        cancelButton.className = "cancel-button";
        cancelButton.textContent = "Cancel";
        cancelButton.setAttribute(
          "aria-label",
          `Cancel booking ${booking.id} for ${booking.name} in room ${roomId}`
        );
        cancelButton.addEventListener("click", async () => {
          cancelButton.disabled = true;
          try {
            await apiRequest(`/api/bookings/${booking.id}`, { method: "DELETE" });
            showMessage("Booking cancelled.", "success");
            await refreshRoom(roomId);
          } catch (error) {
            showMessage(error.message, "error");
            cancelButton.disabled = false;
          }
        });

        item.append(details, cancelButton);
        list.append(item);
      }
    } catch (error) {
      if (roomRequestVersions.get(roomId) !== version) return;
      list.replaceChildren(emptyItem("Could not load bookings."));
      showMessage(error.message, "error");
    }
  }

  async function refreshAllRooms() {
    await Promise.all([...roomLists.keys()].map(refreshRoom));
  }

  function nextUtcDate(day) {
    const next = new Date(`${day}T00:00:00Z`);
    next.setUTCDate(next.getUTCDate() + 1);
    return next.toISOString().slice(0, 10);
  }

  async function createBooking(event) {
    event.preventDefault();
    clearMessage();

    const selectedDate = dateInput.value;
    const roomId = Number(roomSelect.value);
    const startValue = startSelect.value;
    const endValue = endSelect.value;
    const startMinutes = Number(startValue);
    const endMinutes = Number(endValue);
    const now = new Date();
    if (selectedDate && selectedDate < now.toISOString().slice(0, 10)) {
      tickUtcClock();
      showMessage("Past UTC dates cannot be booked. Choose today or a future date.", "error");
      return;
    }
    if (!selectedDate || !roomId || !startValue || !endValue || endMinutes <= startMinutes) {
      showMessage("Choose a date, room, and valid time range.", "error");
      return;
    }
    if (new Date(`${selectedDate}T${timeLabel(startMinutes)}:00Z`) < now) {
      tickUtcClock();
      showMessage("That start time has passed. Choose a future UTC time.", "error");
      return;
    }

    const endDate = endMinutes === 1440 ? nextUtcDate(selectedDate) : selectedDate;
    const endClock = timeLabel(endMinutes);
    const request = {
      roomId,
      name: nameInput.value.trim(),
      startUtc: `${selectedDate}T${timeLabel(startMinutes)}:00Z`,
      endUtc: `${endDate}T${endClock}:00Z`
    };

    bookingInFlight = true;
    updateBookButton();
    try {
      await apiRequest("/api/bookings", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request)
      });
      showMessage("Room booked.", "success");
      await refreshRoom(roomId);
    } catch (error) {
      tickUtcClock();
      showMessage(error.message, "error");
      if (error.status === 409) {
        await refreshRoom(roomId);
      }
    } finally {
      bookingInFlight = false;
      updateBookButton();
    }
  }

  async function initialize() {
    tickUtcClock();
    window.setInterval(tickUtcClock, 1000);
    startSelect.addEventListener("change", updateEndOptions);
    dateInput.addEventListener("change", () => {
      const today = new Date().toISOString().slice(0, 10);
      const pastDate = Boolean(dateInput.value && dateInput.value < today);
      if (pastDate) dateInput.value = today;
      startSelect.replaceChildren();
      endSelect.replaceChildren();
      timeChoiceKey = "";
      tickUtcClock();
      clearMessage();
      if (pastDate) {
        showMessage("Past UTC dates are unavailable. Showing today.", "error");
      } else if (!dateInput.value) {
        showMessage("Select a UTC date to view or create bookings.", "error");
      }
      void refreshAllRooms();
    });
    form.addEventListener("submit", createBooking);

    try {
      const rooms = await apiRequest("/api/rooms");
      roomsElement.replaceChildren();
      roomSelect.replaceChildren();

      for (const room of rooms) {
        addOption(roomSelect, room.id, room.name);

        const card = document.createElement("section");
        card.className = "room-card";
        const heading = document.createElement("h3");
        heading.textContent = room.name;
        const list = document.createElement("ul");
        list.className = "booking-list";
        roomLists.set(room.id, list);
        card.append(heading, list);
        roomsElement.append(card);
      }

      updateBookButton();
      await refreshAllRooms();
    } catch (error) {
      roomsElement.textContent = "Could not load rooms.";
      showMessage(error.message, "error");
    }
  }

  void initialize();
})();
