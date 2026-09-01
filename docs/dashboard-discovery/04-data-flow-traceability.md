# 04 — Data Flow Traceability

> **Generated**: 2026-08-31 | **Purpose**: Trace the full data path for each important dashboard metric to identify where data can be lost, delayed, duplicated, or become incorrect.

---

## Flow 1: User Registration Count

```
USER ACTION:       User submits registration form
      ↓
APPLICATION LOGIC: AuthServices.RegisterAsync()
      ↓              → Creates ApplicationUser with CreatedAt = DateTime.UtcNow
      ↓              → Sends OTP email
      ↓              → EventTracker.Track("user_registered", userId)
      ↓
DATABASE WRITE:    UserManager.CreateAsync(user) → AspNetUsers table
      ↓            EventFlushService → batches → UserEvents table
      ↓
QUERY:             AdminService: UserManager.Users.GroupBy(Status).Count()
      ↓            AnalyticsRepo: Users.Where(CreatedAt >= from).ToList()
      ↓
API:               GET /Admin/Dashboard/Stats → DashboardStatsDto
      ↓            GET /Analytics/Users/Growth → UserGrowthDto
      ↓
DASHBOARD:         Admin panel displays count cards + growth chart
```

### Risk Points
| Point | Risk | Severity |
|-------|------|----------|
| Event queue full | `user_registered` event dropped (DropWrite policy), but user is still created. Dashboard stats from Users table are fine; funnel data from UserEvents may undercount. | MEDIUM |
| Hard delete | `DeleteAccountAsync` hard-deletes the user. Historical registration count permanently decreases. | **HIGH** |
| DateTime.UtcNow in model constructor | `CreatedAt` is set in the C# model constructor, not by the database. Clock skew between app instances is possible. | LOW |

---

## Flow 2: Room Joined

```
USER ACTION:       User taps "Join Room"
      ↓
APPLICATION LOGIC: RoomsController.Join() → RoomService.JoinRoomAsync()
      ↓              → Creates RoomParticipant with JoinedAt = DateTime.UtcNow
      ↓              → Event: room_join_requested (if manual mode)
      ↓
      ↓            Host approves → RoomService.ApproveUserAsync()
      ↓              → Event: room_join_approved
      ↓
      ↓            User connects via SignalR → RoomHub.JoinRoom()
      ↓              → Re-activates Left participants
      ↓              → Tracks _connections[ConnectionId] = (UserId, RoomId)
      ↓              → EventTracker.Track("room_joined", userId, {roomId})
      ↓
DATABASE WRITE:    RoomParticipant → saved via RoomRepository
      ↓            UserEvent → batched via EventFlushService
      ↓
QUERY:             AnalyticsRepo: UserEvents.Where(EventType == "room_joined")
      ↓            AnalyticsRepo: RoomParticipants.Where(JoinedAt in range)
      ↓
DASHBOARD:         Active Rooms widget, Participation Stats
```

### Risk Points
| Point | Risk | Severity |
|-------|------|----------|
| Dual data source | Room participation is tracked in BOTH `RoomParticipants` table AND `UserEvents`. If either source fails, metrics diverge. | **HIGH** |
| JoinedAt reset | When a user disconnects and reconnects, `RoomHub.JoinRoom` resets `JoinedAt = DateTime.UtcNow` for Left participants. The original join time is lost. | **HIGH** |
| room_joined per reconnect | Each SignalR reconnect fires a new `room_joined` event, inflating "join events" in Active Rooms metric. `UniqueJoiners` mitigates this. | MEDIUM |
| SignalR in-memory state | If server restarts, `_connections` dictionary is lost. OnDisconnectedAsync cleanup won't fire for existing connections. | MEDIUM |

---

## Flow 3: Speaking Time

```
USER ACTION:       Speaker unmutes mic
      ↓
APPLICATION LOGIC: RoomHub.ToggleMic(roomId, muteStatus=false)
      ↓              → participant.LastUnmutedAt = DateTime.UtcNow
      ↓              → EventTracker.Track("mic_activated", userId, {roomId})
      ↓
USER ACTION:       Speaker mutes mic
      ↓
APPLICATION LOGIC: RoomHub.ToggleMic(roomId, muteStatus=true)
      ↓              → spokenSeconds = (now - LastUnmutedAt).TotalSeconds
      ↓              → participant.TotalSpokenSeconds += spokenSeconds
      ↓              → participant.LastUnmutedAt = null
      ↓
DATABASE WRITE:    RoomParticipant updated via RoomRepository
      ↓
QUERY:             AnalyticsRepo: RoomParticipants.Sum(TotalSpokenSeconds)
      ↓
DASHBOARD:         Top Speakers, Avg Spoken Time, Total Spoken Hours
```

