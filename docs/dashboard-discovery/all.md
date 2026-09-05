# 00 — Repository Overview

> **Generated**: 2026-08-31 | **Scope**: `cocorra-backend` repository | **Method**: Full source inspection

---

## Technology Stack

| Area             | Technology                                                                 |
|------------------|---------------------------------------------------------------------------|
| **Backend**      | ASP.NET Core (`.NET 10`), C#                                              |
| **Frontend**     | None in this repository — backend API only. Admin panel at `admin.cocorraapp.com` (separate repo). Mobile app is Flutter (separate repo). |
| **Database**     | Microsoft SQL Server (`UseSqlServer` in `Program.cs:238`)                 |
| **ORM**          | Entity Framework Core (`Microsoft.EntityFrameworkCore`)                   |
| **Authentication** | ASP.NET Identity + JWT Bearer tokens (`Program.cs:252-343`)            |
| **Real-time**    | SignalR (3 hubs: `RoomHub`, `ChatHub`, `SupportHub`)                     |
| **Voice/Video**  | LiveKit (external media server at `wss://live.cocorraapp.com`)           |
| **Push Notifications** | Firebase Cloud Messaging (FCM) via Firebase Admin SDK              |
| **Object Storage** | MinIO (S3-compatible, at `http://152.239.115.176:9000`)               |
| **Email**        | SMTP via Gmail (`smtp.gmail.com:587`)                                    |
| **Background Jobs** | `BackgroundService` — `EventFlushService`, `EventCleanupService`      |
| **Caching**      | `IMemoryCache` (in-process, for analytics stampede protection)           |
| **Infrastructure** | Docker (multi-stage `Dockerfile`, `docker-compose.yml`)               |
| **Deployment**   | WebDeploy to `cocorra.runasp.net` + Docker container on `152.239.115.176` |
| **API Docs**     | Swagger/OpenAPI (enabled in all environments)                            |
| **Rate Limiting** | ASP.NET `FixedWindowRateLimiter` — 100 req/min per IP                  |
| **Event System** | MediatR (domain events) + custom `EventTracker` (analytics events)      |

---

## Repository Structure

