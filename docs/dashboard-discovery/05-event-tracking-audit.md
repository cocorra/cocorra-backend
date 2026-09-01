# 05 — Event Tracking Audit

> **Generated**: 2026-08-31 | **Source**: `EventTypes.cs`, all `_eventTracker.Track(...)` call sites, `EventsController.cs`

---

## Event Tracking Architecture

### Components
1. **`EventTracker`** — Singleton, writes to `Channel<UserEvent>` (bounded 10K, `BoundedChannelFullMode.DropWrite`)
2. **`EventFlushService`** — BackgroundService, batches up to 100 events, bulk-inserts to `UserEvents` table
3. **`EventCleanupService`** — BackgroundService, purges events older than 180 days every 24 hours
4. **`SessionTrackingMiddleware`** — Emits `session_started` on first authenticated request per session

### Pipeline
```
Code call site → EventTracker.Track() → Channel<UserEvent> → EventFlushService → AppDbContext.UserEvents → SQL Server
                                              ↓ (if queue full)
                                         Event DROPPED (logged as warning)
```

### Retention Policy
- Events older than **180 days** are permanently deleted by `EventCleanupService`
- No archive or export before deletion
- No configurable retention period (hardcoded in `EventCleanupService.cs:33`)

---

## Complete Event Inventory

### Server-Side Events (Authoritative)

| Event Type | Emitted From | UserId | Properties | Indexed RoomId? |
|------------|-------------|:------:|------------|:---:|
| `user_registered` | `AuthServices.RegisterAsync:129` | ✅ | — | No |
| `voice_verification_submitted` | `AuthServices.RegisterAsync:130`, `AuthServices.ReRecordVoiceAsync:504` | ✅ | — | No |
| `email_confirmed` | `OTPService.ConfirmEmailAsync:68` | ✅ | — | No |
| `mbti_submitted` | `AuthServices.SubmitMbtiAsync:252` | ✅ | `{mbti}` | No |
| `voice_verification_result` | `AdminService.ChangeUserStatusAsync:137` | ✅ | `{status}` | No |
| `activation_completed` | `AdminService.ChangeUserStatusAsync:144` | ✅ | — | No |
| `account_deleted` | `AuthServices.DeleteAccountAsync:565` | ✅ | `{reason}` | No |
| `room_created` | `RoomService.CreateRoomAsync:130` | ✅ (hostId) | `{roomId, category, isPrivate}` | ✅ |
| `room_join_requested` | `RoomService.JoinRoomAsync:195,255` | ✅ | `{roomId}` | ✅ |
| `room_join_approved` | `RoomService.ApproveUserAsync:311` | ✅ (hostId) | `{roomId, approvedUserId}` | ✅ |
| `room_joined` | `RoomHub.JoinRoom:270` | ✅ | `{roomId}` | ✅ |
| `room_left` | `RoomHub.OnDisconnectedAsync:79`, `RoomHub.LeaveRoom:370` | ✅ | `{roomId}` | ✅ |
| `mic_activated` | `RoomHub.ToggleMic:521` | ✅ | `{roomId}` | ✅ |
| `speaking_time_logged` | `RoomService.EndRoomAsync:549` | ✅ | `{roomId, spokenSeconds}` | ✅ |
| `room_ended` | `RoomService.EndRoomAsync:543` | ✅ (hostId) | `{roomId, durationHours, participantCount}` | ✅ |
| `message_sent` | `ChatService.SaveMessageAsync:92` | ✅ (senderId) | `{receiverId}` | No |
| `friend_request_sent` | `FriendService.SendFriendRequestAsync:132` | ✅ | `{targetUserId}` | No |
| `friend_request_accepted` | `FriendService.RespondToFriendRequestAsync:187` | ✅ | `{senderId}` | No |
| `user_reported` | `SupportService.SubmitReportAsync:97` | ✅ (reporterId) | `{reportedUserId, reportedRoomId, category, description}` | No |
| `user_blocked` | `BlockService.BlockUserAsync:54` | ✅ | `{blockedUserId}` | No |
| `session_started` | `SessionTrackingMiddleware:53` | ✅ | `{sessionId}` | No |

### Client-Side Events (Untrusted, Allowlisted)

| Event Type | Emitted From | Allowlisted | Properties |
|------------|-------------|:---:|------------|
| `room_create_started` | Flutter app via `POST api/events/track` | ✅ | Client-defined |
| `notification_opened` | Flutter app via `POST api/events/track` | ✅ | Client-defined |
| `feature_viewed` | Flutter app via `POST api/events/track` | ✅ | Client-defined |

---

## Defined But Unused Events

| Event Constant | Status |
|---|---|
| ~~`RoomCreateStarted`~~ | Defined in `EventTypes.cs`, but only emitted from client. Server never emits it. |

> All 24 defined constants in `EventTypes.cs` have at least one emit site (server or client).

---

