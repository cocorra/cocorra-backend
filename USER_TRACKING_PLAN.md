# User Tracking & Analytics Plan — Cocorra

> **Goal:** Understand how people actually use Cocorra (a voice-room social app) so we can
> improve activation, engagement, retention, and safety with evidence instead of guesses.

---

## 1. Where we are today

Cocorra already ships an **aggregate reporting** layer:

| Layer | What exists |
|-------|-------------|
| `AnalyticsController` | `Summary`, `Users/Growth`, `Rooms`, `Participation`, `Reports` (Admin/Coach only) |
| `AnalyticsService` | Cached (10 min), stampede-protected reads |
| `AnalyticsRepository` | EF queries over **existing** tables (Users, Rooms, RoomParticipants, Reports) |

**The gap:** these dashboards can only answer questions the *current schema* happens to
record. We know *how many* rooms were created, but not *how many people opened the create-room
screen and abandoned it*. We can count users, but not measure the **funnel** from register →
verify → first room → retained.

This plan adds a lightweight **event-tracking backbone** that fills that gap and feeds the
dashboards we already have.

### Design principles

1. **One append-only event table.** Don't scatter tracking columns across domain tables.
2. **Fire-and-forget.** Tracking must never slow down or break a user request.
3. **Reuse the existing layered pattern** (API → BLL service → DAL repository, EF Core).
4. **Privacy by default.** Collect the minimum, pseudonymize, expire, and honor deletion.

---

## 2. Key user events to track

Events are grouped by the **user lifecycle**. Each maps to a decision we want to make.

### A. Acquisition & Activation (the make-or-break funnel)
| Event | When it fires | Question it answers |
|-------|---------------|---------------------|
| `user_registered` | `Authentication/Register` succeeds | How many sign-ups per day/source? |
| `email_confirmed` | `ConfirmEmail` succeeds | Where do we lose people in verification? |
| `voice_verification_submitted` | Voice recording uploaded | Is the voice step a drop-off point? |
| `voice_verification_result` | Admin/automated Active / ReRecord | Approval rate, re-record friction |
| `mbti_submitted` | `SubmitMbti` | Do users complete the personality step? |
| `activation_completed` | First time status → **Active** | **North-star activation rate** |

### B. Core engagement — Voice Rooms (the heart of the product)
| Event | When it fires | Question it answers |
|-------|---------------|---------------------|
| `room_create_started` | User opens the create-room flow | Create funnel top |
| `room_created` | `Room/Create` | Create conversion + category mix |
| `room_join_requested` | `Room/{id}/Join` | Demand per room/category |
| `room_join_approved` | Host approves | Approval latency, rejection rate |
| `room_joined` | Participant becomes Active | Actual attendance |
| `room_left` | Participant leaves / disconnects | Session length, early exits |
| `mic_activated` | Participant unmutes / takes stage | **Passive vs. active** participation |
| `speaking_time_logged` | On room end (`TotalSpokenSeconds`) | Who actually talks; room health |
| `room_ended` | `Room/{id}/End` | Room duration, peak concurrency |

### C. Social graph & messaging
| Event | When it fires | Question it answers |
|-------|---------------|---------------------|
| `friend_request_sent` / `_accepted` | Friend flow | Is the social graph growing? |
| `message_sent` | Chat message persisted | 1:1 engagement depth |
| `notification_opened` | Push/in-app notification tapped | Which notifications drive return? |

### D. Retention & habit
| Event | When it fires | Question it answers |
|-------|---------------|---------------------|
| `session_started` | Login / token refresh with new session | DAU / WAU / MAU |
| `feature_viewed` | Key screen opened (feed, profile) | What do users actually look at? |

### E. Safety & friction (protecting the community)
| Event | When it fires | Question it answers |
|-------|---------------|---------------------|
| `user_reported` | `Support/Report` | Abuse hotspots, repeat offenders |
| `user_blocked` | `Users/block/{target}` | Friction between users |
| `support_ticket_opened` | `Support/Ticket` | Top pain points |
| `account_deleted` | `DeleteAccount` | **Churn** — capture a reason |

