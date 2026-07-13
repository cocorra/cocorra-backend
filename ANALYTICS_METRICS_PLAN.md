# Analytics Metrics Plan — Powering the Admin Dashboard from `UserEvents`

> **Goal:** Expand `AnalyticsRepository` with event-driven metrics for a new admin dashboard,
> built on the `UserEvents` table shipped in `USER_TRACKING_PLAN.md`. This plan gives concrete
> EF Core / LINQ implementations for four core metrics, then proposes seven more that matter
> most for a **voice-centric** social product.

---

## 0. Conventions & one architectural decision to make first

Everything here follows the patterns already in `AnalyticsRepository`:

- `AsNoTracking()` on every read; inject `AppDbContext`.
- Signatures take `(DateTime from, DateTime to, int topN = 10)`; results are DTOs in
  `Cocorra.DAL.DTOS.AnalyticsDto`.
- Distinct-user counts use the idiom already proven to translate on SQL Server in
  `GetFunnelAsync`: `g.Select(x => x.UserId).Distinct().Count()`.
- Wrap each new method behind the existing `AnalyticsService` cache + `SemaphoreSlim`
  stampede guard, and expose it via a `Router.AnalyticsRouting` route + `AnalyticsController`
  action (Admin/Coach only).

### ⚠️ The `PropertiesJson` problem — read this before implementing

`EventTracker` serializes event-specific fields (`roomId`, `category`, `source`, …) into the
free-form `PropertiesJson` **string** column. LINQ cannot translate `JsonSerializer.Deserialize`
into SQL, so there are only three honest ways to filter/group by a value *inside* that JSON:

| Approach | How | When to use |
|----------|-----|-------------|
| **A. Promote to a column (recommended)** | Add a nullable, indexed `Guid? RoomId` to `UserEvent`; have `EventTracker` (or the emit site) populate it for room-scoped events. | Any **hot** room-keyed metric (most metrics below). Fast, indexable, translatable. |
| **B. `JSON_VALUE` via raw SQL** | `_context.UserEvents.FromSqlRaw("… WHERE JSON_VALUE(PropertiesJson,'$.roomId') = {0}", id)` or a mapped computed column. | Ad-hoc / low-frequency queries where you don't want a schema change. |
| **C. Client-side deserialize** | `ToListAsync()` then `JsonSerializer.Deserialize` in memory. | Small result sets only — **never** over the full 180-day table. |

**Recommendation:** add a `RoomId` column now (cheap migration) — at least half the metrics
below key on room. The LINQ examples show **Approach A** as the primary form and note the
`JSON_VALUE` fallback where relevant. Where a metric needs no JSON at all (peak hours,
verification funnel), no schema change is required.

```csharp
// Suggested addition to Cocorra.DAL/Models/UserEvent.cs
public Guid? RoomId { get; set; }   // populated for room_* events; indexed for analytics
// AppDbContext: e.HasIndex(x => new { x.RoomId, x.EventType, x.OccurredAtUtc });
```

---

## 1. Core metrics

### 1.1 Most active room (highest join events)

Counts `room_joined` events per room in the window and ranks them. "Active" here = actual
attendance, not just interest — that's why we count `RoomJoined`, not `RoomJoinRequested`.

```csharp
public async Task<List<TopActiveRoomDto>> GetMostActiveRoomsAsync(
    DateTime from, DateTime to, int topN = 10)
{
    // Approach A — RoomId promoted to a column.
    return await _context.UserEvents
        .AsNoTracking()
        .Where(e => e.EventType == EventTypes.RoomJoined
                 && e.OccurredAtUtc >= from && e.OccurredAtUtc <= to
                 && e.RoomId != null)
        .GroupBy(e => e.RoomId!.Value)
        .Select(g => new TopActiveRoomDto
        {
            RoomId       = g.Key,
            JoinEvents   = g.Count(),                                   // total joins (incl. rejoins)
            UniqueJoiners = g.Select(x => x.UserId).Distinct().Count()  // distinct people
        })
        .OrderByDescending(r => r.JoinEvents)
        .Take(topN)
        .ToListAsync();
}
```

