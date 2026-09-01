# 07 — Metric Calculation Verification

> **Generated**: 2026-08-31 | **Purpose**: Provide exact SQL-equivalent queries for every dashboard metric so an external engineer can independently verify the calculations

---

## Metric 1: Dashboard Stats

### API
`GET /Api/V1/Admin/Dashboard/Stats`

### Code Path
`AdminController.GetDashboardStats()` → `AdminService.GetDashboardStatsAsync()`

### SQL Equivalent
```sql
SELECT 
    Status,
    COUNT(*) AS Count
FROM AspNetUsers
GROUP BY Status;
```
Dashboard maps: `TotalUsers = SUM(Count)`, then individual status counts.

### Verification Notes
- No date filter. Always all-time.
- No `WHERE` clause — includes every user in the database.
- Deleted users are NOT included (hard delete removes the row).
- `Status` is stored as integer (0=Pending, 1=Active, 2=Rejected, 3=Banned, 4=ReRecord).

### Verdict: ✅ VERIFIED — Simple aggregation, no logic errors possible

---

## Metric 2: User Growth

### API
`GET /Api/V1/Analytics/Users/Growth?granularity=monthly&from=2026-01-01&to=2026-08-31`

### Code Path
`AnalyticsController.GetUserGrowth()` → `AnalyticsService.GetUserGrowthAsync()` → `AnalyticsRepository.GetUserGrowthAsync()`

### SQL Equivalent
```sql
-- Step 1: Fetch all users in date range
SELECT CreatedAt, Status, MBTI, Age
FROM AspNetUsers
WHERE CreatedAt >= @from AND CreatedAt <= @to;

-- Step 2: Client-side grouping (monthly)
-- Grouped by YEAR(CreatedAt), MONTH(CreatedAt)
-- Per bucket: COUNT where Status = 1 (Active), etc.

-- Step 3: Status breakdown
SELECT Status, COUNT(*) FROM (above result set) GROUP BY Status;

-- Step 4: MBTI distribution
SELECT MBTI, COUNT(*) FROM (above) WHERE MBTI IS NOT NULL GROUP BY MBTI ORDER BY COUNT(*) DESC;

-- Step 5: Average age
SELECT AVG(CAST(Age AS FLOAT)) FROM (above);
```

### Verification Notes
- **CRITICAL BUG**: Status is the user's *current* status, not the status at the time they were in that date bucket. A user registered in January who was banned in June shows as "Banned" in January's data.
- All users are materialized to memory (`.ToList()`). No server-side grouping.
- `topN` parameter exists but is passed through without being used in this method.

### Verdict: ⚠️ MISLEADING — Status backdating makes historical breakdown incorrect

---

## Metric 3: Room Analytics

### API
`GET /Api/V1/Analytics/Rooms?from=2026-01-01&to=2026-08-31&limit=10`

### Code Path
`AnalyticsRepository.GetRoomAnalyticsAsync()`

### SQL Equivalent
```sql
SELECT 
    r.Id, r.RoomTitle, r.Category, r.Status, r.IsPrivate, r.DurationHours,
    (SELECT COUNT(*) FROM RoomParticipants rp WHERE rp.RoomId = r.Id) AS ParticipantCount
FROM Rooms r
WHERE r.StartDate >= @from AND r.StartDate <= @to;
```
Client-side: GroupBy Category, OrderBy ParticipantCount DESC LIMIT @topN.

### Verification Notes
- `ParticipantCount` includes ALL participants (Active, Left, Kicked, Rejected, PendingApproval).
- `AvgDurationHours` uses configured duration, not actual room runtime.
- `AvgParticipantsPerRoom` includes empty rooms (0 participants).
- Status counts (Scheduled/Live/Ended) reflect current status, which is correct.

### Verdict: ⚠️ OVERSTATED — Participant counts are inflated by including non-active statuses

---

## Metric 4: Participation Stats

### API
`GET /Api/V1/Analytics/Participation?from=2026-01-01&to=2026-08-31`

### SQL Equivalent
```sql
SELECT 
    rp.UserId,
    rp.TotalSpokenSeconds,
    rp.IsHandRaised,
    rp.JoinedAt,
    u.FirstName,
    u.LastName
FROM RoomParticipants rp
LEFT JOIN AspNetUsers u ON rp.UserId = u.Id
WHERE rp.JoinedAt >= @from AND rp.JoinedAt <= @to;

-- Client-side: 
-- TopSpeakers: GROUP BY UserId, SUM(TotalSpokenSeconds), ORDER BY DESC, TAKE topN
-- PeakHours: GROUP BY DATEPART(HOUR, JoinedAt), COUNT
-- UsersWhoSpoke: COUNT WHERE TotalSpokenSeconds > 0
-- UsersWhoRaisedHand: COUNT WHERE IsHandRaised = 1
```

### Verification Notes
- `IsHandRaised` is a snapshot boolean. Once the user lowers their hand, `IsHandRaised = false`. The count `UsersWhoRaisedHand` only captures currently-raised hands at the moment of the query. This is essentially meaningless for historical analysis.
- `JoinedAt` is reset on reconnect, so the same user may appear in multiple time buckets.
- Peak hours are based on join time, not activity time.

### Verdict: ⚠️ PARTIALLY CORRECT — Spoken time is reliable; hand-raise count is unreliable

---

## Metric 5: Funnel

### API
`GET /Api/V1/Analytics/Funnel?steps=user_registered,email_confirmed,voice_verification_submitted,activation_completed,room_joined&from=...&to=...`