### "Conversion points" that matter most for Cocorra
Cocorra isn't paid, so *conversion* = **progression through value milestones**:

```
Register → Email confirmed → Voice verified (Active) → Joined first room
        → Spoke in a room → Made a friend → Returned next day (D1 retention)
```

Instrument every arrow above. The single most important metric is **Activation
(Register → Active)** and **D1/D7 retention** after the first room.

---

## 3. Implementation guide

The tracking backbone is **one table + one service + one enum + integration calls**. It slots
directly into the existing `Cocorra.DAL` / `Cocorra.BLL` / `Cocorra.API` structure.

### Step 1 — The event model (`Cocorra.DAL/Models/UserEvent.cs`)

```csharp
using System.ComponentModel.DataAnnotations;

namespace Cocorra.DAL.Models;

public class UserEvent
{
    public long Id { get; set; }                 // bigint identity — high volume

    /// <summary>Nullable: some events (e.g. registration failure) have no user yet.</summary>
    public Guid? UserId { get; set; }
    public virtual ApplicationUser? User { get; set; }

    [Required, MaxLength(64)]
    public string EventType { get; set; } = string.Empty;   // use EventTypes constants

    /// <summary>Free-form JSON for event-specific fields (roomId, category, source…).
    /// NEVER store message bodies, emails, or other PII here.</summary>
    public string? PropertiesJson { get; set; }

    /// <summary>Groups events into a single app session for funnel analysis.</summary>
    public Guid? SessionId { get; set; }

    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Store a HASH of the IP, never the raw IP (see Privacy §4).</summary>
    [MaxLength(64)]
    public string? IpHash { get; set; }

    [MaxLength(256)]
    public string? UserAgent { get; set; }
}
```

Stable event names as constants (avoids typos, enables refactoring):

```csharp
namespace Cocorra.DAL.Models;

public static class EventTypes
{
    public const string UserRegistered        = "user_registered";
    public const string EmailConfirmed         = "email_confirmed";
    public const string ActivationCompleted    = "activation_completed";
    public const string RoomCreateStarted      = "room_create_started";
    public const string RoomCreated            = "room_created";
    public const string RoomJoinRequested      = "room_join_requested";
    public const string RoomJoined             = "room_joined";
    public const string RoomLeft               = "room_left";
    public const string MicActivated           = "mic_activated";
    public const string MessageSent            = "message_sent";
    public const string FriendRequestSent      = "friend_request_sent";
    public const string SessionStarted         = "session_started";
    public const string UserReported           = "user_reported";
    public const string AccountDeleted         = "account_deleted";
    // …extend as the event catalog in §2 grows.
}
```

### Step 2 — Register the table (`AppDbContext`)

```csharp
public DbSet<UserEvent> UserEvents => Set<UserEvent>();

protected override void OnModelCreating(ModelBuilder builder)
{
    base.OnModelCreating(builder);

    builder.Entity<UserEvent>(e =>
    {
        // Indexes that match how analytics queries filter: by type+time, and by user+time.
        e.HasIndex(x => new { x.EventType, x.OccurredAtUtc });
        e.HasIndex(x => new { x.UserId, x.OccurredAtUtc });

        // Deleting a user nulls their events (keep aggregates, drop the link).
        e.HasOne(x => x.User)
         .WithMany()
         .HasForeignKey(x => x.UserId)
         .OnDelete(DeleteBehavior.SetNull);
    });
}
```

Then create the migration:

```bash
dotnet ef migrations add AddUserEventTracking --project Cocorra.DAL --startup-project Cocorra.API
dotnet ef database update --project Cocorra.DAL --startup-project Cocorra.API
```

### Step 3 — The tracking service (`Cocorra.BLL/Services/EventTracking/`)

```csharp
public interface IEventTracker
{
    /// <summary>Fire-and-forget. Never throws to the caller.</summary>
    void Track(string eventType, Guid? userId = null, object? properties = null);
}
```

The implemented `EventTracker` also **enriches** each event from the current request via
`IHttpContextAccessor`: it resolves the `SessionId` (stamped by the middleware in Step 4),
truncates the User-Agent, and hashes the client IP with a salted SHA-256. It never throws.

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cocorra.BLL.Services.EventTracking;