- Enrich `RoomTitle`/`Category` with a follow-up join to `Rooms` on the returned ids (keeps
  the heavy aggregation on the indexed event table).
- Report **both** `JoinEvents` and `UniqueJoiners` — a big gap between them flags a churny room
  people keep leaving and rejoining (unstable audio, bad host), which is itself a signal.
- *Approach B fallback:* `.GroupBy(e => EF.Functions... JSON_VALUE(e.PropertiesJson,"$.roomId"))`
  via raw SQL if you skip the column.

### 1.2 Peak active hours (time of day with most activity)

Buckets **all** events by UTC hour-of-day. Unlike the existing `GetParticipationStatsAsync`
(which materializes rows and groups client-side), `DateTime.Hour` **does** translate to
`DATEPART(hour, …)` on SQL Server — so this can run fully server-side and scale.

```csharp
public async Task<List<HourlyActivityDto>> GetPeakActiveHoursAsync(
    DateTime from, DateTime to)
{
    var rows = await _context.UserEvents
        .AsNoTracking()
        .Where(e => e.OccurredAtUtc >= from && e.OccurredAtUtc <= to)
        .GroupBy(e => e.OccurredAtUtc.Hour)          // → DATEPART(hour, OccurredAtUtc)
        .Select(g => new HourlyActivityDto
        {
            Hour        = g.Key,
            EventCount  = g.Count(),
            ActiveUsers = g.Select(x => x.UserId).Distinct().Count()
        })
        .ToListAsync();

    // Fill the 0–23 gaps so the dashboard chart has every hour.
    return Enumerable.Range(0, 24)
        .Select(h => rows.FirstOrDefault(r => r.Hour == h)
                     ?? new HourlyActivityDto { Hour = h })
        .OrderBy(r => r.Hour)
        .ToList();
}
```

- **Caveat:** hours are **UTC**. If your users are concentrated in one timezone, either offset
  before grouping or note the offset on the dashboard, or "peak hours" will be misleading.
- Optionally add a `DayOfWeek` dimension for a weekday-vs-weekend heatmap.

### 1.3 Voice verification drop-off rate (started vs completed)

Voice verification is the make-or-break activation gate for Cocorra. Drop-off =
`1 − (completed / started)`, counted by **distinct users** (a re-record shouldn't inflate the
denominator). This mirrors the `GetFunnelAsync` distinct-count idiom.

```csharp
public async Task<VoiceVerificationFunnelDto> GetVoiceVerificationDropOffAsync(
    DateTime from, DateTime to)
{
    var counts = await _context.UserEvents
        .AsNoTracking()
        .Where(e => (e.EventType == EventTypes.VoiceVerificationSubmitted
                  || e.EventType == EventTypes.ActivationCompleted)
                 && e.OccurredAtUtc >= from && e.OccurredAtUtc <= to
                 && e.UserId != null)
        .GroupBy(e => e.EventType)
        .Select(g => new { g.Key, Users = g.Select(x => x.UserId).Distinct().Count() })
        .ToDictionaryAsync(x => x.Key, x => x.Users);

    int started   = counts.GetValueOrDefault(EventTypes.VoiceVerificationSubmitted);
    int completed = counts.GetValueOrDefault(EventTypes.ActivationCompleted);

    return new VoiceVerificationFunnelDto
    {
        Started        = started,
        Completed      = completed,
        DropOffRate    = started > 0 ? Math.Round((1.0 - (double)completed / started) * 100, 2) : 0,
        CompletionRate = started > 0 ? Math.Round((double)completed / started * 100, 2) : 0
    };
}
```

- For finer diagnosis, add `VoiceVerificationResult` (the Active vs ReRecord verdict) as a
  middle step to separate **submission** drop-off from **re-record** friction.
- This is effectively a two-step specialization of the generic `GetFunnelAsync` — worth keeping
  as its own typed method since it's the north-star activation gate.

### 1.4 Active (speakers) vs Passive (listeners) participation rate

The core health metric of a voice product: of everyone who *joined* a room, what share ever
took the mic? Passive = joined but never `mic_activated`.

