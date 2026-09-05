# 02 — Database & Data Model Audit

> **Generated**: 2026-08-31 | **Source**: `AppDbContext.cs`, all Model files, Enum files

---

## Entity Inventory

| Entity | Purpose | Important Fields | Relationships |
|--------|---------|-----------------|---------------|
| **ApplicationUser** | Core user identity | `Id (Guid)`, `FirstName`, `LastName`, `Age`, `Email`, `MBTI`, `Status (UserStatus)`, `VoiceVerificationPath`, `FcmToken`, `ProfilePicturePath`, `Bio`, `CreatedAt`, `RefreshToken`, `RefreshTokenExpiryTime` | Has many `RoomParticipations`, `OwnedRooms`, `BlockedDevices`. Extends `IdentityUser<Guid>`. |
| **Room** | Live audio room | `Id (Guid)`, `RoomTitle`, `Description`, `StartDate`, `Status (RoomStatus)`, `TotalCapacity`, `StageCapacity`, `DefaultSpeakerDurationMinutes`, `SelectionMode`, `HostId`, `IsPrivate`, `ImagePath`, `DurationHours`, `Category (RoomCategory)`, `CreatedAt`, `UpdatedAt` | Belongs to `Host (ApplicationUser)`. Has many `Participants (RoomParticipant)`. Extends `BaseEntity`. |
| **RoomParticipant** | User-in-room junction | `RoomId`, `UserId`, `Status (ParticipantStatus)`, `JoinedAt`, `IsOnStage`, `IsHandRaised`, `IsMuted`, `TotalSpokenSeconds`, `LastUnmutedAt`, `ExtraMinutesGranted` | Composite PK `(RoomId, UserId)`. Belongs to `Room` and `ApplicationUser`. |
| **FriendRequest** | Friend connection | `Id (Guid)`, `SenderId`, `ReceiverId`, `Status (FriendRequestStatus)`, `CreatedAt`, `UpdatedAt` | Belongs to `Sender` and `Receiver (ApplicationUser)`. Unique index on `(SenderId, ReceiverId)`. Extends `BaseEntity`. |
| **Message** | Direct message | `Id (Guid)`, `SenderId`, `ReceiverId`, `Content`, `IsRead`, `CreatedAt`, `UpdatedAt` | Belongs to `Sender` and `Receiver`. Index on `(SenderId, ReceiverId, CreatedAt)`, `(ReceiverId, IsRead)`. Extends `BaseEntity`. |
| **Notification** | In-app notification | `Id (Guid)`, `UserId`, `Title`, `Message`, `Type (NotificationType)`, `ReferenceId`, `IsRead`, `CreatedAt`, `UpdatedAt` | Belongs to `User (ApplicationUser)`. Index on `(UserId, CreatedAt)`. Extends `BaseEntity`. |
| **Report** | User/room report | `Id (Guid)`, `ReporterId`, `ReportedUserId (nullable)`, `ReportedRoomId (nullable)`, `Category (ReportCategory)`, `Description`, `ScreenshotPath`, `Status (string)`, `CreatedAt`, `UpdatedAt` | Belongs to `Reporter`, `ReportedUser`, `ReportedRoom`. Extends `BaseEntity`. |
| **SupportTicket** | Support ticket | `Id (Guid)`, `UserId (nullable)`, `Type (SupportTicketType)`, `Message`, `ContactEmail`, `ScreenshotPath`, `Status (string)`, `CreatedAt`, `UpdatedAt` | Belongs to `User`. Extends `BaseEntity`. |
| **SupportChat** | Real-time support chat | `Id (Guid)`, `UserId (string)`, `AdminId (string, nullable)`, `Status (SupportChatStatus)`, `CreatedAt`, `ClosedAt`, `RowVersion` | Has many `Messages (SupportMessage)`. **Note**: `UserId` and `AdminId` are `string`, not `Guid`. |
| **SupportMessage** | Support chat message | `Id (Guid)`, `SupportChatId`, `SenderId (string)`, `Content`, `IsFromAdmin`, `CreatedAt` | Belongs to `SupportChat`. |
| **UserBlock** | User blocking | `Id (Guid)`, `BlockerId`, `BlockedId`, `BlockedDeviceId (nullable)`, `CreatedAt`, `UpdatedAt` | Belongs to `Blocker`, `Blocked`, `BlockedDevice`. Unique on `(BlockerId, BlockedId)`. Extends `BaseEntity`. |
| **BlockedDevices** | Device ban | `Id (Guid)`, `DeviceId`, `DeviceName`, `DeviceModel`, `DeviceType`, `DeviceOs`, `IsBlocked`, `ApplicationUserId`, `CreatedAt`, `UpdatedAt` | Belongs to `ApplicationUser`. Has many `UserBlocks`. Extends `BaseEntity`. |
| **RoomTopicRequest** | Topic suggestion | `Id (Guid)`, `TopicTitle`, `Description`, `RequesterId`, `TargetCoachId`, `Status (RequestStatus)`, `VotesCount`, `CreatedAt`, `UpdatedAt` | Belongs to `Requester` and `TargetCoach`. Extends `BaseEntity`. |
| **TopicVote** | Topic vote | `UserId`, `TopicRequestId`, `VotedAt` | Composite PK `(UserId, TopicRequestId)`. Belongs to `User` and `TopicRequest`. |
| **RoomReminder** | Room reminder | `UserId`, `RoomId`, `CreatedAt` | Composite PK `(UserId, RoomId)`. Belongs to `User` and `Room`. |
| **UserEvent** | Analytics event | `Id (long)`, `UserId (nullable Guid)`, `EventType`, `PropertiesJson`, `SessionId`, `RoomId (nullable Guid)`, `OccurredAtUtc`, `IpHash`, `UserAgent` | FK to `ApplicationUser` with `SetNull` on delete. Indexes on `(EventType, OccurredAtUtc)`, `(UserId, OccurredAtUtc)`, `(RoomId, EventType, OccurredAtUtc)`. |

