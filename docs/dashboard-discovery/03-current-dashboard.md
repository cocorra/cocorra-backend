# 03 — Current Dashboard Audit

> **Generated**: 2026-08-31 | **Source**: `AdminController.cs`, `AnalyticsController.cs`, `AnalyticsService.cs`, `AnalyticsRepository.cs`, `AdminService.cs`

---

## Dashboard Endpoint Map

Cocorra has **two** dashboard data sources:

1. **Admin Stats** — `GET /Api/V1/Admin/Dashboard/Stats` → `AdminService.GetDashboardStatsAsync()`
2. **Analytics API** — 11 endpoints under `/Api/V1/Analytics/` → `AnalyticsService` + `AnalyticsRepository`

---

## Metric 1: Dashboard Stats (Admin)

### What the UI says
`TotalUsers`, `ActiveUsers`, `PendingUsers`, `BannedUsers`, `RejectedUsers`, `ReRecordUsers`

### Current Value Source
`AdminService.GetDashboardStatsAsync()` → `UserManager.Users.GroupBy(u => u.Status)` → `DashboardStatsDto`

File: `AdminService.cs:383-401`

### Calculation
```
ApplicationUser table
  → GroupBy(Status)
  → Count per status
  → TotalUsers = Sum of all counts
  → ActiveUsers = Count where Status == Active
  → PendingUsers = Count where Status == Pending
  → BannedUsers = Count where Status == Banned
  → RejectedUsers = Count where Status == Rejected
  → ReRecordUsers = Count where Status == ReRecord
```

### Business Meaning
Point-in-time snapshot of user status distribution across the entire platform. **Not** time-windowed.

### Reliability Assessment
**VERIFIED** — Direct count from database, no filtering that could cause errors.

### Problems
- **No time dimension**: Always returns all-time counts. Cannot see how these numbers changed over time.
- **No deleted user handling**: `DeleteAccount` hard-deletes the user. Previously counted users disappear from totals, making historical comparison impossible.

### Decision Safety
**USE WITH CAUTION** — Numbers are accurate point-in-time but cannot be compared historically due to hard deletes.

---

## Metric 2: Platform Summary

### What the UI says
Combined snapshot: Users + Rooms + Participation + Reports.

### Current Value Source
`GET /Api/V1/Analytics/Summary?from=&to=` → `AnalyticsService.GetPlatformSummaryAsync()`

### Calculation
```
Parallel execution of:
  1. GetUserGrowthAsync("monthly", from, to)
  2. GetRoomAnalyticsAsync(from, to)
  3. GetParticipationStatsAsync(from, to)
  4. GetReportInsightsAsync(from, to)
→ Bundled into PlatformSummaryDto + GeneratedAt timestamp
→ Cached 10 min with SemaphoreSlim stampede protection
```

### Reliability Assessment
**LIKELY CORRECT** — Aggregation of the four sub-queries (see individual assessments below).

### Decision Safety
**USE WITH CAUTION** — Depends on individual metric quality.

---

## Metric 3: User Growth

### What the UI says
Registration trends over time, status breakdown, MBTI distribution, average age.

### Current Value Source
`GET /Api/V1/Analytics/Users/Growth?granularity=monthly&from=&to=&limit=10`

### Calculation
```
ApplicationUser
  → WHERE CreatedAt >= from AND CreatedAt <= to
  → SELECT CreatedAt, Status, MBTI, Age
  → ToList() (materializes all users in window to memory)
  → Client-side GroupBy on date (monthly or daily buckets)
  → Per bucket: Count(Status == Active), Count(Status == Pending), etc.
  → MBTI: GroupBy(MBTI), OrderByDescending, ToDictionary
  → AvgAge = Average(Age)
```

File: `AnalyticsRepository.cs:21-93`

### Business Meaning
Shows how many users registered in each time period and their current status distribution.

### Reliability Assessment
**MISLEADING**