```
cocorra-backend/
├── Cocorra.sln                  # Solution file (4 projects)
├── Cocorra.API/                 # ASP.NET Core Web API (entry point)
│   ├── Program.cs               # Application bootstrap, DI, middleware pipeline
│   ├── Controllers/             # 12 REST API controllers
│   │   ├── AdminController.cs           # Admin user management + dashboard stats
│   │   ├── AnalyticsController.cs       # Analytics/dashboard endpoints (11 routes)
│   │   ├── AuthenticationController.cs  # Registration, login, OTP, MBTI, voice
│   │   ├── BlockController.cs           # User blocking/unblocking
│   │   ├── ChatController.cs            # Friends list, chat history, mark-read
│   │   ├── EventsController.cs          # Client-side event tracking ingestion
│   │   ├── FriendsController.cs         # Friend requests (send, respond, remove)
│   │   ├── NotificationsController.cs   # User notification management
│   │   ├── ProfileController.cs         # Profile view/update, avatar, picture
│   │   ├── RolesController.cs           # RBAC role management (Admin-only)
│   │   ├── RoomsController.cs           # Room CRUD, join, approve, feed, history
│   │   └── SupportController.cs         # Tickets, reports, real-time chat support
│   ├── Hubs/                    # SignalR WebSocket hubs
│   │   ├── RoomHub.cs           # Live room: join, mic, stage, chat (~736 lines)
│   │   ├── ChatHub.cs           # Direct messaging between friends
│   │   └── SupportHub.cs        # Admin ↔ user support chat
│   ├── Middleware/
│   │   ├── SessionTrackingMiddleware.cs  # Session cookie + session_started event
│   │   └── DeviceBlockingMiddleware.cs   # Blocked device enforcement
│   ├── EventHandlers/           # MediatR event handlers
│   ├── Services/
│   │   └── SignalRNotifier.cs   # IRealTimeNotifier → SignalR implementation
│   └── Seeder/                  # Role and identity seeders
│
├── Cocorra.BLL/                 # Business Logic Layer
│   ├── Base/                    # ResponseHandler base class
│   └── Services/                # 19 service directories
│       ├── AdminService/        # User management, dashboard stats
│       ├── AnalyticsService/    # Analytics aggregation + caching
│       ├── AuthService/         # Registration, login, JWT, tokens
│       ├── BlockService/        # User blocking logic
│       ├── BlockedDevicesService/
│       ├── ChatService/         # Message persistence
│       ├── Email/               # SMTP email service
│       ├── EventTracking/       # EventTracker, EventFlushService, EventCleanupService
│       ├── Events/              # MediatR domain events
│       ├── FriendService/       # Friend request logic
│       ├── LiveKit/             # LiveKit token generation + room management
│       ├── NotificationService/ # Push notifications (FCM) + in-app
│       ├── OTPService/          # OTP generation/verification
│       ├── ProfileService/      # Profile management
│       ├── RealTimeNotifier/    # IRealTimeNotifier interface
│       ├── RolesService/        # Role management
│       ├── RoomService/         # Room lifecycle management
│       ├── SupportService/      # Support tickets + live chat
│       └── UploadService/       # Image + voice file upload (MinIO)
│
├── Cocorra.DAL/                 # Data Access Layer
│   ├── AppMetaData/Router.cs    # All API route constants
│   ├── Data/
│   │   ├── AppDbContext.cs      # EF Core DbContext (14 DbSets)
│   │   └── RoleSeeder.cs
│   ├── DTOS/                    # 13 DTO directories
│   ├── Enums/                   # 12 enum files
│   ├── Models/                  # 18 entity models
│   ├── Repository/              # 10 repository directories
│   └── Migrations/
│
├── Cocorra.Tests/               # Unit tests (20 test files)
├── livekit/                     # LiveKit docker-compose config
├── Dockerfile                   # Multi-stage Docker build
├── docker-compose.yml           # Production container orchestration
└── *.md                         # Various planning/bug-report documents
```

---

## Architecture Summary

### Layered Architecture (N-Tier)

Cocorra follows a classic **3-tier architecture**:

1. **Cocorra.API** (Presentation) — Controllers, Hubs, Middleware
2. **Cocorra.BLL** (Business Logic) — Services, domain events, event tracking
3. **Cocorra.DAL** (Data Access) — EF Core DbContext, repositories, models, DTOs

All layers use **constructor injection** via ASP.NET's built-in DI container. Services are registered as `Scoped` in `Program.cs:153-215`.

### Authentication and Authorization

- **Identity**: ASP.NET Identity with `ApplicationUser : IdentityUser<Guid>` and `IdentityRole<Guid>`.
- **JWT**: Symmetric key signing, 1-day token lifetime (inferred from refresh token pattern).
- **Refresh Tokens**: Stored in `ApplicationUser.RefreshToken` with `RefreshTokenExpiryTime`.
- **Authorization Policies**:
  - **Default policy**: Requires `VerificationStatus=Active` claim — all `[Authorize]` endpoints enforce this.
  - **VerificationOnly**: Allows `Pending`, `ReRecord`, `Active` users — for re-recording voice verification.
- **Roles**: `Admin`, `Coach`, `User` (seeded via `RoleSeeder`).

### Real-time Architecture

Three SignalR hubs mounted at:
- `/hubs/rooms` → `RoomHub` — Live room interactions (join, mic, stage, chat, kick, end room)
- `/hubs/chat` → `ChatHub` — Direct messaging between friends
- `/hubs/support` → `SupportHub` — Admin-to-user support real-time chat

The `RoomHub` maintains a **static in-memory `ConcurrentDictionary`** (`_connections`) mapping `ConnectionId → (UserId, RoomId)`. This is used for disconnect cleanup and admin force-disconnect on ban. **This state is not distributed** — it would not survive a multi-instance deployment.