### SQL Equivalent
```sql
SELECT 
    EventType,
    COUNT(DISTINCT UserId) AS UniqueUsers
FROM UserEvents
WHERE EventType IN ('user_registered', 'email_confirmed', 'voice_verification_submitted', 'activation_completed', 'room_joined')
  AND OccurredAtUtc >= @from AND OccurredAtUtc <= @to
  AND UserId IS NOT NULL
GROUP BY EventType;
```

### Verification Notes
- This is a **parallel count**, not a **sequential funnel**. Each step counts independently.
- A user who has `room_joined` but NOT `email_confirmed` (data anomaly) would still appear in the `room_joined` count. This can cause funnel counts to increase at a later step (non-monotonic funnel).
- 180-day event cleanup means historical funnels are truncated.

### Verdict: ⚠️ NOT A TRUE FUNNEL — Independent counts per step, not sequential progression

---

## Metric 6: Retention Cohort

### API
`GET /Api/V1/Analytics/Retention?cohortEvent=user_registered&activeEvent=session_started&cohortStart=...&cohortEnd=...`

### SQL Equivalent
```sql
-- Step 1: Find cohort
SELECT UserId, MIN(OccurredAtUtc) AS CohortDate
FROM UserEvents
WHERE EventType = @cohortEvent
  AND OccurredAtUtc BETWEEN @cohortStart AND @cohortEnd
  AND UserId IS NOT NULL
GROUP BY UserId;

-- Step 2: Find ALL activity for cohort users (no time limit!)
SELECT UserId, OccurredAtUtc
FROM UserEvents
WHERE EventType = @activeEvent
  AND UserId IN (SELECT UserId FROM above);

-- Step 3: For each retention day (1, 7, 30):
-- Count DISTINCT users where DATEDIFF(DAY, CohortDate, OccurredAtUtc) = @day
-- Retention% = COUNT / CohortSize * 100
```

### Verification Notes
- **CRITICAL**: Uses `== day` (exact day match), not `>= day` or range. This means a user active on Day 2 but not Day 1 is NOT counted for D1 retention. Standard retention tools count "active on Day N or later" or "within a window."
- Step 2 fetches ALL activity events for cohort users with NO time limit, loading potentially massive data.
- If events are cleaned up (>180 days), both cohort and activity data may be missing.
- `session_started` depends on cookie reliability (see Blind Spots doc).

### Verdict: ❌ INCORRECT — Exact-day matching produces artificially low retention numbers

---

## Metric 7: Voice Verification Drop-Off

### API
`GET /Api/V1/Analytics/VoiceVerification/DropOff?from=...&to=...`

### SQL Equivalent
```sql
SELECT 
    EventType,
    COUNT(DISTINCT UserId) AS UniqueUsers
FROM UserEvents
WHERE EventType IN ('voice_verification_submitted', 'activation_completed')
  AND OccurredAtUtc BETWEEN @from AND @to
  AND UserId IS NOT NULL
GROUP BY EventType;

-- DropOffRate = (1 - Completed/Started) * 100
-- CompletionRate = Completed/Started * 100
```

### Verification Notes
- `activation_completed` is deduplicated at emit time (AdminService checks `AnyAsync` before tracking). This is correct.
- `voice_verification_submitted` fires on both initial registration AND re-record. A user who re-records counts once per submission, but DISTINCT makes this a non-issue.
- The funnel compares two independent event types — a user could have `activation_completed` without `voice_verification_submitted` if the latter event was lost.

### Verdict: ✅ LIKELY CORRECT — Simple two-step comparison

---

## Metric 8: Active vs Passive

### API
`GET /Api/V1/Analytics/Participation/ActiveVsPassive?from=...&to=...`

### SQL Equivalent
```sql
-- Step 1: All users who joined a room
SELECT DISTINCT UserId 
FROM UserEvents
WHERE EventType = 'room_joined'
  AND OccurredAtUtc BETWEEN @from AND @to
  AND UserId IS NOT NULL;

-- Step 2: Of those, who activated mic
SELECT COUNT(DISTINCT UserId)
FROM UserEvents
WHERE EventType = 'mic_activated'
  AND OccurredAtUtc BETWEEN @from AND @to
  AND UserId IS NOT NULL
  AND UserId IN (step 1 results);

-- Passive = Step1.Count - Step2.Count
-- ActiveRate = Step2 / Step1 * 100
```

### Verification Notes
- Step 1 results are materialized as a `List<Guid?>` and used in a `.Contains()` LINQ query for Step 2. This generates a SQL `WHERE UserId IN (...)` clause. For thousands of users, this could hit SQL Server's parameter limit or cause very slow queries.
- The logic is correct: speakers are a subset of joiners, passive = joiners - speakers.

### Verdict: ✅ CORRECT — May have performance issues at scale but logic is sound

---

## Verification Summary

| Metric | Verdict | Issue |
|--------|:-------:|-------|
| Dashboard Stats | ✅ VERIFIED | None |
| User Growth | ⚠️ MISLEADING | Status backdating |
| Room Analytics | ⚠️ OVERSTATED | All participant statuses counted |
| Participation Stats | ⚠️ PARTIAL | Hand-raise is snapshot |
| Funnel | ⚠️ NOT TRUE FUNNEL | Independent counts |
| Retention Cohort | ❌ INCORRECT | Exact-day matching |
| Voice Drop-Off | ✅ LIKELY CORRECT | Minor risks |
| Active/Passive | ✅ CORRECT | Scale concerns |
| Most Active Rooms | ✅ VERIFIED | — |
| Peak Hours | ✅ CORRECT | UTC-only |
