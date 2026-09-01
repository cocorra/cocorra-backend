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