### Problems
1. **Status is current, not historical**: Groups users by their *current* status at the time of query, not the status they had when they registered. A user who registered in January and was banned in June will show as "Banned" in the January bucket. This makes historical analysis incorrect.
2. **Hard deletes distort history**: Deleted users disappear entirely from the counts.
3. **Memory pressure**: All users in the date window are materialized to memory. For large user bases this could OOM.
4. **MBTI distribution is window-scoped**: Only shows MBTI for users who registered in the window, not all active users.

### Decision Safety
**NOT SAFE FOR DECISIONS** — The status backdating problem makes growth trends unreliable.

---

## Metric 4: Room Analytics

### What the UI says
Total rooms, by status, category breakdown, public/private ratio, top rooms by participants, avg participants/duration.

### Current Value Source
`GET /Api/V1/Analytics/Rooms?from=&to=&limit=10`

### Calculation
```
Rooms
  → WHERE StartDate >= from AND StartDate <= to
  → SELECT Id, Title, Category, Status, IsPrivate, DurationHours, Participants.Count()
  → ToList() (materialized)
  → Client-side aggregation: GroupBy(Category), OrderBy(ParticipantCount)
  → AvgParticipantsPerRoom, AvgDurationHours (configured, not actual)
```

File: `AnalyticsRepository.cs:98-164`

### Reliability Assessment
**LIKELY CORRECT** with caveats.

### Problems
1. **DurationHours is configured, not actual**: The average duration uses the configured room duration (default 2h), not the actual time the room was live.
2. **ParticipantCount includes all statuses**: Counts include `Left`, `Kicked`, `Rejected` participants — not just active ones. This inflates "top rooms."
3. **No actual attendance**: `Participants.Count` is total ever-joined, not concurrent peak.

### Decision Safety
**USE WITH CAUTION** — Category breakdown and counts are reliable; duration and participant metrics are approximate.

---

## Metric 5: Participation Stats

### What the UI says
Total participations, spoken time, top speakers, peak hours, users who spoke, users who raised hand.

### Current Value Source
`GET /Api/V1/Analytics/Participation?from=&to=&limit=10`

### Calculation
```
RoomParticipants
  → WHERE JoinedAt >= from AND JoinedAt <= to
  → SELECT UserId, TotalSpokenSeconds, IsHandRaised, JoinedAt, User.FirstName, User.LastName
  → ToList()
  → TopSpeakers: GroupBy(UserId), Sum(TotalSpokenSeconds), OrderByDescending
  → PeakHours: GroupBy(JoinedAt.Hour), Count
  → UsersWhoSpoke: Count(TotalSpokenSeconds > 0)
  → UsersWhoRaisedHand: Count(IsHandRaised)
```

File: `AnalyticsRepository.cs:166-231`

### Reliability Assessment
**LIKELY CORRECT** with caveats.

### Problems
1. **IsHandRaised is a current boolean, not historical**: Only captures whether the hand is *currently* raised, not whether it was *ever* raised. After lowering hand, the user won't be counted.
2. **TotalSpokenSeconds may be incomplete**: If a user disconnects while unmuted without clean muting, `LastUnmutedAt` is not finalized in `RoomHub.OnDisconnectedAsync` (finalization only happens in `LeaveRoomCleanupAsync`).
3. **Peak hours by join time, not activity time**: Shows when users joined, not when they were most active.
4. **JoinedAt is reset on rejoin**: Users who disconnect and reconnect get a new `JoinedAt`, so the same user could appear in multiple time windows.

### Decision Safety
**USE WITH CAUTION** — Speaking time is the most reliable metric here. Hand-raise and peak hours are unreliable.

---

## Metric 6: Report Insights

### What the UI says
Total reports, status breakdown (Open/Resolved/InProgress), category breakdown, most reported users.

### Current Value Source
`GET /Api/V1/Analytics/Reports?from=&to=&limit=10`