/// <summary>
/// Non-blocking event tracker. Writes are queued to an in-memory channel and
/// persisted in batches by a background service — the user's request never waits
/// on the analytics DB write, and a tracking failure can never break a feature.
/// </summary>
public class EventTracker : IEventTracker
{
    private readonly Channel<UserEvent> _queue;
    private readonly ILogger<EventTracker> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;   // request enrichment
    private readonly IConfiguration _configuration;                // IP-hash salt

    public EventTracker(Channel<UserEvent> queue, ILogger<EventTracker> logger,
        IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
    {
        _queue = queue;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
        _configuration = configuration;
    }

    public void Track(string eventType, Guid? userId = null, object? properties = null)
    {
        try
        {
            var http = _httpContextAccessor.HttpContext;
            Guid? sessionId = null; string? ipHash = null, userAgent = null;

            if (http is not null)
            {
                // Fall back to the authenticated user when the caller didn't pass one.
                if (userId is null && http.User.Identity?.IsAuthenticated == true &&
                    Guid.TryParse(http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var uid))
                    userId = uid;

                if (http.Items.TryGetValue("SessionId", out var s) && s is Guid sid)
                    sessionId = sid;

                userAgent = http.Request.Headers["User-Agent"].ToString();
                if (userAgent.Length > 256) userAgent = userAgent[..256];

                var ip = http.Connection.RemoteIpAddress?.ToString();
                var salt = _configuration["Analytics:IpHashSalt"];   // NO hardcoded fallback
                if (!string.IsNullOrEmpty(ip) && !string.IsNullOrEmpty(salt))
                    ipHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(salt + ip)));
            }

            var evt = new UserEvent
            {
                EventType = eventType,
                UserId = userId,
                PropertiesJson = properties is null ? null : JsonSerializer.Serialize(properties),
                SessionId = sessionId,
                IpHash = ipHash,
                UserAgent = userAgent,
                OccurredAtUtc = DateTime.UtcNow
            };

            // TryWrite is synchronous & lock-free; drops silently only if the queue is full.
            if (!_queue.Writer.TryWrite(evt))
                _logger.LogWarning("Event queue full; dropped {EventType}", eventType);
        }
        catch (Exception ex)
        {
            // Tracking must NEVER surface to the user.
            _logger.LogError(ex, "Failed to enqueue event {EventType}", eventType);
        }
    }
}
```

Background writer that batches inserts:

```csharp
public class EventFlushService : BackgroundService
{
    private readonly Channel<UserEvent> _queue;
    private readonly IServiceScopeFactory _scopeFactory;

    public EventFlushService(Channel<UserEvent> queue, IServiceScopeFactory scopeFactory)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var batch = new List<UserEvent>(capacity: 100);

        while (await _queue.Reader.WaitToReadAsync(ct))
        {
            while (batch.Count < 100 && _queue.Reader.TryRead(out var evt))
                batch.Add(evt);

            if (batch.Count == 0) continue;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.UserEvents.AddRange(batch);
            await db.SaveChangesAsync(ct);
            batch.Clear();
        }
    }
}
```

A second background service, `EventCleanupService`, enforces retention (Privacy §4): once a
day it `ExecuteDeleteAsync`-purges events older than 180 days.

Wire it up in `Program.cs` (next to the existing Analytics registrations, line ~162):

```csharp
// Event tracking backbone
// Fail fast if the IP-hash salt is missing — a public fallback salt would make IP
// hashes reversible, defeating pseudonymization (see Privacy §4).
if (string.IsNullOrWhiteSpace(builder.Configuration["Analytics:IpHashSalt"]))
    throw new InvalidOperationException(
        "Analytics:IpHashSalt is not configured. Set a secret salt before starting.");

builder.Services.AddHttpContextAccessor();   // required by EventTracker for enrichment
builder.Services.AddSingleton(Channel.CreateBounded<UserEvent>(
    new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite }));