### BaseEntity (Abstract)

```
Id: Guid (PK)
CreatedAt: DateTime (default UTC now)
UpdatedAt: DateTime? (column name "UpdateAt" in DB)
```

Entities extending `BaseEntity`: `Room`, `FriendRequest`, `Message`, `Notification`, `Report`, `SupportTicket`, `UserBlock`, `BlockedDevices`, `RoomTopicRequest`.

Entities **NOT** extending `BaseEntity` (no `UpdatedAt`): `ApplicationUser`, `RoomParticipant`, `TopicVote`, `RoomReminder`, `SupportChat`, `SupportMessage`, `UserEvent`.

---

## Enum Values

| Enum | Values |
|------|--------|
| **UserStatus** | `Pending(0)`, `Active(1)`, `Rejected(2)`, `Banned(3)`, `ReRecord(4)` |
| **RoomStatus** | `Scheduled(0)`, `Live(1)`, `Ended(2)`, `Cancelled(3)` |
| **ParticipantStatus** | `Active(0)`, `Left(1)`, `Kicked(2)`, `PendingApproval(3)`, `Rejected(4)` |
| **FriendRequestStatus** | `Pending(0)`, `Accepted(1)`, `Rejected(2)` |
| **RoomCategory** | `Relationships(1)`, `MentalHealth(2)`, `Others(3)` |
| **RoomSelectionMode** | `Automatic_FirstComeFirstServed(0)`, `Manual_CoachDecision(1)` |
| **NotificationType** | `System(0)`, `RoomReminder(1)`, `FriendRequest(2)`, `FriendAccept(3)`, `AdminWarning(4)` |
| **ReportCategory** | `InappropriateContent(1)`, `Harassment(2)`, `Spam(3)`, `FakeIdentity(4)`, `Other(5)` |
| **SupportTicketType** | `GeneralQuestion(1)`, `TechnicalProblem(2)`, `Other(3)` |
| **SupportChatStatus** | `Pending(0)`, `Active(1)`, `Closed(2)` |
| **RequestStatus** | `Pending(0)`, `Approved(1)`, `Rejected(2)`, `Completed(3)` |
| **AdminReportAction** | `WarnUser(1)`, `Mute24h(2)`, `BanUser(3)`, `RejectReport(4)` |

---

## Analytics-Relevant Data

### Timestamps Available

| Field | Entity | Purpose | Usable for Analytics? |
|-------|--------|---------|:---:|
| `CreatedAt` | `ApplicationUser` | Registration date | ✅ (indexed: `IX_Users_CreatedAt`) |
| `CreatedAt` | `Room` (via BaseEntity) | Room creation date | ✅ |
| `StartDate` | `Room` | When room was scheduled/started | ✅ |
| `JoinedAt` | `RoomParticipant` | When user joined room | ✅ (indexed: `IX_RoomParticipants_JoinedAt`) |
| `TotalSpokenSeconds` | `RoomParticipant` | Speaking duration | ✅ (indexed) |
| `CreatedAt` | `Message` | When message was sent | ✅ |
| `CreatedAt` | `FriendRequest` | When request was sent | ✅ |
| `CreatedAt` | `Report` | When report was filed | ✅ (indexed: `IX_Reports_CreatedAt`) |
| `CreatedAt` | `Notification` | When notification was created | ✅ |
| `OccurredAtUtc` | `UserEvent` | When event occurred | ✅ (indexed) |
| `CreatedAt` / `ClosedAt` | `SupportChat` | Support resolution time | ✅ |
| `VotedAt` | `TopicVote` | When vote was cast | ✅ |