### Calculation
```
Reports
  → WHERE CreatedAt >= from AND CreatedAt <= to
  → SELECT Category, Status, ReportedUserId, ReportedUser names/email
  → ToList()
  → Status comparison: string equality (case-insensitive)
  → MostReported: GroupBy(ReportedUserId), Count, OrderByDescending
```

File: `AnalyticsRepository.cs:233-298`

### Reliability Assessment
**VERIFIED**

### Problems
1. **Status is a free-form string**: Comparison uses `StringComparison.OrdinalIgnoreCase` but any non-standard status value won't be counted. The code only recognizes "Open", "Resolved", and "InProgress".
2. **Report.ReportedUser SetNull on delete**: If a reported user is deleted, their reports lose the association. `MostReported` won't include them.

### Decision Safety
**SAFE FOR DECISIONS** — Report counts and categories are reliable.

---

## Metric 7: Funnel Analysis

### What the UI says
User counts per funnel step (default: registered → email_confirmed → activation_completed → room_joined → mic_activated).

### Current Value Source
`GET /Api/V1/Analytics/Funnel?steps=user_registered,email_confirmed,...&from=&to=`

### Calculation
```
UserEvents
  → WHERE EventType IN (steps) AND OccurredAtUtc >= from AND <= to AND UserId != null
  → GroupBy(EventType)
  → For each group: Count(DISTINCT UserId)
  → Return ordered by input step sequence
```

File: `AnalyticsRepository.cs:300-322`

### Reliability Assessment
**LIKELY CORRECT** but depends on event emission reliability.

### Problems
1. **Funnel is not sequential**: Counts distinct users per step independently, not users who completed step N *after* step N-1. A user could have `mic_activated` without `room_joined` if events are emitted from different code paths.
2. **180-day event cleanup**: `EventCleanupService` purges events older than 180 days. Historical funnel analysis beyond 6 months is impossible.

### Decision Safety
**USE WITH CAUTION** — Good for relative comparison but not a true sequential funnel.

---

## Metric 8: Retention Cohort

### What the UI says
D1, D7, D30 retention rates for a user cohort.

### Current Value Source
`GET /Api/V1/Analytics/Retention?cohortEvent=user_registered&activeEvent=session_started&from=&to=`

### Calculation
```
1. Find cohort: Users who did cohortEvent in [from, to], grouped by user. CohortDate = min(OccurredAtUtc).
2. Find activity: All activeEvent events for cohort users (no time limit).
3. For each retention day (1, 7, 30): count users whose activity event is exactly N days after their cohort date.
4. Retention = count / cohortSize * 100
```

File: `AnalyticsRepository.cs:324-392`

### Reliability Assessment
**UNCLEAR**

### Problems
1. **Exact day matching**: Retention counts users active on *exactly* day N, not "within day N". Most retention tools use "active on day N or later" or "active within window N±1". This will significantly undercount retention.
2. **session_started depends on cookies**: The default `activeEvent` is `session_started`, which uses a session cookie. Mobile apps may not reliably send cookies, making this metric unreliable for mobile-first platforms.
3. **180-day cleanup**: After 6 months, cohort events are deleted. D30 retention for old cohorts is impossible.

### Decision Safety
**NOT SAFE FOR DECISIONS** — Exact-day matching produces artificially low retention numbers.

---

## Metric 9: Most Active Rooms

### What the UI says
Rooms ranked by join activity (room_joined events), with unique joiners.

### Current Value Source
`GET /Api/V1/Analytics/Rooms/Active?from=&to=&limit=10`

### Calculation
```
UserEvents
  → WHERE EventType == "room_joined" AND OccurredAtUtc in range AND RoomId != null
  → GroupBy(RoomId)
  → JoinEvents = Count, UniqueJoiners = Count(DISTINCT UserId)
  → OrderByDescending(JoinEvents), Take(topN)
  → Enrich with Room title and category
```

File: `AnalyticsRepository.cs:399-444`

### Reliability Assessment
**VERIFIED** — Uses event data with promoted RoomId column, properly indexed.

### Decision Safety
**SAFE FOR DECISIONS**

---