builder.Services.AddSingleton<IEventTracker, EventTracker>();
builder.Services.AddHostedService<EventFlushService>();    // batched writes
builder.Services.AddHostedService<EventCleanupService>();  // 180-day retention purge
```

Configure the salt in `appsettings.json` (or a secret store / env var in production):

```json
"Analytics": { "IpHashSalt": "<a long random secret — not this literal>" }
```

### Step 4 — Emit events at the integration points

Inject `IEventTracker` into existing services and fire *after* the domain action succeeds.

**In `AuthServices` (registration & activation):**
```csharp
var result = await _userManager.CreateAsync(user, dto.Password);
if (result.Succeeded)
    _eventTracker.Track(EventTypes.UserRegistered, user.Id, new { source = dto.Source });
```

**In `RoomService.CreateAsync`:**
```csharp
await _roomRepository.AddAsync(room);
_eventTracker.Track(EventTypes.RoomCreated, ownerId,
    new { roomId = room.Id, category = room.Category.ToString(), isPublic = room.IsPublic });
```

**In `ChatHub` / `RoomHub` (SignalR real-time events):**
```csharp
public async Task JoinRoom(Guid roomId)
{
    await _roomService.JoinAsync(roomId, UserId);
    _eventTracker.Track(EventTypes.RoomJoined, UserId, new { roomId });
}
```

**For high-value HTTP endpoints, a middleware** captures session + request context once
(so you don't repeat IP-hashing everywhere):

```csharp
public class SessionTrackingMiddleware
{
    private readonly RequestDelegate _next;
    public SessionTrackingMiddleware(RequestDelegate next) => _next = next;