### Event Tracking System

A custom analytics backbone implemented in `Cocorra.BLL/Services/EventTracking/`:

1. **`EventTracker`** (Singleton): Non-blocking write to a `Channel<UserEvent>` (bounded, 10K capacity, DropWrite on full).
2. **`EventFlushService`** (BackgroundService): Batches up to 100 events and bulk-inserts into `UserEvents` table.
3. **`EventCleanupService`** (BackgroundService): Purges events older than 180 days every 24 hours.

Events are tracked from:
- **Server-side**: Auth service, room service, admin service, RoomHub (authoritative events)
- **Client-side**: `EventsController` — limited to `room_create_started`, `notification_opened`, `feature_viewed`
- **Middleware**: `SessionTrackingMiddleware` emits `session_started` once per session cookie

### External Services

| Service | Purpose | Configuration |
|---------|---------|---------------|
| SQL Server | Primary database | `152.239.115.176:1433` |
| MinIO | Object storage (images, voice) | `152.239.115.176:9000` |
| LiveKit | WebRTC media server | `wss://live.cocorraapp.com` |
| Firebase | Push notifications (FCM) | `firebase-config.json` |
| Gmail SMTP | Transactional email | `smtp.gmail.com:587` |
| STUN/TURN | NAT traversal for WebRTC | Google STUN + LiveKit TURN |

### Existing Analytics Infrastructure

- **AnalyticsController**: 11 endpoints for admin/coach dashboard data.
- **AnalyticsService**: Caching layer with SemaphoreSlim stampede protection (10-min TTL).
- **AnalyticsRepository**: Raw LINQ-to-SQL queries against `Users`, `Rooms`, `RoomParticipants`, `Reports`, and `UserEvents` tables.
- **Admin Dashboard Stats**: Separate simple endpoint (`GET /Api/V1/Admin/Dashboard/Stats`) returning user status counts.

### Existing Logging Infrastructure

- Standard ASP.NET Core logging (`ILogger<T>`) with `Information` default level.
- Extensive diagnostic `[JOINROOM-TRACE]` and `[HUB-TRACE]` log entries in `RoomHub.cs`.
- Docker JSON file logging driver with 10MB/3-file rotation.
- **No structured logging sink** (no Seq, ELK, Application Insights, etc.).
- **No request logging middleware** (no Serilog request pipeline).

### Existing Monitoring Infrastructure

- Docker `HEALTHCHECK` hitting `http://localhost:8080/`.
- **No application performance monitoring** (no APM tool detected).
- **No error tracking service** (no Sentry, Raygun, etc.).
- **No metrics export** (no Prometheus, OpenTelemetry, etc.).

> **FACT**: The monitoring infrastructure is minimal — health check only. No centralized logging, APM, or alerting.
# 01 — Product & Feature Inventory

> **Generated**: 2026-08-31 | **Method**: Full source inspection of controllers, hubs, services, and entities

---

## What Is Cocorra?

Cocorra is a **voice-first social platform** — a Clubhouse/Twitter Spaces-style application focused on Arabic-speaking communities. Users join live audio rooms hosted by "coaches," participate in discussions by raising their hand to get on stage, send direct messages to friends, and interact via a mobile app (Flutter).

Key differentiators:
- **Voice Verification**: Every user must submit a voice recording that an admin manually approves before gaining full access.
- **MBTI Integration**: Users submit their MBTI personality type during onboarding.
- **Coach-Led Rooms**: Rooms have a host (coach) who controls the stage — approving speakers, granting time, muting, kicking.
- **Topic Requests**: Users can request room topics, and others can vote on them.

---

## Feature 1: User Registration & Onboarding

### Purpose
Allow new users to create accounts and go through a multi-step verification process.

### Actors
- **New User** (unverified)
- **Admin** (approver)