## Metric 10: Peak Active Hours

### What the UI says
Event activity by UTC hour (0-23), with active user counts.

### Current Value Source
`GET /Api/V1/Analytics/PeakHours?from=&to=`

### Calculation
```
UserEvents
  → WHERE OccurredAtUtc in range
  → GroupBy(OccurredAtUtc.Hour)
  → EventCount = Count, ActiveUsers = Count(DISTINCT UserId)
  → Fill all 24 hours (0-padded)
```

File: `AnalyticsRepository.cs:447-469`

### Reliability Assessment
**LIKELY CORRECT** — but UTC-only. If users are in a single timezone (e.g., MENA region), the dashboard consumer must convert.

### Decision Safety
**SAFE FOR DECISIONS** — UTC hours are consistently measured.

---

## Metric 11: Voice Verification Drop-Off

### What the UI says
Started vs completed voice verification, with drop-off and completion rates.

### Current Value Source
`GET /Api/V1/Analytics/VoiceVerification/DropOff?from=&to=`

### Calculation
```
UserEvents
  → WHERE EventType IN ("voice_verification_submitted", "activation_completed") AND in range
  → GroupBy(EventType)
  → Count(DISTINCT UserId) per type
  → DropOffRate = (1 - Completed/Started) * 100
  → CompletionRate = Completed/Started * 100
```

File: `AnalyticsRepository.cs:472-498`

### Reliability Assessment
**LIKELY CORRECT** — depends on `voice_verification_submitted` being reliably emitted.

### Decision Safety
**USE WITH CAUTION** — Accurate if both events are consistently tracked.

---

## Metric 12: Active vs Passive Participation

### What the UI says
Speakers (mic_activated) vs listeners (joined but never spoke) ratio.

### Current Value Source
`GET /Api/V1/Analytics/Participation/ActiveVsPassive?from=&to=`

### Calculation
```
1. Joined = DISTINCT UserId from room_joined events in range
2. Speakers = DISTINCT UserId from mic_activated events in range, filtered to joined set
3. PassiveListeners = Joined - Speakers
4. ActiveRate = Speakers / Joined * 100
```

File: `AnalyticsRepository.cs:501-540`

### Reliability Assessment
**LIKELY CORRECT**

### Problems
1. **`joined` list loaded to memory**: All distinct user IDs are materialized as a list, then used in a `.Contains()` LINQ query. Could be slow with large user sets.
2. **mic_activated is per-unmute**: A user who unmutes 10 times gets 10 events but is still counted once (distinct). This is correct.

### Decision Safety
**SAFE FOR DECISIONS**

---

## Dashboard Metric Summary

| # | Metric | Reliability | Decision Safety | Critical Issue |
|---|--------|:-----------:|:---------------:|----------------|
| 1 | Admin Stats | VERIFIED | USE WITH CAUTION | No time dimension, hard deletes |
| 2 | Platform Summary | LIKELY CORRECT | USE WITH CAUTION | Aggregation of sub-metrics |
| 3 | User Growth | MISLEADING | NOT SAFE | Status backdating problem |
| 4 | Room Analytics | LIKELY CORRECT | USE WITH CAUTION | Duration is configured, not actual |
| 5 | Participation | LIKELY CORRECT | USE WITH CAUTION | Hand-raise is snapshot, not historical |
| 6 | Report Insights | VERIFIED | SAFE | String status comparison |
| 7 | Funnel | LIKELY CORRECT | USE WITH CAUTION | Not sequential, 180-day cleanup |
| 8 | Retention | UNCLEAR | NOT SAFE | Exact-day matching, cookie dependency |
| 9 | Active Rooms | VERIFIED | SAFE | — |
| 10 | Peak Hours | LIKELY CORRECT | SAFE | UTC-only |
| 11 | Voice Drop-Off | LIKELY CORRECT | USE WITH CAUTION | Depends on event emission |
| 12 | Active/Passive | LIKELY CORRECT | SAFE | Memory concern at scale |
