# 📱 Mobile Tracking Guide — Cocorra Analytics

> **TL;DR for iOS & Android:** For the core product metrics, **you don't need to do anything.**
> The backend already tracks Room Joins, Mic toggles, Registration/Verification, and activity
> timing automatically from the APIs and Hubs you already call. Please **don't** fire duplicate
> analytics events for these — you'd double-count our numbers. This guide covers what's automatic,
> one small consistency ask, and what's coming next for pure UI events.

Hey team 👋 — we just shipped an event-tracking backbone so we can finally measure activation,
engagement, and room health. We deliberately built it **server-side first** to take the heavy
lifting off your plates. Here's where things stand.

---

## 1. ✨ The "Magic" — what the backend tracks automatically

Every action below is captured **server-side** the moment your existing request hits our API or
Hub. No SDK, no extra calls, no client code.

| Product event | What triggers it (you already call this) | Captured in |
|---|---|---|
| **Room joined** | `RoomHub.JoinRoom(roomId)` (SignalR) | `RoomHub` |
| **Mic activated** (speaker) | `RoomHub.ToggleMic(roomId, muteStatus)` on unmute | `RoomHub` |
| **Room left / disconnect** | `RoomHub.LeaveRoom(roomId)` + auto on socket drop | `RoomHub` |
| **Room created** | `POST` create-room REST endpoint | `RoomService` |
| **Join requested / approved** | Join + host-approve REST endpoints | `RoomService` |
| **Room ended + speaking time** | Host ends room (or host disconnects) | `RoomService` |
| **Registration** | `Register` | `AuthServices` |
| **Voice verification submitted** | Register + re-record voice | `AuthServices` |
| **Voice verification result / Activation** | Admin/automated review → Active | `AdminService` |
| **MBTI submitted** | `SubmitMbti` | `AuthServices` |
| **Account deleted** | Delete-account flow | `AuthServices` |
| **Peak active hours** | Derived from the timestamps of *all* the above | (analytics layer) |

**What this means for you:**
- ✅ **Do nothing extra** for any of these. Keep calling the same endpoints/Hub methods you
  already use.
- 🚫 **Do NOT** add a client-side "room_joined" / "mic_on" / "registered" analytics event. It
  would create duplicates and corrupt the dashboards.
- 🔒 We derive **who** did it from the **JWT**, not from your payload — so you never send a
  userId for analytics. One less thing to worry about.

---

## 2. 🎯 One small ask — keep `roomId` consistent

Because we read room events straight off your **SignalR calls**, the accuracy of "Most Active
Room" and "Speakers vs. Listeners" depends on one thing: the `roomId` you pass.

Please make sure these Hub methods always receive the **canonical room GUID** (the exact `id`
the API returned when the room was created/fetched):

```txt
JoinRoom(roomId)                  // roomId = "3fa85f64-5717-4562-b3fc-2c963f66afa6"
ToggleMic(roomId, muteStatus)
LeaveRoom(roomId)
```

Quick checklist:
- ✔️ `roomId` is the **GUID string** from the room object — not a title, slug, or channel name.
- ✔️ Same `roomId` used across `JoinRoom` → `ToggleMic` → `LeaveRoom` for a given session.
- ✔️ It's a well-formed GUID (we parse it; malformed values silently won't attribute to a room).

That's it — no new fields, just consistency on the value you're already sending. 🙏

---

## 3. 🖐️ Pure client-side events — what's next

Some things only the app can see — the backend is blind to them:
- Screen / feature views (`feature_viewed`) — e.g. opened Feed, Profile, Explore
- UI interactions — button taps, tab switches, scroll depth
- Notification opened (tapped a push before any API call)
- Time-on-screen / in-app navigation

For these we're exposing a **generic ingestion endpoint** so you can report them directly.

### The contract (already scaffolded — safe to start integrating)

```http
POST /api/events/track
Authorization: Bearer <jwt>
Content-Type: application/json

{
  "eventType": "feature_viewed",
  "properties": { "screen": "profile", "tab": "friends" }
}
```

- **Auth:** standard JWT — we attach the user from the token automatically.
- **Fire-and-forget:** returns `200 { "succeeded": true }` immediately; treat it as best-effort
  and **never block the UI or user flow** on this call.
- **Allowlist:** to prevent accidental duplication of the server-tracked events above, only a
  vetted set of client event types is accepted. Currently:
  `room_create_started`, `notification_opened`, `feature_viewed`.
  Sending anything else returns `400` by design.

### 🔑 Rules for `properties`
- **IDs and enums only** — e.g. `screen`, `roomId`, `category`, `notificationType`.
- **No PII, ever** — no emails, names, message text, phone numbers, or free-text the user typed.
- Keep it small and stable; we'll help you standardize keys.

### How to add a new event type
Ping the backend team (or drop a PR comment) with the **event name + the properties** you want
to send. We'll add it to the allowlist and confirm the schema — usually a same-day turnaround.
This keeps the event catalog clean and prevents typos from splintering our metrics.

> 📌 **Priority:** The core funnel is already covered server-side, so there's **no rush** on
> client events. Integrate `feature_viewed` / `notification_opened` whenever it fits your
> roadmap — we'll coordinate the first event names together.

---

## 4. 🧭 Session & device context (heads-up, not a task yet)

To stitch multi-step **funnels** and **retention** across a user's app session (e.g. "opened
notification → launched → joined room"), we'll eventually need a stable session identifier.

We currently set a session cookie server-side, but **cookies don't round-trip cleanly for a
native JWT app** — so we'll likely ask you to help here. Nothing to build today, but on the
roadmap:

- **`Session-Id` header** — a client-generated UUID, created once per app launch/session and
  sent on your HTTP requests (and, where feasible, on the SignalR connection). Lets us group a
  user's events into one journey.
- **Lightweight device context** — e.g. platform (`iOS`/`Android`), app version, and OS family,
  so we can compare experience across builds. (We'll define exact header names with you.)

We'll bring a concrete, minimal spec to a joint sync before asking for any changes. No surprises.

---

## ✅ Summary — your action items

| Priority | Action |
|---|---|
| 🟢 Now | **Nothing** for core metrics — they're automatic. Just don't add duplicate analytics events. |
| 🟢 Now | Double-check `roomId` passed to `JoinRoom` / `ToggleMic` / `LeaveRoom` is the canonical room GUID. |
| 🟡 Soon | Adopt `POST /api/events/track` for pure UI events (`feature_viewed`, `notification_opened`). No rush. |
| 🔵 Later | Session-Id header + device context — we'll spec it together. |

Thanks for building alongside us 🚀 — the backend's got the funnel covered for now, so you can
focus on the app. Questions or event-name requests: reach out anytime.

— *Backend Team*