### User Journey
1. User registers via `POST /Api/V1/Authentication/Register` with name, email, password, age, and voice recording.
2. System sends OTP email for email confirmation.
3. User confirms email via `GET /Api/V1/Authentication/ConfirmEmail?email=&otpCode=`.
4. User submits MBTI type via `POST /Api/V1/Authentication/SubmitMbti`.
5. Admin reviews voice recording and changes user status (`Pending → Active`, `Rejected`, or `ReRecord`).
6. If `ReRecord`, user re-submits voice via `POST /Api/V1/Authentication/ReRecordVoice`.
7. On `Active`, user gets full access; `activation_completed` event is tracked.

### Backend Components
- **Endpoints**: Register, Login, ConfirmEmail, ResendOtp, SubmitMbti, ReRecordVoice, ForgotPassword, ResetPassword, UpdatePassword, DeleteAccount, UpdateFcmToken, RefreshToken, RevokeToken
- **Controllers**: `AuthenticationController.cs`
- **Services**: `AuthServices`, `OTPService`, `EmailService`, `UploadVoice`
- **Entities**: `ApplicationUser`, `UserEvent`

### Frontend Components
- **UNKNOWN** — Mobile app is in a separate Flutter repository not included in this codebase.

### Data Generated
- `ApplicationUser` row (with `CreatedAt`, `Status`, `MBTI`, `VoiceVerificationPath`)
- `UserEvent` rows: `user_registered`, `email_confirmed`, `voice_verification_submitted`, `voice_verification_result`, `mbti_submitted`, `activation_completed`

### Current Measurability
**PARTIALLY MEASURABLE**

Registration count is measurable from `ApplicationUser.CreatedAt`. The onboarding funnel is measurable through `UserEvents` (registration → email → voice → activation). However, onboarding step timing and abandonment reasons are not captured. No `UpdatedAt` on user status changes to track admin review latency.

---

## Feature 2: Voice Rooms (Core Feature)

### Purpose
Enable live audio discussions led by a host/coach, where audience members can request to speak on stage.

### Actors
- **Host/Coach** — creates and manages the room
- **Participant** — joins and listens
- **Speaker** — on-stage participant with mic access

### User Journey
1. Host creates room via `POST /Api/V1/Room/Create` (title, description, category, capacity, duration, selection mode).
2. Room appears in feed (`GET /Api/V1/Room/Feed`) with status `Scheduled`.
3. Host starts room via `POST /Api/V1/Room/{id}/Start` → status becomes `Live`.
4. Users join via `POST /Api/V1/Room/{id}/Join` → creates `RoomParticipant` row.
5. User connects via SignalR `RoomHub.JoinRoom(roomId)` → gets LiveKit token for audio.
6. User raises hand via `RoomHub.RaiseHand(roomId)`.
7. Host approves to stage via `RoomHub.ApproveToStage(roomId, userId)`.
8. Speaker toggles mic via `RoomHub.ToggleMic(roomId, muteStatus)` — spoken time is tracked.
9. Host can grant extra time, move to audience, or kick users.
10. Host ends room via `RoomHub.EndRoom(roomId)` or `POST /Api/V1/Room/{id}/End`.
11. On disconnect, if host disconnects, room auto-ends for everyone.

### Backend Components
- **Endpoints**: Create, Join, Approve, State, Feed, ToggleReminder, Start, End, Token, AdminHistory
- **Controllers**: `RoomsController.cs`
- **Hubs**: `RoomHub.cs` (JoinRoom, LeaveRoom, RaiseHand, LowerHand, ApproveToStage, MoveToAudience, ToggleMic, GrantExtraTime, KickUser, EndRoom, SendRoomGroupMessage, SendRoomPrivateMessage)
- **Services**: `RoomService`, `LiveKitService`
- **Entities**: `Room`, `RoomParticipant`, `RoomReminder`, `UserEvent`