## Event Emission Audit by Feature

### Onboarding Funnel
| Step | Event | Emit Site | Completeness |
|------|-------|-----------|:---:|
| Register | `user_registered` | `AuthServices.RegisterAsync` | ✅ |
| Voice Upload | `voice_verification_submitted` | `AuthServices.RegisterAsync`, `AuthServices.ReRecordVoiceAsync` | ✅ |
| Email Confirm | `email_confirmed` | `OTPService.ConfirmEmailAsync` | ✅ |
| MBTI Submit | `mbti_submitted` | `AuthServices.SubmitMbtiAsync` | ✅ |
| Admin Review | `voice_verification_result` | `AdminService.ChangeUserStatusAsync` | ✅ |
| Activation | `activation_completed` | `AdminService.ChangeUserStatusAsync` | ✅ (deduplicated) |
| **Login** | — | — | ❌ **MISSING** |
| **App Open** | — | — | ❌ **MISSING** |

### Room Lifecycle
| Step | Event | Emit Site | Completeness |
|------|-------|-----------|:---:|
| Create intent | `room_create_started` | Client (EventsController) | ⚠️ Client-dependent |
| Create | `room_created` | `RoomService.CreateRoomAsync` | ✅ |
| Join request | `room_join_requested` | `RoomService.JoinRoomAsync` | ✅ |
| Join approved | `room_join_approved` | `RoomService.ApproveUserAsync` | ✅ |
| Join room | `room_joined` | `RoomHub.JoinRoom` | ✅ |
| Leave room | `room_left` | `RoomHub.LeaveRoom`, `OnDisconnectedAsync` | ✅ |
| Mic on | `mic_activated` | `RoomHub.ToggleMic` | ✅ |
| Speaking time | `speaking_time_logged` | `RoomService.EndRoomAsync` | ✅ (only at room end) |
| Room end | `room_ended` | `RoomService.EndRoomAsync` | ✅ |
| **Mic off** | — | — | ❌ **MISSING** |
| **Hand raised** | — | — | ❌ **MISSING** |
| **User kicked** | — | — | ❌ **MISSING** |
| **Stage promoted** | — | — | ❌ **MISSING** |
| **Stage demoted** | — | — | ❌ **MISSING** |
| **Room started (Go Live)** | — | — | ❌ **MISSING** |
| **Room cancelled** | — | — | ❌ **MISSING** |

### Social
| Step | Event | Emit Site | Completeness |
|------|-------|-----------|:---:|
| Send friend request | `friend_request_sent` | `FriendService.SendFriendRequestAsync` | ✅ |
| Accept friend | `friend_request_accepted` | `FriendService.RespondToFriendRequestAsync` | ✅ |
| Send message | `message_sent` | `ChatService.SaveMessageAsync` | ✅ |
| Report user | `user_reported` | `SupportService.SubmitReportAsync` | ✅ |
| Block user | `user_blocked` | `BlockService.BlockUserAsync` | ✅ |
| **Reject friend request** | — | — | ❌ **MISSING** |
| **Remove friend** | — | — | ❌ **MISSING** |
| **Unblock user** | — | — | ❌ **MISSING** |
| **Message read** | — | — | ❌ **MISSING** |
| **Room group message** | — | — | ❌ **MISSING** |

### Profile
| Step | Event | Emit Site | Completeness |
|------|-------|-----------|:---:|
| **Profile viewed** | — | — | ❌ **MISSING** |
| **Profile updated** | — | — | ❌ **MISSING** |
| **Avatar changed** | — | — | ❌ **MISSING** |

### Engagement
| Step | Event | Emit Site | Completeness |
|------|-------|-----------|:---:|
| Session start | `session_started` | `SessionTrackingMiddleware` | ⚠️ Cookie-dependent |
| Notification opened | `notification_opened` | Client (EventsController) | ⚠️ Client-dependent |
| Feature viewed | `feature_viewed` | Client (EventsController) | ⚠️ Client-dependent |
| **Session ended** | — | — | ❌ **MISSING** |
| **App backgrounded** | — | — | ❌ **MISSING** |
| **Push notification received** | — | — | ❌ **MISSING** |
| **Push notification tapped** | — | — | ❌ **MISSING** |
| **Search performed** | — | — | ❌ **MISSING** |

---

## Summary Statistics

| Category | Defined Events | Emitted Events | Missing Events |
|----------|:-:|:-:|:-:|
| Onboarding | 6 | 6 | 2 (login, app_open) |
| Room | 9 | 9 | 7 (mic_off, hand_raised, kicked, stage changes, start, cancel) |
| Social | 5 | 5 | 5 (reject, remove, unblock, message_read, group_message) |
| Profile | 0 | 0 | 3 (viewed, updated, avatar) |
| Engagement | 3 | 3 | 5 (session_end, app_bg, push events, search) |
| **Total** | **24** | **24** | **22 MISSING** |