    public async Task Invoke(HttpContext ctx, IEventTracker tracker)
    {
        // Stamp a session id (cookie) into ctx.Items["SessionId"] for funnel stitching (see §2).
        // Emit session_started once per session on the first authenticated request.
        await _next(ctx);
    }
}
```

> ⚠️ **Client model:** the implemented middleware stitches sessions via a `Secure`,
> `SameSite=Strict` cookie. Cocorra's clients are mobile (JWT — `ValidAudience:
> CocorraMobileApp`), so a browser cookie won't reliably round-trip. If `SessionId` comes
> back null for most events, switch to a client-sent `X-Session-Id` header derived from the
> auth session instead of a cookie.

**Client-emitted events** (UI-only signals with no server action — `room_create_started`,
`notification_opened`, `feature_viewed`) go through `EventsController`:

```csharp
[HttpPost("api/events/track")]
public IActionResult Track([FromBody] TrackEventDto dto)
{
    // Allowlist: clients may ONLY emit UI signals. Server-owned lifecycle events
    // (activation_completed, room_created, …) are fired server-side so the funnel
    // can't be forged. userId comes from the token, never the request body.
    if (dto is null || !ClientAllowedEvents.Contains(dto.EventType))
        return BadRequest(new { succeeded = false });

    var userId = Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var g) ? g : (Guid?)null;
    _eventTracker.Track(dto.EventType, userId, dto.Properties);
    return Ok(new { succeeded = true });
}
```

> **Never** enforce the "no PII in `PropertiesJson`" rule (§4) on trust alone for this
> endpoint — clients send `Properties` freely. Keep the property payloads to IDs/enums and,
> if the catalog grows, validate them per allowed event type.

### Step 5 — Read events via the existing analytics layer

Extend `IAnalyticsRepository` with funnel/retention queries — same pattern as today:

```csharp
// Activation funnel: count of distinct users reaching each milestone in a window.
public async Task<Dictionary<string, int>> GetFunnelAsync(
    string[] steps, DateTime fromUtc, DateTime toUtc)
{
    return await _db.UserEvents
        .Where(e => steps.Contains(e.EventType)
                 && e.OccurredAtUtc >= fromUtc && e.OccurredAtUtc <= toUtc
                 && e.UserId != null)
        .GroupBy(e => e.EventType)
        .Select(g => new { g.Key, Count = g.Select(x => x.UserId).Distinct().Count() })
        .ToDictionaryAsync(x => x.Key, x => x.Count);
}
```

Then expose it as a new route in `Router.AnalyticsRouting` (e.g. `Funnel`, `Retention`) and a
new `AnalyticsController` action, reusing the existing cache + `SemaphoreSlim` stampede guard
in `AnalyticsService`. No new infrastructure needed.

### Rollout order
1. Ship the table + service + background writer (Steps 1–3) — zero behavior change.
2. Instrument the **activation funnel** events first (highest-value).
3. Add room/engagement events.
4. Build the funnel & retention dashboard endpoints (Step 5).
5. Expand the event catalog as questions arise.

---

## 4. Data privacy best practices (non-negotiable)

Cocorra handles **voice recordings, personality data (MBTI), and minors' ages** — treat
tracking data as sensitive from day one.

### Collect the minimum
- **Never** put PII in `PropertiesJson`: no emails, names, message bodies, or voice paths.
  Store **IDs and enums** only (roomId, category, status).
- Track *events*, not *content*. `message_sent` records that a message happened — never the text.

### Pseudonymize
- Store `UserId` (already a pseudonymous GUID), not name/email.
- **Hash IP addresses** with a salted SHA-256 before storing; never persist raw IPs.
  ```csharp
  IpHash = Convert.ToHexString(
      SHA256.HashData(Encoding.UTF8.GetBytes(salt + remoteIp)));
  ```
  The salt (`Analytics:IpHashSalt`) is **mandatory** — there is no hardcoded fallback, and
  the app refuses to start without it (Step 3). A public/default salt would make the hashes
  reversible and defeat the whole point.
- Truncate/generalize user-agent strings (browser + OS family is enough).

### Retention & deletion
- **Auto-expire raw events** (e.g. 90–180 days) via a scheduled cleanup job; keep only
  pre-aggregated rollups long-term.
  ```csharp
  await _db.UserEvents
      .Where(e => e.OccurredAtUtc < DateTime.UtcNow.AddDays(-180))
      .ExecuteDeleteAsync();
  ```
- **Honor account deletion.** The existing `DeleteAccount` flow must also purge or null the
  user's events. The `OnDelete(DeleteBehavior.SetNull)` FK (Step 2) does this automatically —
  events survive as anonymous aggregates, the personal link is severed.

### Access control & security
- Analytics endpoints stay **Admin/Coach only** (already enforced by
  `[Authorize(Roles = "Admin,Coach")]`).
- Dashboards should show **aggregates**, not raw per-user event logs. Never expose one user's
  activity trail to another user.
- Log access to analytics endpoints for audit.

### Consent & transparency
- Document what is tracked and why in the privacy policy.
- Distinguish **operational analytics** (product improvement, legitimate interest) from any
  future marketing/third-party sharing — the latter requires explicit opt-in.
- For minors, apply the stricter standard: no behavioral profiling beyond safety and core
  product function.

### Golden rule
> If a tracking write fails, the user must not notice. If a tracking field could identify or
> embarrass a user, it should not exist. **Track behavior, protect people.**

---

## 5. Quick reference — what to build first

| Priority | Deliverable | Effort | Value | Status |
|----------|-------------|--------|-------|--------|
| 🔴 P0 | `UserEvent` table + migration + `IEventTracker` + background writer | S | Foundation | ✅ Done |
| 🟡 P2 | Retention cleanup job + IP hashing (salted, mandatory) | S | Privacy compliance | ✅ Done |
| 🟢 — | Client event ingest endpoint (`/api/events/track`) with allowlist | S | Client-side signals | ✅ Done |
| 🔴 P0 | Activation funnel events (register→active) | S | North-star metric | ⬜ Next |
| 🟠 P1 | Room engagement events (create/join/speak) | M | Core product insight | ⬜ |
| 🟠 P1 | Funnel + retention analytics endpoints | M | Answers the real questions | ⬜ |
| 🟠 P1 | Social & safety events | M | Community health | ⬜ |

*S = ~1 day, M = ~2–3 days.*

The **backbone is live** (table, tracker, batched writer, retention purge, IP hashing, client
endpoint). Remaining work is *instrumentation*: calling `_eventTracker.Track(...)` at the
integration points in §2/Step 4, then building the funnel/retention read endpoints (Step 5).