### Data Generated
- `Room` row (CreatedAt, StartDate, Status, Category, HostId, capacities, duration)
- `RoomParticipant` rows (JoinedAt, Status, IsOnStage, TotalSpokenSeconds, IsMuted, IsHandRaised, ExtraMinutesGranted)
- `RoomReminder` rows (UserId, RoomId)
- `UserEvent` rows: `room_created`, `room_join_requested`, `room_join_approved`, `room_joined`, `room_left`, `mic_activated`, `speaking_time_logged`, `room_ended`

### Current Measurability
**FULLY MEASURABLE**

Room creation, participation, spoken time, stage activity are all captured via both relational data (`Room`, `RoomParticipant`) and events (`UserEvents`). Peak hours, active rooms, and participation mode (speaker vs listener) are already exposed in the analytics API.

---

## Feature 3: Direct Messaging (Chat)

### Purpose
Allow friends to send persistent text messages to each other.

### Actors
- **User A** (sender)
- **User B** (receiver, must be an accepted friend)

### User Journey
1. User opens friends list via `GET /api/Chat/friends-list`.
2. User views chat history via `GET /api/Chat/history/{friendId}`.
3. User sends message via SignalR `ChatHub.SendMessage(receiverId, content)`.
4. Message is persisted to DB and delivered in real-time.
5. User marks messages as read via `PUT /api/Chat/mark-read/{friendId}`.

### Backend Components
- **Endpoints**: friends-list, history/{friendId}, mark-read/{friendId}
- **Controllers**: `ChatController.cs`
- **Hubs**: `ChatHub.cs`, `RoomHub.SendRoomPrivateMessage` (in-room DMs)
- **Services**: `ChatService`
- **Entities**: `Message`

### Data Generated
- `Message` rows (SenderId, ReceiverId, Content, IsRead, CreatedAt)
- `UserEvent`: `message_sent` (tracked server-side)

### Current Measurability
**PARTIALLY MEASURABLE**

Message count and read status are in the database. However: no `ReadAt` timestamp (only boolean `IsRead`), no tracking of message response time, no conversation-level aggregation, and room-group messages are **ephemeral** (not persisted).

---

## Feature 4: Friends System

### Purpose
Allow users to connect as friends, enabling direct messaging.

### Actors
- **Sender** — initiates friend request
- **Receiver** — accepts or rejects

### User Journey
1. User searches for another user by ID via `GET /api/Friends/search/{targetId}`.
2. User sends friend request via `POST /api/Friends/send-request`.
3. Receiver responds via `POST /api/Friends/respond-request/{senderId}?accept=true|false`.
4. Either party can remove the friendship via `DELETE /api/Friends/remove/{targetId}`.

### Backend Components
- **Endpoints**: search/{targetId}, send-request, respond-request/{senderId}, remove/{targetId}
- **Controllers**: `FriendsController.cs`
- **Services**: `FriendService`
- **Entities**: `FriendRequest`

### Data Generated
- `FriendRequest` rows (SenderId, ReceiverId, Status [Pending/Accepted/Rejected], CreatedAt)
- `UserEvent`: `friend_request_sent`, `friend_request_accepted`

### Current Measurability
**PARTIALLY MEASURABLE**

Friend request counts are available. Accept/reject rates can be calculated. However: no `UpdatedAt` on `FriendRequest` means we can't measure response latency. No `friend_request_rejected` event is defined in `EventTypes`.

---

## Feature 5: Topic Requests & Voting

### Purpose
Allow users to suggest room topics and vote on them, enabling community-driven content.

### Actors
- **Requester** — suggests a topic
- **Voter** — votes for a topic
- **Target Coach** — receives topic request

### User Journey
1. User submits topic request (title, description, target coach).
2. Coach reviews and approves/rejects.
3. Other users vote on pending topics.

### Backend Components
- **Entities**: `RoomTopicRequest`, `TopicVote`
- **Note**: No dedicated controller found for topic requests. **UNKNOWN if this is exposed via API or only implemented at the model level.**

