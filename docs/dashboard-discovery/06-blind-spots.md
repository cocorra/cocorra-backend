# 06 — Blind Spots & Missing Data

> **Generated**: 2026-08-31 | **Purpose**: Identify data gaps that prevent informed product decisions

---

## Critical Blind Spots

### 1. No User Activity Tracking (DAU/MAU)

**What's Missing**: There is no reliable way to know how many users are active on any given day.

**Evidence**: 
- `session_started` depends on a cookie (`CocorraSessionId`) set via `SessionTrackingMiddleware`.
- Mobile apps (Flutter) typically don't persist cookies across launches like browsers do.
- Deduplication relies on in-memory `IMemoryCache` (lost on server restart).
- No `login` event is tracked when a user authenticates.

**Impact**: Cannot calculate DAU, MAU, WAU, or stickiness ratio (DAU/MAU). Cannot identify churning users.

**File**: `SessionTrackingMiddleware.cs:22-59`

---

### 2. No Session Duration

**What's Missing**: There is no `session_ended` event or mechanism to measure how long users spend in the app.

**Evidence**:
- `session_started` is the only session event.
- No heartbeat or periodic activity ping exists.
- No `app_backgrounded` or `app_closed` event from the Flutter client.

**Impact**: Cannot measure engagement depth. Cannot distinguish between users who open the app for 5 seconds vs. 2 hours.

---

### 3. No User Status Change History

**What's Missing**: When an admin changes a user's status, only the new status is recorded. The timestamp of the change is lost.

**Evidence**:
- `ApplicationUser` does not extend `BaseEntity` and has no `UpdatedAt` field.
- `voice_verification_result` event captures the change, but events are purged after 180 days.
- Multiple status transitions (Pending → ReRecord → Pending → Active) are not captured as a log.

**Impact**: 
- Cannot measure admin review time (how long between voice submission and approval).
- Cannot identify bottlenecks in the verification pipeline.
- After 180 days, the entire verification history is gone.

**File**: `AdminService.cs:77-250` — status change logic writes to user, not a history table.

---

### 4. No Room Duration (Actual)

**What's Missing**: No timestamp of when a room actually went live or actually ended.

**Evidence**:
- `Room.StartDate` is the *scheduled* start date, set at creation time.
- `Room.DurationHours` is the *configured* duration (default 2h), not actual.
- No `StartedAt` or `EndedAt` fields on the `Room` entity.
- `room_ended` event captures `durationHours = (DateTime.UtcNow - room.StartDate).TotalHours`, but this uses the scheduled date, which may differ from when the host clicked "Start."

**Impact**: Cannot measure actual room durations. Average room duration metrics are misleading.

**File**: `RoomService.cs:543` — calculates duration from scheduled StartDate, not actual start.

---

### 5. No User Stay Duration in Rooms

**What's Missing**: Cannot determine how long a user was in a room.

**Evidence**:
- `RoomParticipant.JoinedAt` is reset on rejoin (disconnect → reconnect).
- No `LeftAt` field on `RoomParticipant`.
- `room_left` event is tracked but doesn't include how long the user was in the room.

**Impact**: Cannot measure audience retention per room. Cannot identify drop-off patterns (users who leave early).

**File**: `RoomHub.cs:245-253` — `JoinedAt` is overwritten on re-activation.

---

### 6. No Room Group Message Persistence

**What's Missing**: Group messages in rooms are ephemeral — broadcast via SignalR but never saved to the database.

**Evidence**:
- `RoomHub.SendRoomGroupMessage` broadcasts to the group but does not call any persistence service.
- Only private messages (via `SendRoomPrivateMessage`) are persisted through `ChatService.SaveMessageAsync`.

**Impact**: Cannot measure in-room chat engagement. Cannot analyze room conversation patterns.

**File**: `RoomHub.cs:654-694` — no database write.

---

### 7. No Push Notification Delivery Tracking

**What's Missing**: FCM push notifications are sent but delivery status is not recorded.

**Evidence**:
- `PushNotificationService.SendPushNotificationAsync` sends via Firebase but the response is not persisted.
- `notification_opened` is a client-side event — depends on the Flutter app implementing the tracking call.
- No `notification_delivered` or `notification_failed` events.

**Impact**: Cannot measure notification effectiveness. Cannot calculate notification-to-action conversion rate.

---

### 8. No Feature Usage Tracking

**What's Missing**: Most feature interactions are not tracked.

**Evidence**:
- `feature_viewed` is defined but is a client-side event with no standardized feature list.
- No tracking for: room feed viewed, room details viewed, profile viewed, friends list opened, chat opened, settings accessed.

**Impact**: Cannot identify most/least used features. Cannot prioritize feature development based on usage data.

---

### 9. No Error/Failure Tracking

**What's Missing**: No event for failed operations that affect user experience.

**Evidence**:
- No `registration_failed`, `login_failed`, `room_join_failed`, `message_send_failed` events.
- Error handling exists but errors are only logged via `ILogger`, which writes to console/Docker logs with no persistence.

**Impact**: Cannot measure system reliability from the user's perspective.

---

### 10. No Topic Request Analytics

**What's Missing**: Topic request feature has models (`RoomTopicRequest`, `TopicVote`) but no API endpoints, services, or events.

**Evidence**:
- No controller for topic requests.
- No event tracking for topic submission, voting, or approval.
- The `Router.cs` file has no `TopicRouting` section.

**Impact**: Cannot measure community engagement through topic requests. Feature may be unused or only frontend-mocked.

---

## Moderate Blind Spots

### 11. Hard Delete Destroys Historical Data
- `AuthServices.DeleteAccountAsync` hard-deletes the user, removing them from all historical counts.
- `Report.ReportedUser` FK uses `SetNull`, so reports lose their reported user reference.

### 12. No Search/Discovery Analytics
- No tracking of how users find rooms (search, feed scroll, category filter, push notification, deep link).
- Cannot measure room discovery funnel.

### 13. No Admin Action Audit Trail
- Admin status changes are tracked via `voice_verification_result` event.
- But: which admin made the change is not recorded (no `adminId` in the event properties).
- Bulk operations track per-user but the bulk nature is not recorded.

### 14. No Invitation/Referral Tracking
- No mechanism to track how users discover Cocorra (organic, referral, marketing campaign).
- `ApplicationUser` has no `source` or `referralCode` field.

### 15. No Timezone Awareness
- All analytics are UTC-only.
- Peak hours chart shows UTC hours, not local user hours.
- The user base appears to be Arabic-speaking (MENA region), all likely in UTC+2/+3.

---

## Blind Spot Impact Matrix

| Blind Spot | Product Question It Blocks |
|---|---|
| No DAU/MAU | "Is our platform growing in engagement, or just registrations?" |
| No session duration | "How engaged are our active users?" |
| No status history | "How fast is our admin review process?" |
| No actual room duration | "How long are rooms really lasting?" |
| No stay duration | "Do users stay for the whole room or leave early?" |
| No group messages | "Is in-room chat engagement driving room value?" |
| No push delivery | "Are our notifications actually reaching users?" |
| No feature usage | "Which features should we invest in?" |
| No error tracking | "How reliable is the user experience?" |
| No topic requests | "Is the topic suggestion feature working?" |
| Hard deletes | "Are we losing users or just can't see them?" |
| No discovery tracking | "How do users find rooms to join?" |
| No admin audit | "Which admin approved this user?" |
| No referral tracking | "Where are our users coming from?" |
| No timezone | "When should we schedule featured rooms?" |