### Risk Points
| Point | Risk | Severity |
|-------|------|----------|
| Unclean disconnect while unmuted | If user disconnects while mic is on, `OnDisconnectedAsync` calls `LeaveRoomCleanupAsync`. Whether this finalizes spoken time depends on `LeaveRoomCleanupAsync` implementation. If it doesn't check `LastUnmutedAt`, speaking time is **lost**. | **HIGH** |
| Host disconnect auto-ends room | When host disconnects, `OnDisconnectedAsync` calls `EndRoomAsync` which should finalize all participants' spoken time. If it doesn't, all active speakers lose their current segment. | **HIGH** |
| No `speaking_time_logged` emit found | `EventTypes.SpeakingTimeLogged` is defined but never emitted in the codebase (grep found no usage in actual Track calls). The analytics rely solely on `RoomParticipant.TotalSpokenSeconds`. | MEDIUM |

---

## Flow 4: User Status Change (Admin Action)

```
USER ACTION:       Admin changes user status in dashboard
      ↓
APPLICATION LOGIC: AdminController.ChangeStatus()
      ↓              → AdminService.ChangeUserStatusAsync()
      ↓              → user.Status = newStatus
      ↓              → Side effects: lockout, voice deletion, token invalidation
      ↓              → EventTracker.Track("voice_verification_result", userId, {status})
      ↓              → If newStatus == Active && not already activated:
      ↓                  EventTracker.Track("activation_completed", userId)
      ↓
DATABASE WRITE:    UserManager.UpdateAsync(user)
      ↓
QUERY:             AdminService: UserManager.Users.GroupBy(Status)
      ↓
DASHBOARD:         Admin Stats cards
```

### Risk Points
| Point | Risk | Severity |
|-------|------|----------|
| No status change timestamp | `ApplicationUser` has no `UpdatedAt`. When a user transitions from Pending → Active, there is NO timestamp of when this happened. Admin review latency is impossible to measure from the database. | **HIGH** |
| Event replaces timestamp | The `voice_verification_result` event has `OccurredAtUtc` which captures when the status change happened. But this event is purged after 180 days. | **HIGH** |
| Multiple status changes | A user can go Pending → ReRecord → Pending → Active. Only the final state is in the database; the transition history is only in events (which expire). | MEDIUM |

---

## Flow 5: Session Tracking

```
USER ACTION:       User opens app / makes any API request
      ↓
APPLICATION LOGIC: SessionTrackingMiddleware
      ↓              → Checks for "CocorraSessionId" cookie
      ↓              → If missing: creates new Guid, sets cookie (7-day expiry)
      ↓              → Stores SessionId in HttpContext.Items
      ↓              → After pipeline: if authenticated & first time for this session:
      ↓                  EventTracker.Track("session_started", userId, {sessionId})
      ↓                  Cache key "session_logged:{sessionId}" set (1-day TTL)
      ↓
DATABASE WRITE:    UserEvent via EventFlushService
      ↓
QUERY:             Used by Retention Cohort (activeEvent = "session_started")
      ↓
DASHBOARD:         Retention metrics, Peak Hours
```

### Risk Points
| Point | Risk | Severity |
|-------|------|----------|
| Mobile cookie unreliability | Flutter mobile apps may not reliably persist cookies between sessions. Each app open could generate a new SessionId, inflating session counts. | **CRITICAL** |
| Cookie-based deduplication | The session dedup relies on `IMemoryCache` with 1-day TTL. If the server restarts, all sessions are re-counted. | **HIGH** |
| No session_ended | There is no `session_ended` event. Session duration cannot be calculated. | **HIGH** |
| Middleware executes after pipeline | `SessionTrackingMiddleware` calls `await _next(context)` before logging. This means if the request fails, the session is still logged. | LOW |

---

## Flow 6: Report Submission

```
USER ACTION:       User submits a report
      ↓
APPLICATION LOGIC: SupportController.SubmitReport()
      ↓              → SupportService.SubmitReportAsync()
      ↓              → Creates Report entity (Status = "Open")
      ↓              → EventTracker.Track("user_reported", userId, {...})
      ↓
DATABASE WRITE:    Report table
      ↓
QUERY:             AnalyticsRepo: Reports.Where(CreatedAt in range)
      ↓
DASHBOARD:         Report Insights (category, status breakdown, most reported)
```

### Risk Points
| Point | Risk | Severity |
|-------|------|----------|
| Report status is string | Status field is a string, not an enum. Typos or non-standard values will silently be excluded from status counts. | MEDIUM |
| ReportedUser SetNull | If the reported user deletes their account, `ReportedUserId` becomes null. "Most Reported Users" will lose data. | MEDIUM |
| No "resolved_at" timestamp | Only `CreatedAt` and `UpdatedAt`. Resolution time must be inferred from `UpdatedAt`, which changes on ANY update. | MEDIUM |

---

## Data Loss Risk Summary

| Flow | Critical Risk | Impact on Dashboard |
|------|--------------|---------------------|
| Registration | Hard delete removes users from count | Historical growth chart shows declining numbers |
| Room Joined | JoinedAt reset + duplicate events | Inflated join counts, lost original join time |
| Speaking Time | Unclean disconnect loses time | Top speakers may undercount |
| Status Change | No timestamp, events expire | Cannot measure admin review latency beyond 180 days |
| Session Tracking | Mobile cookie unreliability | Retention metrics are unreliable for mobile users |
| Report | String status, user deletion | Inconsistent status counts, lost reporter identity |