### Data Generated
- `RoomTopicRequest` rows (TopicTitle, RequesterId, TargetCoachId, Status, VotesCount, CreatedAt)
- `TopicVote` rows (UserId, TopicRequestId, VotedAt)

### Current Measurability
**NOT MEASURABLE**

No API endpoints or event tracking found for topic requests. The models exist but there are no controllers, services, or events to populate or measure them.

---

## Feature 6: Notifications

### Purpose
Deliver in-app and push notifications for system events, friend requests, room reminders, and admin warnings.

### Actors
- **System** — generates notifications
- **User** — receives and reads them

### User Journey
1. System creates `Notification` rows on events (friend request, room reminder, status change).
2. FCM push sent via `PushNotificationService` if user has `FcmToken`.
3. User fetches via `GET /api/Notifications/my-notifications`.
4. User marks read via `PUT /api/Notifications/read-notification/{id}` or `PUT /api/Notifications/mark-all-read`.

### Backend Components
- **Endpoints**: my-notifications, read-notification/{id}, mark-all-read
- **Controllers**: `NotificationsController.cs`
- **Services**: `NotificationService`, `PushNotificationService`
- **Entities**: `Notification`

### Data Generated
- `Notification` rows (UserId, Title, Message, Type, ReferenceId, IsRead, CreatedAt)
- `UserEvent`: `notification_opened` (client-tracked)

### Current Measurability
**PARTIALLY MEASURABLE**

Notification delivery count is in the database. Read rate calculable from `IsRead`. Push delivery success is NOT tracked (FCM response not persisted). `notification_opened` depends on client implementation.

---

## Feature 7: User Profiles

### Purpose
Allow users to view and customize their profile.

### Actors
- **User** — views/edits own profile
- **Other User** — views another user's profile

### Backend Components
- **Endpoints**: me, {targetUserId}, update, upload-picture, update-avatar-preset
- **Controllers**: `ProfileController.cs`
- **Services**: `ProfileService`, `UploadImage`
- **Entities**: `ApplicationUser` (FirstName, LastName, Age, MBTI, ProfilePicturePath, Bio)

### Data Generated
- `ApplicationUser` field updates

### Current Measurability
**NOT MEASURABLE**

No `profile_updated`, `profile_viewed`, or `avatar_changed` events. No `UpdatedAt` on `ApplicationUser`. Cannot measure profile completion rate or engagement.

---

## Feature 8: Reporting & Moderation

### Purpose
Allow users to report inappropriate behavior; admins review and take action.

### Actors
- **Reporter** — files a report
- **Admin** — reviews reports, takes action

### User Journey
1. User submits report via `POST /Api/V1/Support/Report` (category, description, optional screenshot).
2. Admin views reports via `GET /Api/V1/Support/admin/reports`.
3. Admin updates status via `PUT /Api/V1/Support/admin/reports/{id}/status`.
4. Admin takes action via `POST /Api/V1/Support/admin/reports/{id}/action` (warn, mute 24h, ban, reject report).

### Backend Components
- **Endpoints**: SubmitReport, AdminReports, AdminUpdateReportStatus, AdminTakeReportAction
- **Controllers**: `SupportController.cs`
- **Services**: `SupportService`
- **Entities**: `Report`

### Data Generated
- `Report` rows (ReporterId, ReportedUserId, ReportedRoomId, Category, Description, Status, CreatedAt)
- `UserEvent`: `user_reported`

### Current Measurability
**FULLY MEASURABLE**

Report counts, categories, status transitions, most-reported users — all available in the database and exposed via analytics API.

---

## Feature 9: User Blocking

### Purpose
Allow users to block others, preventing interaction.

### Actors
- **Blocker** — blocks another user
- **Blocked** — loses ability to interact with blocker

### Backend Components
- **Endpoints**: block/{target}, unblock/{target}
- **Controllers**: `BlockController.cs`
- **Services**: `BlockService`
- **Entities**: `UserBlock`