```csharp
public async Task<ParticipationModeDto> GetActiveVsPassiveRateAsync(
    DateTime from, DateTime to)
{
    // Distinct users who joined at least one room in the window.
    var joined = await _context.UserEvents.AsNoTracking()
        .Where(e => e.EventType == EventTypes.RoomJoined
                 && e.OccurredAtUtc >= from && e.OccurredAtUtc <= to && e.UserId != null)
        .Select(e => e.UserId).Distinct().ToListAsync();

    // Of those, who ever activated the mic in the window.
    var speakers = await _context.UserEvents.AsNoTracking()
        .Where(e => e.EventType == EventTypes.MicActivated
                 && e.OccurredAtUtc >= from && e.OccurredAtUtc <= to
                 && e.UserId != null && joined.Contains(e.UserId))
        .Select(e => e.UserId).Distinct().CountAsync();

    int total = joined.Count;
    return new ParticipationModeDto
    {
        TotalParticipants = total,
        ActiveSpeakers    = speakers,
        PassiveListeners  = total - speakers,
        ActiveRate        = total > 0 ? Math.Round((double)speakers / total * 100, 2) : 0
    };
}
```

- `SpeakingTimeLogged` (with `TotalSpokenSeconds > 0` in properties) is an alternative/stronger
  "active" definition if you want *actually spoke* rather than *unmuted*.
- A healthy voice room has a meaningful speaker share; a collapsing one trends toward
  all-listeners. Track this **over time**, not just as a point value.

---

## 2. Seven more high-value metrics (Data-Analyst hat)

Ranked roughly by impact for a voice-room social app. Each: **why it matters** + a brief LINQ
sketch. All assume the `AsNoTracking` + window conventions above.

### 2.1 Average room dwell time (session length) — *engagement depth*

**Why:** For a live-audio product, *time-in-room* is the truest engagement signal — richer than
"rooms joined." Pairing `room_joined` → `room_left` per user per room shows whether rooms hold
attention or people bounce in seconds (a content/quality problem). Trending dwell time down is
an early retention warning.

```csharp
// Pair each join with that user's next leave in the same room (client-side pairing after a
// tight, indexed pull). RoomId column assumed; order matters for the pairing.
var evts = await _context.UserEvents.AsNoTracking()
    .Where(e => (e.EventType == EventTypes.RoomJoined || e.EventType == EventTypes.RoomLeft)
             && e.OccurredAtUtc >= from && e.OccurredAtUtc <= to
             && e.UserId != null && e.RoomId != null)
    .OrderBy(e => e.OccurredAtUtc)
    .Select(e => new { e.UserId, e.RoomId, e.EventType, e.OccurredAtUtc })
    .ToListAsync();

var durations = evts
    .GroupBy(e => new { e.UserId, e.RoomId })
    .SelectMany(g =>
    {
        var list = g.ToList(); var spans = new List<double>();
        for (int i = 0; i < list.Count - 1; i++)
            if (list[i].EventType == EventTypes.RoomJoined &&
                list[i + 1].EventType == EventTypes.RoomLeft)
                spans.Add((list[i + 1].OccurredAtUtc - list[i].OccurredAtUtc).TotalMinutes);
        return spans;
    });

double avgDwellMinutes = durations.Any() ? Math.Round(durations.Average(), 2) : 0;
```

> Note the pairing is client-side; keep the window bounded. `SessionId` can further scope
> pairing to a single app session.

### 2.2 Time-to-first-mic (speak-up latency) — *activation inside the room*

**Why:** The hardest conversion in social audio is turning a listener into a speaker. How long
after joining does a user first unmute? A rising latency (or users who never reach it) means the
"raise hand / take stage" UX is too intimidating — the #1 growth lever for voice apps.