### Status Fields

| Field | Entity | Values | Notes |
|-------|--------|--------|-------|
| `Status` | `ApplicationUser` | Enum (5 values) | ⚠️ No `UpdatedAt` — status change timestamp lost |
| `Status` | `Room` | Enum (4 values) | ⚠️ No status transition log |
| `Status` | `RoomParticipant` | Enum (5 values) | No timestamp per status change |
| `Status` | `FriendRequest` | Enum (3 values) | `UpdatedAt` available via BaseEntity |
| `Status` | `Report` | String ("Open", "Resolved", "InProgress") | ⚠️ String, not enum |
| `Status` | `SupportTicket` | String ("Open") | ⚠️ String, not enum |
| `Status` | `SupportChat` | Enum (3 values) | `ClosedAt` available |

### Notable Gaps

| Missing Data Point | Impact |
|---|---|
| `ApplicationUser.UpdatedAt` | Cannot determine when user status changed, when MBTI was set, when profile was updated |
| `LastLoginAt` | Cannot determine user activity without relying on `session_started` events |
| `Room.EndedAt` | No explicit end timestamp; must infer from Room status change |
| `Room.StartedAt` (actual) | `StartDate` is the scheduled date, not when the host actually clicked "Start" |
| `Message.ReadAt` | Only boolean `IsRead`, no timestamp for read latency |
| `RoomParticipant.LeftAt` | No leave timestamp; `JoinedAt` is reset on rejoin |

---

## Data Availability Matrix

| Question | Can Current DB Answer It? | Source | Confidence |
|----------|:---:|--------|:---:|
| How many users registered? | **Yes** | `ApplicationUser.CreatedAt` | **High** |
| How many users are active? | **Yes** | `ApplicationUser.Status == Active` | **High** |
| How many users are pending verification? | **Yes** | `ApplicationUser.Status == Pending` | **High** |
| What is the registration-to-activation conversion rate? | **Yes** | `UserEvents` funnel | **High** |
| How long does admin verification take? | **No** | No status change timestamp on `ApplicationUser` | N/A |
| What is the voice verification drop-off rate? | **Yes** | `UserEvents` (`voice_verification_submitted` vs `activation_completed`) | **High** |
| How many rooms were created? | **Yes** | `Room.CreatedAt` | **High** |
| How many rooms are live right now? | **Yes** | `Room.Status == Live` | **High** |
| What is the most popular room category? | **Yes** | `Room.Category` | **High** |
| How many users joined rooms? | **Yes** | `RoomParticipant` + `UserEvents (room_joined)` | **High** |
| What is the average room duration? | **Partially** | `Room.DurationHours` is the *configured* duration, not actual. No `EndedAt`. | **Low** |
| Which features are used? | **Partially** | Only room/auth events tracked; no profile, chat, notification usage events | **Medium** |
| How long do users stay in rooms? | **No** | No `LeftAt` on `RoomParticipant`; `JoinedAt` is reset on rejoin | N/A |
| Which users return? | **Yes** | `UserEvents (session_started)` retention cohorts | **Medium** (depends on cookie reliability) |
| How many messages are sent daily? | **Yes** | `Message.CreatedAt` | **High** |
| What is the friend request acceptance rate? | **Yes** | `FriendRequest.Status` | **High** |
| How many reports are filed daily? | **Yes** | `Report.CreatedAt` | **High** |
| What is the support ticket resolution time? | **Partially** | `SupportChat.CreatedAt` to `ClosedAt`. But `SupportTicket` has no `ResolvedAt`. | **Medium** |
| When do users churn? | **No** | No last-active timestamp. Must infer from `session_started` events. | N/A |
| What is the DAU/MAU ratio? | **Partially** | Via `session_started` events — depends on mobile cookie persistence | **Low** |
| Is the platform growing? | **Yes** | `ApplicationUser.CreatedAt` time series | **High** |