### Data Generated
- `UserBlock` rows (BlockerId, BlockedId, BlockedDeviceId, CreatedAt)
- `UserEvent`: `user_blocked`

### Current Measurability
**PARTIALLY MEASURABLE**

Block counts exist. No `user_unblocked` event. No `UpdatedAt` for unblock tracking. No analytics endpoint for block metrics.

---

## Feature 10: Support System

### Purpose
Provide ticketing and real-time chat support between users and admins.

### Actors
- **User** — submits tickets or chats with support
- **Admin** — claims chats, replies, closes

### User Journey
1. User submits ticket via `POST /Api/V1/Support/Ticket` (type, message, optional screenshot) — anonymous OK.
2. User opens real-time support chat via `POST /Api/V1/Support/chat/send`.
3. Admin sees pending chats, claims one via `POST /Api/V1/Support/chat/{chatId}/claim`.
4. Admin replies via `POST /Api/V1/Support/chat/{chatId}/reply`.
5. Admin closes via `POST /Api/V1/Support/chat/{chatId}/close`.

### Backend Components
- **Endpoints**: SubmitTicket, chat/send, chat/{chatId}/claim, chat/{chatId}/reply, chat/{chatId}/close, chat/pending, chat/active, chat/history, chat/my-chat
- **Controllers**: `SupportController.cs`
- **Hubs**: `SupportHub.cs`
- **Services**: `SupportService`
- **Entities**: `SupportTicket`, `SupportChat`, `SupportMessage`

### Data Generated
- `SupportTicket` rows (UserId, Type, Message, ContactEmail, Status, CreatedAt)
- `SupportChat` rows (UserId, AdminId, Status, CreatedAt, ClosedAt)
- `SupportMessage` rows (SupportChatId, SenderId, Content, IsFromAdmin, CreatedAt)

### Current Measurability
**PARTIALLY MEASURABLE**

Ticket and chat counts are available. Resolution time calculable from `CreatedAt` to `ClosedAt`. But no analytics endpoint for support metrics. No response time tracking per message.

---

## Feature 11: Admin User Management

### Purpose
Allow admins to manage users — view, search, change status, bulk operations, device blocking.

### Actors
- **Admin** — manages users

### Backend Components
- **Endpoints**: GetAll, GetById, ChangeStatus, BulkChangeStatus, Stats, BlockDeviceAndEmail
- **Controllers**: `AdminController.cs`
- **Services**: `AdminService`

### Data Generated
- Status changes tracked via `UserEvent` (`voice_verification_result`, `activation_completed`)

### Current Measurability
**FULLY MEASURABLE** for user counts. Dashboard stats endpoint exists.

---

## Feature 12: Role Management

### Purpose
Manage user roles (Admin, Coach, User).

### Backend Components
- **Endpoints**: List, GetRoleById, ManageUserRoles, GetUsersInRole
- **Controllers**: `RolesController.cs`
- **Services**: `RolesService`

### Current Measurability
**NOT MEASURABLE** — No event tracking for role changes. No analytics for role distribution over time.

---

## Feature Measurability Summary

| Feature | Measurability | Key Gap |
|---------|:---:|---------|
| Registration & Onboarding | PARTIALLY | No admin review latency, no step timing |
| Voice Rooms | FULLY | Already well-instrumented |
| Direct Messaging | PARTIALLY | No ReadAt, no response time, ephemeral group msgs lost |
| Friends System | PARTIALLY | No response latency, no rejection event |
| Topic Requests | NOT | Models exist but no API/events |
| Notifications | PARTIALLY | No push delivery tracking, client-dependent opened event |
| User Profiles | NOT | No profile events at all |
| Reporting | FULLY | Well-covered by analytics API |
| User Blocking | PARTIALLY | No unblock event |
| Support System | PARTIALLY | No analytics endpoint, no per-message timing |
| Admin Management | FULLY | Dashboard stats endpoint exists |
| Role Management | NOT | No events or analytics |

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