```csharp
// Per user: earliest mic_activated minus earliest room_joined, in the window.
var firstJoin = await _context.UserEvents.AsNoTracking()
    .Where(e => e.EventType == EventTypes.RoomJoined && e.UserId != null
             && e.OccurredAtUtc >= from && e.OccurredAtUtc <= to)
    .GroupBy(e => e.UserId).Select(g => new { UserId = g.Key, T = g.Min(x => x.OccurredAtUtc) })
    .ToListAsync();

var firstMic = await _context.UserEvents.AsNoTracking()
    .Where(e => e.EventType == EventTypes.MicActivated && e.UserId != null
             && e.OccurredAtUtc >= from && e.OccurredAtUtc <= to)
    .GroupBy(e => e.UserId).Select(g => new { UserId = g.Key, T = g.Min(x => x.OccurredAtUtc) })
    .ToListAsync();

var latencies = firstJoin.Join(firstMic, j => j.UserId, m => m.UserId,
        (j, m) => (m.T - j.T).TotalSeconds)
    .Where(s => s >= 0);
double medianSpeakUpSeconds = latencies.Any()
    ? latencies.OrderBy(x => x).ElementAt(latencies.Count() / 2) : 0;   // median > mean here
```

### 2.3 Room fill rate / empty-room rate — *host success & supply health*

**Why:** The classic live-audio failure mode is the **dead room** — someone creates a room and
nobody joins. If a high share of `room_created` rooms never reach `room_joined` (by anyone but
the host), hosts get discouraged and stop creating, and the supply side collapses. This directly
predicts creator retention.

```csharp
var created = await _context.UserEvents.AsNoTracking()
    .Where(e => e.EventType == EventTypes.RoomCreated
             && e.OccurredAtUtc >= from && e.OccurredAtUtc <= to && e.RoomId != null)
    .Select(e => e.RoomId!.Value).Distinct().ToListAsync();

var filled = await _context.UserEvents.AsNoTracking()
    .Where(e => e.EventType == EventTypes.RoomJoined && e.RoomId != null
             && created.Contains(e.RoomId.Value))
    .Select(e => e.RoomId!.Value).Distinct().CountAsync();

double emptyRoomRate = created.Count > 0
    ? Math.Round((1.0 - (double)filled / created.Count) * 100, 2) : 0;
```

### 2.4 Notification-driven return rate — *retention channel ROI*

**Why:** Push/in-app notifications are the primary re-engagement lever. Measuring how often a
`notification_opened` is followed (within, say, 30 min) by a `session_started` or `room_joined`
tells you which nudges actually bring people back vs. annoy them. Feeds directly into not
strategy and unsubscribe/churn risk.

```csharp
var opens = await _context.UserEvents.AsNoTracking()
    .Where(e => e.EventType == EventTypes.NotificationOpened && e.UserId != null
             && e.OccurredAtUtc >= from && e.OccurredAtUtc <= to)
    .Select(e => new { e.UserId, e.OccurredAtUtc }).ToListAsync();

var returns = await _context.UserEvents.AsNoTracking()
    .Where(e => (e.EventType == EventTypes.SessionStarted || e.EventType == EventTypes.RoomJoined)
             && e.UserId != null && e.OccurredAtUtc >= from && e.OccurredAtUtc <= to)
    .Select(e => new { e.UserId, e.OccurredAtUtc }).ToListAsync();

int converted = opens.Count(o => returns.Any(r =>
    r.UserId == o.UserId &&
    r.OccurredAtUtc > o.OccurredAtUtc &&
    r.OccurredAtUtc <= o.OccurredAtUtc.AddMinutes(30)));
double returnRate = opens.Count > 0 ? Math.Round((double)converted / opens.Count * 100, 2) : 0;
```

> Slice by a `notificationType` key in `PropertiesJson` (Approach B) to compare campaigns.

### 2.5 Friend-request acceptance rate & velocity — *social graph growth*

**Why:** A social product lives or dies by its graph. Acceptance rate (`friend_request_accepted`
÷ `friend_request_sent`) measures whether connections are meaningful or spammy; low acceptance
often signals unwanted requests (a safety/UX smell). Volume trend measures viral growth.

```csharp
var counts = await _context.UserEvents.AsNoTracking()
    .Where(e => (e.EventType == EventTypes.FriendRequestSent
              || e.EventType == EventTypes.FriendRequestAccepted)
             && e.OccurredAtUtc >= from && e.OccurredAtUtc <= to)
    .GroupBy(e => e.EventType)
    .Select(g => new { g.Key, Count = g.Count() })
    .ToDictionaryAsync(x => x.Key, x => x.Count);

int sent = counts.GetValueOrDefault(EventTypes.FriendRequestSent);
int accepted = counts.GetValueOrDefault(EventTypes.FriendRequestAccepted);
double acceptanceRate = sent > 0 ? Math.Round((double)accepted / sent * 100, 2) : 0;
```

### 2.6 Report concentration & repeat-offender rate — *safety*

**Why:** Voice is real-time and unrecorded, so proactive safety leans on `user_reported`
patterns. Concentration (how many reports target the top offenders, and how many *distinct*
reporters each has) separates a genuine bad actor — many independent reporters — from a single
user with a grudge. Repeat offenders drive good users away; catching them fast protects
retention. Needs `reportedUserId` from `PropertiesJson` (Approach A/B).

```csharp
var offenders = await _context.UserEvents.AsNoTracking()
    .Where(e => e.EventType == EventTypes.UserReported
             && e.OccurredAtUtc >= from && e.OccurredAtUtc <= to
             && e.RoomId != null /* or a TargetUserId column */)
    .GroupBy(e => e.RoomId)                       // group by reported-user id column
    .Select(g => new RepeatOffenderDto
    {
        TargetId          = g.Key!.Value,
        ReportCount       = g.Count(),
        DistinctReporters = g.Select(x => x.UserId).Distinct().Count()   // key credibility signal
    })
    .Where(x => x.ReportCount >= 3)
    .OrderByDescending(x => x.DistinctReporters)
    .Take(topN)
    .ToListAsync();
```

> Add a dedicated `TargetUserId` column (same rationale as `RoomId`) if safety metrics become
> first-class — grouping on JSON is otherwise the bottleneck here.

### 2.7 Silent-listener churn predictor — *retention early-warning*

**Why:** The strongest leading churn indicator in social audio is **passive lurking**: users who
attend rooms across multiple sessions but never speak, message, or add friends. They get little
value and leave quietly. Flagging them lets you trigger targeted "take the mic" nudges before
they're gone — far cheaper than win-back.

```csharp
// Users active on 2+ distinct days who joined rooms but produced zero "contribution" events.
var contributionTypes = new[] {
    EventTypes.MicActivated, EventTypes.MessageSent,
    EventTypes.RoomCreated,  EventTypes.FriendRequestSent };

var activity = await _context.UserEvents.AsNoTracking()
    .Where(e => e.UserId != null && e.OccurredAtUtc >= from && e.OccurredAtUtc <= to)
    .Select(e => new { e.UserId, e.EventType, Day = e.OccurredAtUtc.Date })
    .ToListAsync();

var atRisk = activity
    .GroupBy(e => e.UserId)
    .Where(g => g.Select(x => x.Day).Distinct().Count() >= 2               // recurring visitor
             && g.Any(x => x.EventType == EventTypes.RoomJoined)           // did attend
             && !g.Any(x => contributionTypes.Contains(x.EventType)))      // never contributed
    .Select(g => g.Key)
    .ToList();
```

> This is the analytical seed for a churn model; even the raw count trending up is an alarm.

---

## 3. Suggested build order

1. **Add the `RoomId` (and optionally `TargetUserId`) column** + migration — unblocks §1.1,
   §2.1, §2.3, §2.6 with indexed, translatable queries.
2. Ship §1.2 (peak hours) and §1.3 (verification drop-off) first — **zero schema change**,
   highest dashboard value.
3. Add §1.1 and §1.4 (room activity, active/passive) — core voice health.
4. Layer in §2 metrics as the dashboard matures; each is one repository method + DTO + cached
   service call + route, reusing the existing `AnalyticsService`/`AnalyticsController` plumbing.

*New DTOs referenced above (`TopActiveRoomDto`, `HourlyActivityDto`, `VoiceVerificationFunnelDto`,
`ParticipationModeDto`, `RepeatOffenderDto`, …) go in `Cocorra.DAL/DTOS/AnalyticsDto/`, matching
the existing DTO style.*
