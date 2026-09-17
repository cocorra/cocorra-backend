# Cocorra Backend — Testing Strategy & Implementation Roadmap

## Context

Cocorra is a live audio-room platform (.NET 10, three projects: `Cocorra.API` → `Cocorra.BLL` → `Cocorra.DAL`)
with SQL Server, SignalR, LiveKit media, Firebase push, MinIO/S3 storage and an analytics
event pipeline. It is in production.

`Cocorra.Tests` already holds **468 xUnit tests across 56 files**. They are good tests, but they are
*all one kind of test*: in-process, mock-driven, no HTTP, no real database engine. The consequence is
that the behaviours most likely to cause a production incident are the ones nothing currently proves:

| Not covered today | Why it matters |
| --- | --- |
| The HTTP pipeline | No `WebApplicationFactory`. `[Authorize]`, the `VerificationStatus=Active` default policy, `VerificationOnly`, `Roles="Admin"`, the rate limiter, CORS, `DeviceBlockingMiddleware` and `SessionTrackingMiddleware` are **never executed by a test**. Controller tests hand-build `ControllerContext` with a pre-authenticated `ClaimsPrincipal`, so a missing `[Authorize]` attribute would pass every test in the suite. |
| A real database engine | 8 files use `SqliteTestHost`, 1 uses EF InMemory. SQLite ignores `HasFilter("[DeviceId] IS NOT NULL")` on the `BlockedDevices` unique index, does not reproduce SQL Server error 2601/2627 classification in `EventFlushService.IsDuplicateKeyViolation`, and cannot run the `AnalyticsRepository` T-SQL. Migrations are never applied in a test. |
| Cross-layer flows | `DeleteAccountAsync` → `EndRoomAsync` → LiveKit teardown → `ExecuteDeleteAsync` over four Restrict-FK tables is the single highest-risk transaction in the codebase and is only unit-tested in pieces. |
| CI execution | `.github/workflows/deploy.yml` builds and deploys on push to `prod`. It **never runs `dotnet test`**. Nothing stops a red build reaching production. |

This document defines the target architecture, the tests to write, their priority, and the pipeline
that runs them. **No tests are implemented here — this is the roadmap.**

Two facts found during the audit that shape the plan and are stated plainly:

1. **`Cocorra.API/appsettings.json` is tracked in git and contains live production secrets**: SQL Server
   `sa` password, JWT `securityKey`, Gmail app password, LiveKit `ApiSecret`, TURN credential, MinIO
   `SecretKey`, and the seeded admin password. `ProductionReadinessTests` guards only
   `Analytics:IpHashSalt`. Test **BE-SEC-020** extends that guard to every secret key; rotation itself is
   an ops task outside this plan's scope, but the test is what stops the next one being committed.
2. **There is no soft deletion anywhere in the schema.** No `IsDeleted` column, no global query filter.
   Deletes are hard, protected by `DeleteBehavior.Restrict` on `FriendRequest`, `Message`, `UserBlock`,
   `Room.Host`, `RoomParticipant.User`, `Report.Reporter`, and manual `ExecuteDeleteAsync` cleanup in
   `AuthServices.DeleteAccountAsync`. The plan therefore tests **referential-integrity deletion**
   (BE-DB-010…014), not soft deletion. Nothing is invented to fill the requested heading.

---

## A. Testing Architecture

### A.1 Current state (audited, not assumed)

| Dimension | Finding |
| --- | --- |
| Test projects | One: `Cocorra.Tests` (`net10.0`), references API + BLL + DAL |
| Framework | xUnit 2.9.3, `Microsoft.NET.Test.Sdk` 17.14.1, `xunit.runner.visualstudio` 3.1.4 |
| Mocking | Moq 4.20.72 |
| Assertions | Built-in xUnit `Assert` — no FluentAssertions/Shouldly |
| Coverage tool | `coverlet.collector` 6.0.4 present, **never invoked by any pipeline** |
| Test DB | `Helpers/SqliteTestHost.cs` — SQLite `:memory:` + `EnsureCreated()` (8 files); EF InMemory (1 file, `EventTrackingSmokeTests`) |
| Testcontainers | None |
| Fixtures | `SqliteTestHost` (IDisposable, per-test-class), `TestIdentityHelper` (Moq'd `UserManager`/`RoleManager`) |
| Factories / builders | None — entities are constructed inline in each test |
| Seed data | None shared; `RoleSeeder` / `IdentitySeeder` are production-only, never exercised by a test |
| Auth helpers | None. Each controller test file re-creates a `ClaimsIdentity` inline |
| HTTP infrastructure | None |
| External service mocking | `ILiveKitService`, `IPushNotificationService`, `IEmailService`, `IUploadImage`, `IUploadVoice` are all Moq'd per test. `IAmazonS3` Moq'd in `UploadServiceTests` |
| CI test execution | **None** |

Existing test files map cleanly onto the plan and are treated as the starting inventory —
`RoomHubTests` (22), `HostReconnectGraceTests` (17), `AuthServicesTests` (16),
`DeviceRegistryTests` (15), `RoomDurationLimitTests` (12), `LiveKitServiceTests` (12),
`PushPayloadFormattingTests` (11) and the rest already cover a real slice of the unit tier.

### A.2 Target architecture

```
Cocorra.sln
├─ Cocorra.Tests                  EXISTING — unit tier only, no I/O, < 30s wall clock
│   ├─ Helpers/SqliteTestHost.cs      (keep: query-shape + EF-model tests)
│   ├─ Helpers/TestIdentityHelper.cs  (keep)
│   └─ Builders/                      NEW — UserBuilder, RoomBuilder, ParticipantBuilder,
│                                     ReportBuilder, SupportChatBuilder, DeviceBuilder
│
├─ Cocorra.IntegrationTests       NEW — real SQL Server, real HTTP pipeline
│   ├─ Infrastructure/
│   │   ├─ SqlServerFixture.cs        Testcontainers.MsSql, ICollectionFixture,
│   │   │                             one container per run, migrations applied once
│   │   ├─ CocorraApiFactory.cs       WebApplicationFactory<Program>
│   │   ├─ DatabaseReset.cs           Respawn between test classes
│   │   ├─ AuthTestClient.cs          real-login + raw-JWT-mint helpers
│   │   └─ Fakes/                     FakeLiveKitService (records calls),
│   │                                 FakePushNotificationService, NullEmailService,
│   │                                 InMemoryS3Client
│   ├─ Api/                           one file per controller (D. API Test Matrix)
│   ├─ Persistence/                   constraints, FKs, transactions (C.2)
│   ├─ Hubs/                          SignalR over TestServer (C.5)
│   └─ Jobs/                          hosted-service sweeps against real DB (C.6)
│
└─ Cocorra.LoadTests               NEW — k6 scripts (loadtest.js already exists at repo root)
```

**Real vs faked inside `CocorraApiFactory`:**

| Component | Integration tier | Rationale |
| --- | --- | --- |
| `AppDbContext` → SQL Server | **Real** (container) | The whole point |
| ASP.NET Identity, `UserManager`, lockout | **Real** | Ban/lockout semantics are security-critical |
| JWT bearer auth + `OnTokenValidated` lockout check | **Real** | `Program.cs:360-374` is untested today |
| Authorization policies (`DefaultPolicy`, `VerificationOnly`) | **Real** | The core authz control |
| `DeviceBlockingMiddleware`, `SessionTrackingMiddleware` | **Real** | Never executed by a test today |
| Rate limiter | **Real**, but raised to a high `PermitLimit` in the test host except in BE-SEC-030 | 100 req/min/IP would fail unrelated tests from one IP |
| SignalR hubs | **Real** over `TestServer` | Hub auth + group semantics |
| `ILiveKitService` | **Faked** by default; real against a disposable LiveKit container in the `LiveKitIntegration` collection only | Section E |
| `IPushNotificationService` | **Faked** (records payloads) | Section F |
| `IEmailService` | **Null/recording fake** | No SMTP from CI |
| `IAmazonS3` (MinIO) | **In-memory fake**; one MinIO-container suite for upload round-trip | Section C.7 |
| `FirebaseApp` | Never initialised in tests — `PushNotificationService` already guards `DefaultInstance == null` | Existing guard |

**Conventions to adopt (all new tests, retrofitted opportunistically):**

- Naming: `Method_Condition_ExpectedOutcome` (already the dominant style — keep it).
- One assertion *concept* per test; `Assert.Multiple` for coupled state.
- Add **FluentAssertions** to both test projects. Rationale: the integration tier asserts on
  `HttpResponseMessage` + deserialised `Response<T>` envelopes, where `Assert` produces failure
  messages that do not name the offending field.
- Add **Respawn** for DB reset between integration classes (faster and safer than re-creating the DB).
- Add **Bogus** only inside `Builders/` — never inline in a test, so no test depends on random data.
- Every new test file carries an `// Owns: BE-XXX-NNN` header comment linking it to the backlog.

### A.3 New NuGet packages required

| Package | Project | Purpose |
| --- | --- | --- |
| `Microsoft.AspNetCore.Mvc.Testing` | IntegrationTests | `WebApplicationFactory<Program>` |
| `Testcontainers.MsSql` | IntegrationTests | Disposable SQL Server 2022 |
| `Respawn` | IntegrationTests | Fast per-class DB reset |
| `FluentAssertions` | Both | Readable failures on HTTP/DTO assertions |
| `Bogus` | Both | Deterministic seeded builders |
| `Microsoft.AspNetCore.SignalR.Client` | IntegrationTests | Hub tests over `TestServer` |
| `coverlet.collector` | IntegrationTests | Already in `Cocorra.Tests`; mirror it |

`Program.cs` needs one change to make `WebApplicationFactory<Program>` work: add
`public partial class Program { }` at the end of the file (top-level-statements requirement).
That is the only production-code edit this plan requires.

---

## B. Unit Test Plan

Scope: pure business behaviour in `Cocorra.BLL`, with all collaborators mocked. Runs in
`Cocorra.Tests`. Tests assert on **outcomes a product owner would recognise** — a response status, a
state transition, a notification being sent — never on how many times a private helper ran.

Format per test: **Given / When / Then**. `[EXISTS]` marks behaviour already covered by the current
suite and needing only verification or a rename; everything else is new.

### B.1 `RoomService` — room lifecycle (the core loop)

`Cocorra.BLL/Services/RoomService/RoomService.cs`

**BE-ROOM-001** · `CreateRoomAsync` · Happy path, immediate live · Unit · **P0**
> **Given** a valid `CreateRoomDto` with `DurationHours = 2` and no `ScheduledStartDate`
> **When** `CreateRoomAsync` is called by host H
> **Then** the room is persisted with `Status = Live`, `WentLiveAt` set to now, the host is added as an
> `Active`, `IsOnStage = true`, `IsMuted = false` participant, `EnsureRoomExistsAsync` is called once,
> and `room_created` + `room_went_live` (`startPath = "created_live"`) are tracked.

**BE-ROOM-002** · `CreateRoomAsync` · Future start date · Unit · **P1**
> **Given** `ScheduledStartDate` is in the future
> **When** the room is created
> **Then** `Status = Scheduled`, `WentLiveAt` is null, **no** host participant row is created,
> `EnsureRoomExistsAsync` is **not** called, and no `room_went_live` event is emitted.

**BE-ROOM-003** · `CreateRoomAsync` · Invalid duration · Unit · **P1**
> **Given** `DurationHours` ∈ {0, 1, 4, 24, −1}
> **When** the room is created
> **Then** a `BadRequest` "Room duration must be exactly 2 or 3 hours." is returned and nothing is
> persisted. *(`AllowedDurations = {2,3}` — `[Theory]` over the invalid set.)*

**BE-ROOM-004** · `CreateRoomAsync` · Image upload failure is non-fatal · Unit · **P2**
> **Given** `IUploadImage.SaveImageAsync` returns a string starting with `"Error"`
> **When** the room is created
> **Then** the room is still created successfully with `ImagePath = null`.

**BE-ROOM-005** · `JoinRoomAsync` · Public room, new participant · Unit · **P0**
> **Given** a `Live`, non-private room below capacity and a user with no participant row
> **When** they join
> **Then** an `Active`, `IsOnStage = false`, `IsMuted = true` row is created and a LiveKit token with
> `canPublish = false` is returned alongside `ServerUrl` and `IceServers`.

**BE-ROOM-006** · `JoinRoomAsync` · Private room queues for approval and issues no token · Unit · **P0**
> **Given** a `Live` room with `IsPrivate = true`
> **When** a user joins
> **Then** the participant is `PendingApproval`, `room_join_requested` is tracked, a `Notification` row
> is created for the host, a push is sent to the host's `FcmToken` if present, and the returned
> `JoinRoomResultDto` carries **no LiveKit token**.

**BE-ROOM-007** · `JoinRoomAsync` · Rejoin as host preserves publish rights · Unit · **P0**
> **Given** the host has an existing `Active` participant row
> **When** they call join again
> **Then** a token with `canPublish = true` is minted (`room.HostId == p.UserId` branch of
> `ResolveCanPublish`) and no duplicate participant row is created.

**BE-ROOM-008** · `JoinRoomAsync` · Kicked user is refused · Unit · **P0**
> **Given** the caller's participant row is `Kicked`
> **When** they join
> **Then** `BadRequest("You are banned from this room.")` and **no token is generated**.

**BE-ROOM-009** · `JoinRoomAsync` · Capacity boundary · Unit · **P1**
> **Given** `TotalCapacity = N` and exactly N participants counted as `Active` **or** `PendingApproval`
> **When** an N+1st user joins
> **Then** `BadRequest("Room is full.")`. `[Theory]`: N−1 admits, N refuses.
> *(Guards the specific counting rule: `PendingApproval` occupies a seat.)*

**BE-ROOM-010** · `JoinRoomAsync` · Scheduled / Ended / Cancelled rooms · Unit · **P1**
> **Given** room status ∈ {Scheduled, Ended, Cancelled}
> **When** a user joins
> **Then** Scheduled → "has not started yet"; Ended/Cancelled → "no longer available"; never a token.

**BE-ROOM-011** · `ApproveUserAsync` · Non-host is refused · Unit · **P0**
> **Given** a caller who is not `room.HostId`
> **When** they approve a pending user
> **Then** `BadRequest("Only the host can approve join requests.")` and the participant stays
> `PendingApproval`.

**BE-ROOM-012** · `ApproveUserAsync` · Happy path · Unit · **P1**
> **Given** a `PendingApproval` participant and the real host
> **When** approved
> **Then** status → `Active`, a notification row is written, a push is sent when an FCM token exists,
> `room_join_approved` is tracked, and `UserApprovedToJoinRoomEvent` is published.

**BE-ROOM-013** · `ApproveUserAsync` · Idempotent on already-active user · Unit · **P2**
> **Given** the target is already `Active`
> **When** approved again
> **Then** `Success("User is already active in the room.")` and no duplicate notification or push.

**BE-ROOM-014** · `GetRoomStateAsync` · Non-live room yields no credential · Unit · **P0**
> **Given** a room whose `Status != Live` (Ended, Scheduled or Cancelled)
> **When** state is requested by a participant
> **Then** `BadRequest("This room is not live.")` and `GenerateToken` is **never** called.
> *(This is the guard that stopped `DeleteAccountAsync` leaking 4-hour tokens for ended rooms — the
> regression must stay pinned.)*

**BE-ROOM-015** · `GetRoomStateAsync` · Non-participant is refused · Unit · **P0**
> **Given** a `Live` room and a caller with no participant row, or one that is not `Active`
> **When** state is requested
> **Then** `BadRequest("You are not an active member of this room.")` and no token is minted.

**BE-ROOM-016** · `GetRoomStateAsync` · Stage member gets a publishing token · Unit · **P1**
> **Given** an `Active` participant with `IsOnStage = true` who is not the host
> **When** state is requested
> **Then** the minted token has `canPublish = true`; with `IsOnStage = false`, `canPublish = false`.

**BE-ROOM-017** · `GetRoomStateAsync` · Only active participants are listed · Unit · **P2**
> **Given** a room containing `Active`, `Left`, `Kicked` and `PendingApproval` rows
> **When** state is requested
> **Then** `Participants` contains only the `Active` ones.

**BE-ROOM-018** · `StartScheduledRoomAsync` · Non-host is refused · Unit · **P0**
> **Given** a `Scheduled` room and a caller who is not the host
> **When** start is attempted
> **Then** `BadRequest("Only the host can start this room.")` and the status is unchanged.

**BE-ROOM-019** · `StartScheduledRoomAsync` · Happy path · Unit · **P0**
> **Given** a `Scheduled` room with 3 reminders set
> **When** the host starts it
> **Then** `Status = Live`, `WentLiveAt` = now, `EnsureRoomExistsAsync` is called, a host participant is
> added on stage unmuted, 3 notification rows are written, a push goes to each reminder-holder with an
> FCM token, the reminders are removed, and `room_went_live` carries
> `startPath = "scheduled_started"` with a signed `minutesFromScheduledStart`.

**BE-ROOM-020** · `StartScheduledRoomAsync` · Already-live / terminal room · Unit · **P2**
> **Given** status ∈ {Live, Ended, Cancelled}
> **When** start is attempted by the host
> **Then** the corresponding `BadRequest` and no second host participant row.

**BE-ROOM-021** · `EndRoomAsync` · Non-host is refused · Unit · **P0**
> **Given** a `Live` room and a non-host caller
> **When** end is attempted
> **Then** `BadRequest("Only the host can end this room.")`, status unchanged, `CloseRoomAsync` **not**
> called.

**BE-ROOM-022** · `EndRoomAsync` · Full teardown · Unit · **P0**
> **Given** a `Live` room with one unmuted on-stage speaker and one muted listener
> **When** the host ends it
> **Then** `Status = Ended`; `HostDisconnectedAt` cleared; every `Active`/`PendingApproval` participant
> becomes `Left` with `LeftAt` set, `IsOnStage = false`, `IsMuted = true`, `IsHandRaised = false`; the
> open mic segment is closed into `TotalSpokenSeconds`; `room_ended` (schemaVersion 2) is tracked with
> `actualDurationSeconds` measured from `WentLiveAt`; `speaking_time_logged` is emitted per speaker; and
> `CloseRoomAsync` is called **last**.

**BE-ROOM-023** · `EndRoomAsync` · LiveKit teardown failure does not fail the caller · Unit · **P0**
> **Given** `CloseRoomAsync` throws
> **When** the host ends the room
> **Then** the call still returns `Success`, the room is still `Ended` in the DB, and the failure is
> logged at Error. *(Telling a host their end failed when it succeeded is the worse outcome.)*

**BE-ROOM-024** · `EndRoomAsync` · Already ended · Unit · **P2**
> **Given** `Status = Ended`
> **When** ended again
> **Then** `BadRequest("This room has already ended.")` and `CloseRoomAsync` is not called a second time.

**BE-ROOM-025** · `EndRoomAsync` · Airtime falls back when `WentLiveAt` is null · Unit · **P2**
> **Given** a legacy room with `WentLiveAt = null` and `StartDate` 90 minutes ago
> **When** ended
> **Then** `actualDurationSeconds` is computed from `StartDate` (≈5400) rather than throwing.

**BE-ROOM-026** · `MarkHostDisconnectedAsync` · Stamps once only · Unit · **P0**
> **Given** a `Live` room whose `HostDisconnectedAt` is already set
> **When** the host's socket drops again
> **Then** `false` is returned and the original timestamp is **not** re-stamped.
> *(A flapping host must not be able to renew the grace deadline forever.)*

**BE-ROOM-027** · `MarkHostDisconnectedAsync` · Guards · Unit · **P1**
> **Given** the room is missing, the caller is not the host, or the room is not `Live`
> **When** called **Then** `false` and no write. `[Theory]` over the three cases.

**BE-ROOM-028** · `ClearHostDisconnectedAsync` · Cancels the countdown · Unit · **P0**
> **Given** a `Live` room with `HostDisconnectedAt` set and the real host reconnecting
> **When** cleared **Then** `true`, and `HostDisconnectedAt` is null. Non-host → `false`, unchanged.

**BE-ROOM-029** · `EndRoomsWithExpiredHostGraceAsync` · Ends through the real end path · Unit · **P0**
> **Given** two rooms past the cutoff
> **When** the sweep runs
> **Then** both ids are returned, and each went through `EndRoomAsync` (mic segments closed,
> `room_ended` with `endReason = "host_disconnected"`, `CloseRoomAsync` called) — not a raw status write.

**BE-ROOM-030** · `EndRoomsPastScheduledDurationAsync` · Per-room deadline · Unit · **P0**
> **Given** a 2-hour room live for 2h10m and a 3-hour room live for 2h10m, `overtimeAllowance = 15m`
> **When** the sweep runs at now
> **Then** only the 2-hour room is ended (deadline 2h15m not yet reached ⇒ it survives at 2h10m; extend
> to 2h20m for the positive case). `[Theory]` over the boundary: −1m survives, +1m ends.

**BE-ROOM-031** · `EndRoomsPastScheduledDurationAsync` · Null `WentLiveAt` is exempt · Unit · **P1**
> **Given** a candidate row with `WentLiveAt = null` slipping past the SQL filter
> **When** the sweep runs **Then** it is skipped, never ended.

**BE-ROOM-032** · `LeaveRoomCleanupAsync` · Closes the open mic segment · Unit · **P1**
> **Given** an `Active`, unmuted participant with `LastUnmutedAt` 30s ago
> **When** they disconnect
> **Then** `TotalSpokenSeconds` grows by ≈30, `LastUnmutedAt` is nulled, status → `Left` with `LeftAt`
> set, and `mic_deactivated` carries `reason = "left_or_disconnected"`.

**BE-ROOM-033** · `LeaveRoomCleanupAsync` · No-op for non-active rows · Unit · **P2**
> **Given** the participant is missing or already `Left`/`Kicked`
> **When** called **Then** nothing is written.

**BE-ROOM-034** · `GetRoomsFeedAsync` · Live shows listeners, scheduled shows reminders · Unit · **P2**
> **Given** one `Live` room with 4 active participants and one `Scheduled` room with 7 reminders
> **When** the feed is read **Then** `ListenersCount` is 4 and 7 respectively, and
> `IsReminderSetByMe` is true only for rooms where the caller holds a reminder.

**BE-ROOM-035** · `ToggleReminderAsync` · Only for scheduled rooms · Unit · **P2**
> **Given** a `Live` room
> **When** a reminder is toggled **Then** `BadRequest("You can only set reminders for scheduled rooms.")`.

**BE-ROOM-036** · `ToggleReminderAsync` · Off-path emits before the row is deleted · Unit · **P2**
> **Given** an existing reminder created 3 hours ago
> **When** toggled off **Then** the row is removed **and** `room_reminder_toggled` carries
> `enabled = false` with `heldForMinutes ≈ 180`. *(The event is the only surviving record.)*

### B.2 `AuthServices` — registration, login, tokens, deletion

`Cocorra.BLL/Services/AuthService/AuthServices.cs`

**BE-AUTH-001** · `RegisterAsync` · Happy path · Unit · **P0** `[EXISTS — extend]`
> **Given** a unique email, a valid voice file and a valid profile picture
> **When** registering
> **Then** the user is created with `Status = Pending`, `EmailConfirmed = false`, assigned the `User`
> role; an OTP email is sent; a refresh token with 7-day expiry is stored; `user_registered` and
> `voice_verification_submitted` are tracked; and a **restricted** JWT carrying
> `VerificationStatus = Pending` is returned with 201.

**BE-AUTH-002** · `RegisterAsync` · Duplicate email · Unit · **P1**
> **Given** the email already exists **When** registering **Then** `BadRequest("Email is already registered")`
> and **no** file is uploaded.

**BE-AUTH-003** · `RegisterAsync` · Voice upload failure aborts cleanly · Unit · **P0**
> **Given** `SaveVoice` returns `"Error:FakeVoice"`
> **When** registering **Then** `BadRequest` with that message, no user created, and the image is never
> uploaded.

**BE-AUTH-004** · `RegisterAsync` · Image failure deletes the orphaned voice file · Unit · **P0**
> **Given** the voice saves but `SaveImageAsync` returns `"Error:FileTooLarge"`
> **When** registering **Then** `DeleteVoice(voicePath)` is called exactly once and no user is created.

**BE-AUTH-005** · `RegisterAsync` · Mid-flight exception rolls back and deletes both files · Unit · **P0**
> **Given** `AddToRoleAsync` fails, throwing inside the transaction
> **When** registering **Then** the transaction is rolled back and **both** `DeleteVoice` and
> `DeleteImage` are called — no orphaned blobs.

**BE-AUTH-006** · `LoginAsync` · Lockout is checked before the password · Unit · **P0**
> **Given** a locked-out user and a **wrong** password
> **When** logging in **Then** `Forbidden("Account is locked or banned.")` with `lockoutEnd`, and
> `CheckPasswordAsync` is **never invoked**. *(Prevents `AccessFailedCount` growing during a lockout and
> keeps the hash path off-limits to locked accounts.)*

**BE-AUTH-007** · `LoginAsync` · Wrong password is indistinguishable from unknown email · Unit · **P0**
> **Given** (a) an unknown email, (b) a known email with a wrong password
> **When** logging in **Then** both return exactly `BadRequest("Invalid Email or Password")` — no user
> enumeration.

**BE-AUTH-008** · `LoginAsync` · Unconfirmed email blocks login · Unit · **P1**
> **Given** correct credentials but `EmailConfirmed = false`
> **When** logging in **Then** "Please confirm your email before logging in." and no token is issued.

**BE-AUTH-009** · `LoginAsync` · Pending/ReRecord get a restricted token · Unit · **P0**
> **Given** `Status ∈ {Pending, ReRecord}` with a confirmed email
> **When** logging in **Then** a JWT carrying `VerificationStatus = Pending|ReRecord` is returned, the
> device is registered, and `ResetAccessFailedCountAsync` has run.

**BE-AUTH-010** · `LoginAsync` · Banned/Rejected are refused · Unit · **P0**
> **Given** `Status ∈ {Banned, Rejected}` **When** logging in **Then** `BadRequest` with the matching
> message, `RegisterDeviceAsync` is **not** called, and no token is issued. `[Theory]`.

**BE-AUTH-011** · `LoginAsync` · Active happy path · Unit · **P0** `[EXISTS]`
> **Then** JWT with `VerificationStatus = Active`, roles, a fresh refresh token (7 days), device
> registered.

**BE-AUTH-012** · `GenerateJwtToken` · Claim contract · Unit · **P0**
> **Given** an `Active` user in roles `{User, Coach}`
> **When** a token is generated **Then** it contains `sub`, `email`, `jti`, `NameIdentifier`, `Name`,
> `profilePicture`, `VerificationStatus = Active`, one `role` claim per role, issuer/audience from
> config, HMAC-SHA256, and `ValidTo ≈ now + 1 day`.
> *(`jti` must differ across two calls — otherwise replay analysis is impossible.)*

**BE-AUTH-013** · `RefreshTokenAsync` · Expired refresh token refused · Unit · **P0**
> **Given** a stored refresh token whose `RefreshTokenExpiryTime` is in the past
> **When** refreshing **Then** `BadRequest("Invalid or expired refresh token.")` and no new JWT.

**BE-AUTH-014** · `RefreshTokenAsync` · Unknown token refused · Unit · **P0**
> **Given** a refresh token matching no user **When** refreshing **Then** the same `BadRequest`.

**BE-AUTH-015** · `RefreshTokenAsync` · Locked-out user is refused *and* burned · Unit · **P0**
> **Given** a valid unexpired refresh token belonging to a locked-out (banned) user
> **When** refreshing **Then** `Forbidden`, **and** `user.RefreshToken` is set to null so the credential
> cannot be reused.

**BE-AUTH-016** · `RefreshTokenAsync` · Rotation · Unit · **P1**
> **Given** a valid refresh for an active user
> **When** refreshing **Then** a **different** refresh token is stored, expiry is pushed to +7 days, the
> device is re-registered (keeping `LastSeenAt` current), and a new access token is returned.

**BE-AUTH-017** · `RevokeTokenAsync` · Clears both credentials · Unit · **P1**
> **Given** a user with a refresh token and an FCM token
> **When** revoking **Then** both are nulled and `RefreshTokenExpiryTime` is set to now.
> Second call → `Success("Token already revoked.")`.

**BE-AUTH-018** · `DeleteAccountAsync` · Hosted rooms end through `EndRoomAsync` · Unit · **P0**
> **Given** a user hosting one `Live` and one `Scheduled` room
> **When** the account is deleted
> **Then** `EndRoomAsync` is called for **both** with `endReason = "host_account_deleted"` — proving the
> LiveKit room is deleted, participants are flipped to `Left` and `room_ended` is emitted, none of which
> a direct status write would do.

**BE-AUTH-019** · `DeleteAccountAsync` · Restrict-FK cleanup order · Unit · **P0**
> **Given** a user with friend requests, messages, blocks and notifications
> **When** deleted **Then** `ExecuteDeleteAsync` runs over `FriendRequests`, `Messages`, `UserBlocks`
> and `Notifications` **before** `DeleteAsync(user)`, and `account_deleted` is tracked.

**BE-AUTH-020** · `DeleteAccountAsync` · Remaining FK references surface a clean error · Unit · **P1**
> **Given** `UserManager.DeleteAsync` throws `DbUpdateException` (e.g. a surviving `RoomParticipant`)
> **When** deleted **Then** "Cannot delete account due to remaining database references." rather than a
> 500.

**BE-AUTH-021** · `UpdateFcmTokenAsync` · Steals the token from stale owners · Unit · **P0**
> **Given** users A and B where B currently holds FCM token T
> **When** A registers T **Then** B's `FcmToken` is nulled and A's is set to T — one device, one
> recipient. *(Guards the misdelivery class of bug.)*

**BE-AUTH-022** · `ReRecordVoiceAsync` · Only in `ReRecord` state · Unit · **P1**
> **Given** `Status ∈ {Pending, Active, Banned}` **When** re-recording **Then** refused; **Given**
> `ReRecord` **Then** the old blob is deleted, the new path stored, status → `Pending`, and
> `voice_verification_submitted` tracked.

**BE-AUTH-023** · `ResetPasswordAsync` · Invalid OTP · Unit · **P0**
> **Given** a wrong or expired OTP **When** resetting **Then** "Invalid or expired OTP code." and the
> password is unchanged.

**BE-AUTH-024** · `ForgotPasswordAsync` · No user enumeration · Unit · **P0**
> **Given** an unregistered email **When** requesting a reset **Then** `Success` with the same generic
> message as the registered case, and **no** email is sent.

**BE-AUTH-025** · `UpdatePasswordAsync` · Wrong current password · Unit · **P1**
> **Given** an incorrect `currentPassword` **When** updating **Then** the Identity errors are surfaced
> and the password is unchanged.

**BE-AUTH-026** · `SubmitMbtiAsync` · Unknown user · Unit · **P3**
> **Given** a userId with no user **When** submitting **Then** `BadRequest("User not found.")`.

### B.3 `AdminService` — moderation

`Cocorra.BLL/Services/AdminService/AdminService.cs`

**BE-ADMIN-001** · `ChangeUserStatusAsync` → `Banned` · Unit · **P0**
> **Given** an `Active` user with an FCM token
> **When** an admin bans them
> **Then** lockout is enabled with `LockoutEnd = DateTimeOffset.MaxValue`; the voice blob is deleted and
> `VoiceVerificationPath` nulled; `RefreshToken` nulled; a `Notification` row of type `AdminWarning` is
> written; a **data-only** push (`type = account_locked`, empty title/body, `lockout_end` present) is
> sent; `FcmToken` is cleared **after** the push; and `user_status_changed` carries
> `fromStatus`/`toStatus`/`changedByAdminId`.

**BE-ADMIN-002** · `ChangeUserStatusAsync` → `Active` clears every lockout trace · Unit · **P0**
> **Given** a `Banned` user with lockout enabled and a non-zero `AccessFailedCount`
> **When** re-activated **Then** `SetLockoutEnabledAsync(false)`, `SetLockoutEndDateAsync(null)` and
> `ResetAccessFailedCountAsync` all run — no ghost-ban.

**BE-ADMIN-003** · `ChangeUserStatusAsync` → `Active` emits an idempotent activation · Unit · **P1**
> **When** activated **Then** `activation_completed` is tracked with the deterministic
> `eventKey = "activation_completed:{userId}"`, so a concurrent double-activation cannot double-count.

**BE-ADMIN-004** · `ChangeUserStatusAsync` · No-op transition rejected · Unit · **P2**
> **Given** the user is already in the requested status **When** changed **Then**
> `BadRequest("User is already {status}")` and no push, email or event.

**BE-ADMIN-005** · `ChangeUserStatusAsync` · Undefined enum value · Unit · **P1**
> **Given** `(UserStatus)99` **When** changed **Then** `BadRequest("Invalid status value.")`.

**BE-ADMIN-006** · `ChangeUserStatusAsync` · Push failure does not fail the transition · Unit · **P1**
> **Given** `SendPushNotificationAsync` throws **When** a status is changed **Then** the status change
> still returns `Success`. *(Also covers `SendVerificationEmailAsync` throwing — it is wrapped in an
> empty catch.)*

**BE-ADMIN-007** · `ChangeUserStatusAsync` → `ReRecord` · Unit · **P2**
> **Then** the voice blob is deleted, the Arabic `ReRecordTitle`/`ReRecordBody` are used for **both** the
> persisted notification and the push (`type = reRecord`), and the FCM token is **kept**.

**BE-ADMIN-008** · `BulkChangeUserStatusAsync` · Batch cap · Unit · **P1**
> **Given** 201 distinct ids **When** submitted **Then** `BadRequest("Too many users in one request…")`
> and nothing is changed. 200 → accepted.

**BE-ADMIN-009** · `BulkChangeUserStatusAsync` · Self-exclusion and de-duplication · Unit · **P0**
> **Given** ids `[A, A, adminId, B]` **When** the admin bulk-bans **Then** A is processed **once**, the
> admin's own id yields a per-item failure "You cannot change your own status.", and B succeeds.

**BE-ADMIN-010** · `BulkChangeUserStatusAsync` · Partial failure still returns 200 · Unit · **P1**
> **Given** 3 ids of which 1 does not exist **When** submitted **Then** the envelope is `Success` with
> `SucceededCount = 2`, `FailedCount = 1` and per-item messages; `isBulkOperation = true` on every
> emitted `user_status_changed`.

**BE-ADMIN-011** · `BlockDeviceAndEmailAsync` · Full hard ban · Unit · **P0**
> **Given** a user with 3 registered devices
> **When** an admin blocks by email **Then** status → `Banned`, lockout to `MaxValue`, `RefreshToken`
> and `FcmToken` nulled, `RefreshTokenExpiryTime` = now, `BlockAllDevicesForUserAsync` returns 3, and
> the result reports `DevicesBlocked = 3`.

**BE-ADMIN-012** · `BlockDeviceAndEmailAsync` · No registered devices · Unit · **P2**
> **Given** a user who only ever used a client omitting `X-Device-Id`
> **When** blocked **Then** the ban still applies and the message reads "No registered devices were
> found for this user." — the count is not silently inflated.

**BE-ADMIN-013** · `BlockDeviceAndEmailAsync` · Unknown email · Unit · **P2**
> **Then** `NotFound("User not found with the provided email.")` and nothing is mutated.

### B.4 `SupportService` — reports, moderation actions, support chat

`Cocorra.BLL/Services/SupportService/SupportService.cs`

**BE-SUP-001** · `SubmitReportAsync` · Neither target supplied · Unit · **P1**
> **Given** `ReportedUserId` and `ReportedRoomId` both null **When** submitted **Then**
> `BadRequest("You must specify a user or a room to report.")`.

**BE-SUP-002** · `SubmitReportAsync` · Happy path · Unit · **P2**
> **Then** the report is stored with `Status = "Open"`, the screenshot is uploaded when present, and
> `user_reported` is tracked with category and description.

**BE-SUP-003** · `TakeActionOnReportAsync` · `Mute24h` · Unit · **P0**
> **Given** an open report against user U with an FCM token
> **When** an admin applies `Mute24h`
> **Then** lockout is enabled with an end **24 hours from a single captured instant**; `ForceLogoutAsync`
> is invoked for U; a notification row is written; a **data-only** push (`type = account_locked`, empty
> title/body) carries `lockout_end` equal to that same instant; report → `Resolved` with `ResolvedAt`
> set; `moderation_action_taken` records the action and `hoursFromReportToAction`.

**BE-SUP-004** · `TakeActionOnReportAsync` · `BanUser` · Unit · **P0**
> **Then** `Status = Banned`, lockout +100 years, `RefreshToken` nulled and expiry set to now,
> `ForceLogoutAsync` called, notification persisted, data-only push sent, **then** `FcmToken` cleared.

**BE-SUP-005** · `TakeActionOnReportAsync` · `WarnUser` · Unit · **P2**
> **Then** an `AdminWarning` notification with the admin note (or the default text) is written, a push
> with `type = report` is sent, and the report is `Resolved`.

**BE-SUP-006** · `TakeActionOnReportAsync` · Room-only report cannot target a user · Unit · **P1**
> **Given** a report with `ReportedUserId = null` **When** `WarnUser`/`Mute24h`/`BanUser` is applied
> **Then** `BadRequest("This report has no reported user to …")` and nothing is mutated. `[Theory]`.

**BE-SUP-007** · `TakeActionOnReportAsync` · `RejectReport` · Unit · **P2**
> **Then** `Status = "Rejected"`, `StatusCode = Rejected`, `ResolvedAt` set, **no** push and **no**
> lockout.

**BE-SUP-008** · `SendMessageAsync` · Pending-chat message cap · Unit · **P1**
> **Given** an unclaimed (`Pending`) chat already holding 3 user messages
> **When** a 4th is sent **Then** `BadRequest("You have reached the maximum messages…")` and nothing is
> persisted. Boundary `[Theory]`: 2 → allowed, 3 → refused.

**BE-SUP-009** · `SendMessageAsync` · `IsFromAdmin` is server-determined · Unit · **P0**
> **Given** a user-originated message **When** persisted **Then** `IsFromAdmin = false` regardless of
> any client-supplied value — the DTO exposes no such field and none may be introduced.

**BE-SUP-010** · `SendMessageAsync` · Pending chats do not push · Unit · **P1**
> **Given** a chat with `AdminId = null` **When** a message is sent **Then** no push is attempted;
> **Given** `AdminId` set **Then** exactly one push is sent to that admin with `type = support_chat` and
> the `chatId`.

**BE-SUP-011** · `ClaimChatAsync` · Only pending chats can be claimed · Unit · **P1**
> **Given** a chat already `Active` or `Closed` **When** claimed **Then** `BadRequest("Chat is not
> pending.")`.

**BE-SUP-012** · `ClaimChatAsync` · Concurrent claim loses gracefully · Unit · **P1**
> **Given** `UpdateChatAsync` throws `DbUpdateConcurrencyException`
> **When** a second admin claims **Then** "This chat has already been claimed by another admin."

**BE-SUP-013** · `AdminReplyAsync` · Wrong admin is refused · Unit · **P0**
> **Given** a chat assigned to admin X **When** admin Y replies **Then** `BadRequest("You are not
> assigned to this chat.")` and no message row. *(Horizontal privilege check between admins.)*

**BE-SUP-014** · `AdminReplyAsync` · Happy path · Unit · **P2**
> **Then** `IsFromAdmin = true`, the result carries `UserId` for hub routing, and a push titled
> "Cocorra Support" is sent to the user.

**BE-SUP-015** · `CloseChatAsync` · Guards · Unit · **P2**
> **Given** an already-closed chat, or a different admin **When** closed **Then** the respective
> `BadRequest`; happy path sets `Status = Closed` and `ClosedAt`.

**BE-SUP-016** · `SendSupportChatPushAsync` · Never throws · Unit · **P1**
> **Given** `FindByIdAsync` or the push throws **When** a message is sent **Then** the message send
> still returns `Success` and the error is logged. *(A failed push must not fail a persisted message.)*

### B.5 `ChatService`, `FriendService`, `BlockService`, `ProfileService`

**BE-CHAT-001** · `SaveMessageAsync` · Block is enforced in both directions · Unit · **P0**
> **Given** either party has blocked the other (`IsBlockedAsync` is symmetric)
> **When** a message is sent **Then** `BadRequest("You cannot send a message due to a block.")` and
> nothing is persisted or pushed.

**BE-CHAT-002** · `SaveMessageAsync` · Self-send refused · Unit · **P2**
> **Given** `senderId == receiverId` **Then** "You cannot send messages to yourself."

**BE-CHAT-003** · `SaveMessageAsync` · Empty/whitespace content · Unit · **P2**
> **Given** `""`, `"   "`, `null` **Then** `BadRequest("Message cannot be empty.")`. `[Theory]`.

**BE-CHAT-004** · `SaveMessageAsync` · First-message flag · Unit · **P2**
> **Given** no prior message from sender to receiver **Then** `message_sent` carries
> `isFirstMessageToRecipient = true`; on the second message, `false`.

**BE-CHAT-005** · `SaveMessageAsync` · Push body is a preview, never raw content · Unit · **P0**
> **Given** content that is a JSON envelope `{"a":1}`
> **When** the message is saved **Then** the push body is `MessagePreview.ForNotificationBody(content)`,
> not the raw string. *(The OS tray prints `notification.body` verbatim when the app is closed.)*

**BE-CHAT-006** · `SaveMessageAsync` · Push failure does not fail the message · Unit · **P1**
> **Given** the push throws **Then** the message is still persisted and `Success` returned.

**BE-CHAT-007** · `GetChatHistoryAsync` · Blocked pair cannot read history · Unit · **P1**

**BE-FRIEND-001** · `SendFriendRequestAsync` · Self-request refused · Unit · **P2**

**BE-FRIEND-002** · `SendFriendRequestAsync` · Duplicate pending / already friends · Unit · **P1**
> **Given** an existing `Pending` or `Accepted` relation **Then** the matching `BadRequest` and no
> second row. *(Backed by the unique index on `(SenderId, ReceiverId)` — see BE-DB-003.)*

**BE-FRIEND-003** · `SendFriendRequestAsync` · Rejected relation is revived, not duplicated · Unit · **P1**
> **Given** a `Rejected` row **When** a new request is sent **Then** the same row is updated to
> `Pending` with the new sender/receiver orientation and a refreshed `CreatedAt`.

**BE-FRIEND-004** · `SendFriendRequestAsync` · `DbUpdateException` rolls back · Unit · **P1**
> **Given** the unique index rejects a racing insert **Then** the transaction rolls back and
> "A request is already in progress." is returned — no orphaned notification row.

**BE-FRIEND-005** · `RespondToFriendRequestAsync` · Accept path · Unit · **P2**
> **Then** status → `Accepted`, a `FriendAccept` notification is written, `friend_request_accepted`
> carries `hoursToAccept`, and a push is sent to the sender.

**BE-FRIEND-006** · `RespondToFriendRequestAsync` · Non-pending request · Unit · **P1**
> **Given** no pending request from that sender to the caller **Then** "Friend request not found or
> already processed." *(Also the authorization check — only the receiver can respond.)*

**BE-FRIEND-007** · `RemoveFriendOrCancelRequestAsync` · Pending removal deletes the notification · Unit · **P2**

**BE-BLOCK-001** · `BlockUserAsync` · Self-block refused by id **and** by email · Unit · **P1**

**BE-BLOCK-002** · `BlockUserAsync` · Blocking burns the target's session · Unit · **P0**
> **Given** a target with an active refresh token **When** blocked **Then** their `RefreshToken` is
> nulled and `RefreshTokenExpiryTime` set to now, `user_blocked` is tracked, and the block row exists.

**BE-BLOCK-003** · `BlockUserAsync` · Unknown target · Unit · **P2**
> **Given** a GUID or email matching no user **Then** `NotFound("Target user not found.")`.

**BE-BLOCK-004** · `UnblockUserAsync` · Idempotent when no block exists · Unit · **P2**

**BE-PROF-001** · `GetUserProfileAsync` · Non-friends see no bio or MBTI · Unit · **P0**
> **Given** a target who is not an accepted friend **When** the public profile is read **Then** `Bio` and
> `MBTI` are null and `IsFriend = false`; as a friend, both are populated. *(Data-minimisation control.)*

**BE-PROF-002** · `GetUserProfileAsync` · Self is redirected · Unit · **P3**
> **Given** `currentUserId == targetUserId` **Then** "Use the 'My Profile' endpoint for your own data."

**BE-PROF-003** · `UploadProfilePictureAsync` · Old blob deleted only after a successful update · Unit · **P1**
> **Given** the update succeeds **Then** the old path is deleted; **given** `UpdateAsync` fails **Then**
> the **new** blob is deleted and the old one is kept.

**BE-PROF-004** · `UpdateAvatarPresetAsync` · Preset overwrites the picture path · Unit · **P3**

### B.6 `PushNotificationService` — payload construction

`Cocorra.BLL/Services/NotificationService/PushNotificationService.cs` — see Section F for the full
push strategy. Unit-level cases: **BE-PUSH-001…010**.

### B.7 `LiveKitService` — token construction

`Cocorra.BLL/Services/LiveKit/LiveKitService.cs` — see Section E. Unit-level cases:
**BE-LIVEKIT-001…008**.

### B.8 `BlockedDevicesService` / `EventTracker` / `MessagePreview`

**BE-DEVICE-001** · `RegisterDeviceAsync` · Null device or empty id is a silent no-op · Unit · **P1**
> **Given** `device == null` (older app builds send no `X-Device-*` headers) **Then** `false` is returned
> and the repository is never called — login must not fail.

**BE-DEVICE-002** · `RegisterDeviceAsync` · Repository failure is swallowed · Unit · **P0**
> **Given** the repository throws **When** called from the login path **Then** `false` is returned, a
> warning is logged, and **no exception escapes** — device bookkeeping must never break authentication.

**BE-DEVICE-003** · `IsDeviceBlockedAsync` · Blank id is not blocked · Unit · **P1**
> **Given** `""` / `"   "` / `null` **Then** `false` — a missing header must not lock everyone out.

**BE-DEVICE-004** · `BlockDeviceAsync` · Existing unblocked row is promoted, not duplicated · Unit · **P2**
> **Given** a registry row for (user, device) with `IsBlocked = false` **Then** it is updated with
> `IsBlocked = true` and `BlockedAt`, and no second row is inserted.

**BE-DEVICE-005** · `BlockDeviceAsync` · Already-blocked row returns true unchanged · Unit · **P3**

**BE-EVENT-001** · `EventTracker.Track` · Never throws · Unit · **P0**
> **Given** a `properties` object that fails to serialize, or a null `HttpContext`
> **When** tracked **Then** no exception escapes and the caller's flow is unaffected.

**BE-EVENT-002** · `EventTracker.Track` · Drop on a full channel is counted · Unit · **P0**
> **Given** a bounded channel of capacity 1 that is already full
> **When** a second event is tracked **Then** `RecordDroppedOnEnqueue` is invoked and a warning logged —
> the drop is observable, not silent. *(Guards the `FullMode = Wait` choice; with `DropWrite`,
> `TryWrite` returns true and this test fails.)*

**BE-EVENT-003** · `DeriveDeterministicGuid` · Stable and well-formed · Unit · **P1**
> **Given** the same `eventKey` twice **Then** identical GUIDs; different keys → different GUIDs; the
> result has version nibble 5 and the RFC-4122 variant bits.

**BE-EVENT-004** · `Track` · `roomId` is promoted to the indexed column, case-insensitively · Unit · **P1**
> **Given** properties `new { roomId = guid }` and `new { RoomId = guid }` **Then** `UserEvent.RoomId` is
> populated in both cases; a non-GUID or non-object payload leaves it null without throwing.

**BE-EVENT-005** · `Track` · IP hash is salted and irreversible-shaped · Unit · **P0**
> **Given** the same IP with two different salts **Then** two different hashes; **given** no salt
> configured **Then** `IpHash` is null — never a raw IP, never an unsalted hash.

**BE-EVENT-006** · `Track` · Feature flags · Unit · **P1**
> **Given** `EnableNewEventEmission = false` **Then** `NewEventEmissionEnabled` and
> `HighFrequencyEventsEnabled` are both false even when the high-frequency flag is true — the flags are
> conjunctive by design. `[Theory]` over the four combinations.

**BE-EVENT-007** · `MessagePreview.ForNotificationBody` · JSON and long text · Unit · **P1**
> **Given** a JSON envelope **Then** a safe prose fallback; **given** a 500-character message **Then** a
> truncated preview; **given** ordinary prose **Then** it passes through.

---

## C. Integration Test Plan

Everything here runs in `Cocorra.IntegrationTests` against **real SQL Server 2022** in a
Testcontainers container, with EF migrations applied. These are behaviours that a mock cannot prove.

### C.0 Environments

| Collection | Container(s) | Reset strategy | Wall-clock budget |
| --- | --- | --- | --- |
| `Database` | `mcr.microsoft.com/mssql/server:2022-latest` | Respawn per test class | ~90s cold, ~15s warm |
| `Api` | as above + `CocorraApiFactory` | Respawn per class; new `HttpClient` per test | ~2 min |
| `Hubs` | as above + `TestServer` SignalR | as above | ~1 min |
| `LiveKitIntegration` | as above + `livekit/livekit-server` | Respawn + `DeleteRoom` teardown | ~2 min, **nightly only** |
| `Storage` | as above + `minio/minio` | bucket wiped per class | ~1 min, **nightly only** |

Shared seed for every class unless stated otherwise (see Section J for the full catalogue):
roles `Admin`/`Coach`/`User` seeded via the real `RoleSeeder`; users `ADMIN_1`, `COACH_1`,
`ACTIVE_1`, `ACTIVE_2`, `PENDING_1`, `RERECORD_1`, `BANNED_1`.

### C.1 Migrations and schema

**BE-DB-001** · Migrations apply cleanly to an empty database · Integration · **P0**
> **Environment:** empty SQL Server container. **Data:** none.
> **Then** `Database.MigrateAsync()` succeeds and `GetPendingMigrationsAsync()` returns empty.
> *(Nothing verifies this today; a broken migration is currently discovered in production.)*

**BE-DB-002** · Model matches the migrated schema · Integration · **P0**
> **Then** after migrating, the EF model produces no pending model changes — i.e. a developer who edits
> `OnModelCreating` without adding a migration fails the build.

### C.2 Constraints and uniqueness

**BE-DB-003** · `FriendRequest (SenderId, ReceiverId)` unique index · Integration · **P0**
> **Data:** two users. **Then** inserting a second row with the same ordered pair throws
> `DbUpdateException`; the reversed pair `(B, A)` is permitted (the index is ordered, so the service-level
> `GetFriendshipRelationAsync` check is what prevents reciprocal duplicates — this test pins the actual
> guarantee rather than the assumed one).

**BE-DB-004** · `BlockedDevices (ApplicationUserId, DeviceId)` filtered unique index · Integration · **P0**
> **Data:** one user. **Then** two rows with the same (user, device) pair are rejected; two rows with
> `DeviceId = NULL` are **both** accepted, because the index carries
> `HasFilter("[DeviceId] IS NOT NULL")`.
> *(SQLite ignores the filter entirely — only a SQL Server test can prove this.)*

**BE-DB-005** · `BlockedDevices.DeviceId` is deliberately **not** unique across users · Integration · **P1**
> **Data:** users A and B. **Then** both may register device `"device-shared"`, and blocking it for A
> makes `IsDeviceBlockedAsync("device-shared")` true — which is the ban-evasion control.

**BE-DB-006** · `UX_UserEvents_EventId` rejects duplicates · Integration · **P0**
> **Then** two `UserEvent` rows with the same `EventId` cannot coexist; this is what makes
> `activation_completed`'s deterministic key idempotent.

**BE-DB-007** · Composite keys · Integration · **P2**
> **Then** `RoomParticipant` is keyed on `(RoomId, UserId)`, `RoomReminder` on `(UserId, RoomId)` and
> `TopicVote` on `(UserId, TopicRequestId)` — a second insert for the same pair fails.

**BE-DB-008** · Analytics read-model grain uniqueness · Integration · **P1**
> **Then** `DailyPlatformMetrics(Date)`, `DailyRoomMetrics(Date, RoomId)`,
> `DailyHostMetrics(Date, HostId)`, `DailyFunnelMetrics(CohortDate, FunnelName, StepIndex)`,
> `DailyStateSnapshots(Date, MetricKey)` and `AggregationCheckpoints(PipelineName)` each reject a
> duplicate — the property that makes re-running aggregation safe.

**BE-DB-009** · Column-name overrides survive migration · Integration · **P2**
> **Then** `Room.Status` maps to `status`, `BaseEntity.UpdatedAt` to `UpdateAt`, and
> `ApplicationUser.CreatedAt` to `CreateAt`. *(A rename here silently breaks the existing production
> data; these are deliberate legacy mappings.)*

### C.3 Deletion and referential integrity

*(There is no soft deletion in this schema — see Context. These test the hard-delete contract.)*

**BE-DB-010** · Deleting a room cascades its participants · Integration · **P1**
> **Data:** a room with 3 participants. **Then** deleting the room removes all 3 rows
> (`DeleteBehavior.Cascade` on `RoomParticipant.Room`).

**BE-DB-011** · Deleting a user with participations is refused · Integration · **P0**
> **Data:** a user who is an `Active` participant in a room.
> **Then** `DeleteAsync` throws / fails — `RoomParticipant.User` is `Restrict`. This is precisely the
> "remaining database references" path `DeleteAccountAsync` reports.

**BE-DB-012** · Deleting a user who hosts a room is refused · Integration · **P0**
> **Then** `Room.Host` is `Restrict`, so account deletion cannot orphan a room.

**BE-DB-013** · `DeleteAccountAsync` end-to-end against real SQL Server · Integration · **P0**
> **Environment:** `Api` collection. **Data:** user U who hosts one `Live` room, is a participant in
> another host's room, has 2 friend requests, 4 messages (both directions), 1 block, 3 notifications and
> 5 `UserEvent` rows.
> **Then** the hosted room is `Ended`; the fake LiveKit records one `CloseRoomAsync`; friend requests,
> messages, blocks and notifications for U are gone; U's `UserEvent` rows survive with `UserId = NULL`
> (`DeleteBehavior.SetNull`, so anonymous analytics are preserved); and U is deleted **or** a clean
> "remaining database references" error is returned if the foreign participation still blocks it.
> *(This test is the single highest-value item in the plan — it is the only place the whole deletion
> contract is observable.)*

**BE-DB-014** · `Report.ReportedUser` is `SetNull`, `Report.Reporter` is `Restrict` · Integration · **P2**
> **Then** deleting a reported user nulls the column and preserves the report; deleting a reporter is
> refused.

### C.4 Transactions

**BE-DB-015** · `RegisterAsync` rolls back on role-assignment failure · Integration · **P0**
> **Environment:** `Api` collection with `IUploadVoice`/`IUploadImage` faked to succeed and the `User`
> role removed so `AddToRoleAsync` fails.
> **Then** no `AspNetUsers` row survives, and the fakes recorded one `DeleteVoice` and one
> `DeleteImage`. *(Proves the `CreateExecutionStrategy` + `BeginTransaction` block actually rolls back
> under a real provider — the mock-based unit test cannot.)*

**BE-DB-016** · `SendFriendRequestAsync` rolls back the notification on a unique-index violation · Integration · **P1**
> **Data:** an existing `Pending` row inserted directly, bypassing the service's pre-check.
> **Then** the racing request fails, and **no** orphaned `Notification` row remains.

**BE-DB-017** · Concurrent `RegisterDeviceAsync` from one device · Integration · **P1**
> **Data:** one user, 5 parallel calls with the same `DeviceId`.
> **Then** exactly one `BlockedDevices` row exists, all 5 calls return true, and `LastSeenAt` is
> advanced. *(Exercises the `catch (DbUpdateException)` → detach → re-read → `Touch` recovery.)*

**BE-DB-018** · `RegisterDeviceAsync` never clears an existing block · Integration · **P0**
> **Data:** a blocked (user, device) row. **When** the user logs in again with the same device
> **Then** `IsBlocked` stays true and `BlockedAt` is unchanged — only `LastSeenAt` and metadata move.

### C.5 Repository query behaviour

**BE-DB-020** · `GetActiveRoomsAsync` ordering · Integration · **P1**
> **Data:** 2 `Live` rooms (5 and 1 active participants) + 2 `Scheduled` rooms with different
> `StartDate`. **Then** Live precede Scheduled; within Live, higher active-participant count first;
> within Scheduled, earlier `StartDate` first; `Ended`/`Cancelled` are excluded; paging is stable.

**BE-DB-021** · `GetRoomsWithExpiredHostGraceAsync` filter · Integration · **P0**
> **Data:** a Live room disconnected 2 min ago, a Live room disconnected 10 s ago, an Ended room with a
> stale `HostDisconnectedAt`, a Live room with null. **Then** only the first is returned, ordered by
> `HostDisconnectedAt`, and the rows are **tracked** (the caller ends them through the same context).

**BE-DB-022** · `GetLiveRoomsStartedBeforeAsync` filter · Integration · **P0**
> **Then** only `Live` rooms with a non-null `WentLiveAt` earlier than the cutoff are returned.

**BE-DB-023** · `IsBlockedAsync` is symmetric · Integration · **P0**
> **Data:** A blocks B. **Then** `IsBlockedAsync(A,B)` and `IsBlockedAsync(B,A)` are both true.

**BE-DB-024** · `BlockAllDevicesForUserAsync` preserves prior `BlockedAt` · Integration · **P2**
> **Data:** a user with one already-blocked device (blocked yesterday) and two unblocked ones.
> **Then** the return value is 3, the two new ones get today's `BlockedAt`, and the pre-existing one
> keeps yesterday's.

**BE-DB-025** · `UnblockDeviceAsync` demotes rather than deletes · Integration · **P2**
> **Then** the rows survive with `IsBlocked = false` and `BlockedAt = null`, so device history is kept.

**BE-DB-026** · Support-chat query indexes return the right sets · Integration · **P2**
> **Then** `GetUserOpenChatAsync` returns the single non-closed chat, `GetPendingChatsAsync` is ordered
> by `CreatedAt`, `GetAdminActiveChatsAsync` is scoped to the admin, and counts match the paged lists.

**BE-DB-027** · `MessageRepository.GetRecentChatSummariesAsync` · Integration · **P2**
> **Data:** 3 conversations with differing last-message times and unread counts.
> **Then** ordering is by most recent message and unread counts are per-conversation.

**BE-DB-028** · `AnalyticsRepository` T-SQL executes on SQL Server · Integration · **P1**
> **Environment:** `Database` collection with a seeded 30-day event/room/user fixture.
> **Then** every public method on `IAnalyticsRepository` (user growth, stage funnel, supply health,
> safety & ops, social & cohort, platform health, corrections) executes without a provider exception and
> returns a well-formed DTO. *(Smoke-level: correctness of each metric is owned by the existing
> `docs/dashboard-discovery/14-metric-contracts.md` work; this proves they run at all outside SQLite.)*

### C.6 Authentication and authorization flows (real pipeline)

**BE-SEC-001** · Full registration → OTP → login → authorized call · Integration · **P0**
> **Environment:** `Api`. **Data:** none; creates its own user.
> **Steps:** `POST /Register` (multipart with a valid MP3 signature and a PNG) → read the OTP from the
> recording `IEmailService` fake → `GET /ConfirmEmail` → `POST /Login` → admin activates the user →
> `POST /Login` again → `GET /api/Profile/me` with the bearer token.
> **Then** each step returns the documented status, and the final call is 200. This is the canonical
> happy path and the smoke test for every release.

**BE-SEC-002** · Verification-stage token cannot reach full-access endpoints · Integration · **P0**
> **Data:** `PENDING_1`. **When** their restricted JWT calls `GET /api/Profile/me`, `POST /Room/Create`,
> `GET /Room/Feed` **Then** **403** on each — the `DefaultPolicy` requires
> `VerificationStatus = Active`. **And** the same token succeeds on `PUT /UpdateFcmToken` and
> `POST /ReRecordVoice` (`VerificationOnly`).

**BE-SEC-003** · `OnTokenValidated` rejects a locked-out user mid-token-life · Integration · **P0**
> **Steps:** log in as `ACTIVE_1` and keep the JWT → an admin bans them → replay the **same** JWT
> against `GET /api/Profile/me`.
> **Then** 401. *(`Program.cs:360-374` re-checks lockout on every request; this is the only revocation
> mechanism for a stateless JWT and nothing currently tests it.)*

**BE-SEC-004** · Expired JWT is rejected · Integration · **P0**
> **Given** a token minted with the real signing key but `ValidTo` in the past **Then** 401 with
> `WWW-Authenticate` indicating expiry.

**BE-SEC-005** · Wrong-signature JWT is rejected · Integration · **P0**
> **Given** a structurally valid token signed with a different key **Then** 401 — proving
> `ValidateIssuerSigningKey` is on.

**BE-SEC-006** · Issuer/audience are validated · Integration · **P0**
> **Given** tokens with a foreign `iss` or `aud` **Then** 401 each. `[Theory]`.

**BE-SEC-007** · `alg: none` and algorithm-confusion tokens are rejected · Integration · **P0**
> **Given** an unsigned token and one signed with a public-key algorithm **Then** 401.

**BE-SEC-008** · Role gates are enforced end-to-end · Integration · **P0**
> **Matrix:** `ACTIVE_1` (User), `COACH_1` (Coach), `ADMIN_1` (Admin) × representative endpoints
> `GET /Api/V1/Admin/Users` (Admin,Coach), `PUT /Api/V1/Admin/User/ChangeStatus/{id}` (Admin only),
> `GET /Api/V1/Roles/List` (Admin), `GET /Api/V1/Room/admin/history` (Admin),
> `GET /Api/V1/Analytics/Summary` (Admin,Coach), `GET /Api/V1/Analytics/System/Health` (Admin only).
> **Then** every cell matches the attribute on the action, and a Coach is **403** on the Admin-only ones.

**BE-SEC-009** · SignalR hub connections require a valid token · Integration · **P0**
> **Then** connecting to `/hubs/rooms`, `/hubs/chat`, `/hubs/support` without a token fails; with
> `?access_token=<jwt>` it succeeds (`OnMessageReceived` reads the query string for hub paths only).
> **And** the same query-string token is **ignored** on a normal REST path, so `?access_token=` cannot
> be used to authenticate `GET /api/Profile/me`.

**BE-SEC-010** · `DeviceBlockingMiddleware` blocks a banned device before the controller runs · Integration · **P0**
> **Data:** a blocked `X-Device-Id`.
> **Then** a request carrying that header returns **403** with the documented JSON body, on both an
> anonymous route (`POST /Login`) and an authorized one — and the controller action never executes.
> **And** a request with no `X-Device-Id` passes through untouched (older app builds).

### C.7 Storage and email

**BE-INT-001** · Image upload round-trip to MinIO · Integration (nightly) · **P2**
> **Environment:** `Storage` collection. **Then** `SaveImageAsync` returns a `PublicUrl`-prefixed path,
> the object exists under `Uploads/img/Profiles/`, and `DeleteImage` removes it.

**BE-INT-002** · Voice upload round-trip and magic-byte rejection · Integration (nightly) · **P1**
> **Then** a real MP3 (`ID3` header) uploads to `Uploads/Voices/`; a `.mp3`-named text file is rejected
> with `Error:FakeVoice` and **nothing is written to the bucket**.

**BE-INT-003** · S3 outage degrades to an error string, not an exception · Integration · **P1**
> **Given** an `IAmazonS3` fake that throws **Then** `SaveImageAsync` returns `"Error:ServerException"`
> and the caller (`RegisterAsync`) surfaces a 400 rather than a 500.

**BE-INT-004** · Email failure does not break registration · Integration · **P1**
> **Given** `IEmailService.SendEmailAsync` throws **Then** `POST /Register` rolls back and returns a
> 400 with a message — not an unhandled 500.

### C.8 Background services against a real database

**BE-JOB-001** · `HostReconnectGraceService.SweepAsync` ends abandoned rooms · Integration · **P0**
> **Environment:** `Api`. **Data:** room R1 `Live`, `HostDisconnectedAt` = now−120s; R2 `Live`,
> `HostDisconnectedAt` = now−10s; R3 `Live`, null; R4 `Ended` with a stale timestamp. Grace = 90s.
> **Then** one sweep ends only R1; its participants are `Left`; the fake LiveKit recorded
> `CloseRoomAsync(R1)`; `IRealTimeNotifier.RoomEndedAsync(R1, …)` fired once; R2–R4 are untouched.

**BE-JOB-002** · Grace sweep is idempotent · Integration · **P1**
> **Then** a second sweep immediately after returns 0 and emits no second `room_ended`.

**BE-JOB-003** · Host reconnect inside the window cancels the sweep · Integration · **P0**
> **Steps:** stamp `HostDisconnectedAt` → call `ClearHostDisconnectedAsync` → run the sweep past the
> deadline. **Then** the room is still `Live`.

**BE-JOB-004** · `RoomDurationLimitService.SweepAsync` respects per-room duration · Integration · **P0**
> **Data:** a 2h room live 2h20m, a 3h room live 2h20m, a 2h room live 1h, a 2h room with
> `WentLiveAt = null`. Overtime = 15m.
> **Then** only the first is ended, with `endReason = "duration_elapsed"`, and participants notified.

**BE-JOB-005** · A failing sweep cycle does not kill the loop · Integration · **P1**
> **Given** the first `SweepAsync` throws (repository forced to fail) **Then** the hosted service logs
> and the next interval still executes a successful sweep.

**BE-JOB-006** · `EventFlushService` persists a batch · Integration · **P0**
> **Then** N events written to the channel appear as N `UserEvents` rows with their properties intact
> and `RoomId` promoted.

**BE-JOB-007** · Duplicate `EventId` falls back to per-row insert · Integration · **P0**
> **Data:** a batch of 10 where 1 collides with an existing row.
> **Then** 9 rows are persisted, the duplicate is discarded and counted, and **no** dead-letter row is
> created. *(Without the fallback the whole batch would be lost — a worse failure than the one it
> replaced.)*

**BE-JOB-008** · Duplicate-key classification on SQL Server · Integration · **P0**
> **Then** `IsDuplicateKeyViolation` returns true for a genuine SQL Server unique-index violation
> (error 2601/2627) and **false** for a NOT NULL or FK violation. *(SQLite cannot produce these codes —
> `PipelineClassificationTests` currently proves only the SQLite half.)*

**BE-JOB-009** · Permanent failure dead-letters instead of dropping · Integration · **P0**
> **Given** the `UserEvents` table made un-writable mid-test **Then** after the retry budget is spent,
> a `DeadLetterEvents` row exists per event with a truncated `FailureReason`, and the metrics record
> the batch failure.

**BE-JOB-010** · Shutdown drains the channel · Integration · **P1**
> **Given** 250 buffered events and a cancellation **Then** all 250 land in `UserEvents` or
> `DeadLetterEvents` — none vanish with the process.

**BE-JOB-011** · `EventCleanupService.PerformBatchedPurgeAsync` respects retention · Integration · **P1**
> **Data:** 100 events older than `RawEventRetentionDays` and 50 newer.
> **Then** exactly 100 are deleted in bounded batches and the 50 survive.

**BE-JOB-012** · `AnalyticsAggregationService.PerformAggregationCycleAsync` is re-runnable · Integration · **P0**
> **Data:** a seeded day of events. **Then** running the cycle twice produces identical read-model rows
> (no duplication), and the `AggregationCheckpoint` advances only once past the safety lag.

**BE-JOB-013** · `StateSnapshotService.CaptureSnapshotAsync` is idempotent per (Date, MetricKey) · Integration · **P0**
> **Then** capturing twice for the same date leaves exactly one row per `ExpectedMetricKeys` entry, and
> `GetGapReportAsync` reports no gap for that date.

**BE-JOB-014** · `AnalyticsBackfillService` resumes and skips · Integration · **P2**
> **Given** a range partially covered **Then** `DatesProcessed`/`DatesSkipped` are accurate and
> `ResumeFromDate` is set when the run is cut short.

### C.9 Cross-layer flows (API → Application → Database)

**BE-FLOW-001** · Create → join → stage → speak → end · Integration · **P0**
> **Environment:** `Api` + `Hubs`. **Actors:** host H (`ACTIVE_1`), listener L (`ACTIVE_2`).
> **Steps:** H `POST /Room/Create` (Live, 2h) → L `POST /Room/{id}/Join` → both connect to `/hubs/rooms`
> and call `JoinRoom` → L `RaiseHand` → H `ApproveToStage(L)` → L `ToggleMic(false)` → wait → L
> `ToggleMic(true)` → H `POST /Room/{id}/End`.
> **Then** at each step the DB reflects the transition, L's `TotalSpokenSeconds` is > 0 after the mute,
> the fake LiveKit recorded `EnsureRoomExists`, `UpdateStagePermission(L, true)` and `CloseRoom`, both
> participants end as `Left`, and the emitted event sequence is
> `room_created, room_went_live, room_joined×2, hand_raised, stage_promoted, hand_lowered(wasApproved=true),
> mic_activated, mic_deactivated, room_ended, speaking_time_logged`.

**BE-FLOW-002** · Private room approval flow · Integration · **P0**
> **Steps:** H creates a private room → L joins (→ `PendingApproval`, host notified, **no token**) → L's
> hub `JoinRoom` is rejected with "still pending approval" → H `POST /Approve/{userId}` → L's
> `GET /Room/{id}/State` now returns a token and the hub join succeeds.

**BE-FLOW-003** · Kick removes from DB, hub and media · Integration · **P0**
> **Steps:** L is `Active` and on stage → H calls `KickUser`.
> **Then** L's row is `Kicked` with `LeftAt`; the fake LiveKit recorded `RemoveParticipant(room, L)`;
> the group received `UserKicked`; L's subsequent `POST /Room/{id}/Join` returns "You are banned from
> this room."; and L's `GET /Room/{id}/Token` is refused.

**BE-FLOW-004** · Ban force-disconnects an in-room user · Integration · **P0**
> **Steps:** L is connected to `/hubs/rooms` in a live room → admin `PUT /Admin/User/ChangeStatus/{L}`
> with `Banned`.
> **Then** L's connection receives `ForceDisconnect`; `RoomHub.GetConnectionsForUser(L)` is empty
> afterwards; L's stored `RefreshToken` is null; and L's existing JWT now 401s
> (via `OnTokenValidated` — chains with BE-SEC-003).

**BE-FLOW-005** · Host account deletion tears down the live room · Integration · **P0**
> **Steps:** H hosts a `Live` room with listener L connected → H `DELETE /Authentication/DeleteAccount`.
> **Then** the room is `Ended`; the fake LiveKit recorded `CloseRoomAsync`; L's participant row is
> `Left`; L's `GET /Room/{id}/Token` returns "This room is not live."; and `room_ended` carries
> `endReason = "host_account_deleted"`.

**BE-FLOW-006** · Support chat: user → admin claim → reply · Integration · **P1**
> **Steps:** user `POST /Support/chat/send` (chat created `Pending`, admins receive
> `NewPendingChatAlert`) → admin `POST /Support/chat/{id}/claim` (others receive `ChatClaimed`) → admin
> `POST /Support/chat/{id}/reply` (the user receives `ReceiveSupportMessage` and a push) → admin
> `POST /Support/chat/{id}/close`.
> **Then** each status transition is persisted and a second admin cannot reply to the claimed chat.

**BE-FLOW-007** · Report → `Mute24h` → login refused → lockout expiry · Integration · **P1**
> **Then** after the action, `POST /Login` for the muted user returns `Forbidden` with `lockoutEnd`;
> fast-forwarding the lockout end permits login again.

**BE-FLOW-008** · Block prevents chat in both directions · Integration · **P1**
> **Steps:** A `POST /Api/V1/Users/block/{B}` → B sends a hub `SendMessage` to A, and A to B.
> **Then** both receive `SendMessageError`; `GET /api/Chat/history/{other}` is refused for both; after
> `DELETE /Api/V1/Users/unblock/{B}`, messaging works again.

---

## D. API Test Matrix

Every endpoint below was read from the controllers and `Cocorra.DAL/AppMetaData/Router.cs`. Routes
declared in `Router` but **not implemented** by any action are listed at D.11 and are explicitly *not*
tested — nothing is invented.

All API tests run in `Cocorra.IntegrationTests/Api/` over the real pipeline.

**Case codes** (each becomes one `[Fact]`/`[Theory]`; the suffix appended to the endpoint's ID block):

| Code | Case | Default expectation |
| --- | --- | --- |
| `H` | Happy path | 200/201 + envelope `Succeeded = true` |
| `NA` | No `Authorization` header | **401** |
| `IA` | Invalid auth (bad signature, expired, wrong issuer, `VerificationStatus` missing/Pending) | **401** (invalid token) / **403** (valid token, wrong claim) |
| `FB` | Authenticated but forbidden (wrong role, not the owner/host) | **403** from the framework, or **400** from the service-level ownership check — asserted per endpoint |
| `IV` | Invalid input (bad DTO, unparseable route value, out-of-range) | **400** |
| `MR` | Missing resource | **404** or **400**, per the service's `NotFound`/`BadRequest` choice |
| `CF` | Conflict / duplicate / repeated action | documented `BadRequest` message |
| `BR` | Business-rule violation | documented `BadRequest` message |
| `DF` | Dependency failure (LiveKit / FCM / SMTP / S3 down) | endpoint still behaves sanely, never 500 |
| `BE` | Boundary / edge (paging, capacity, size limits) | clamped or refused as coded |

> **Envelope note.** Most actions return `StatusCode((int)result.StatusCode, result)`, but several
> (`RoomsController.Create/Join/Approve/GetRoomState`, `AdminController.GetUserById/ChangeStatus`,
> `RolesController.*`, `FriendsController.SearchUser/SendRequest`) collapse *every* service failure to
> **400**, including genuine not-found cases. Tests assert the **actual current** status and message.
> Where that differs from the service's intent it is recorded as a finding in D.12, not silently
> "fixed" by the test.

### D.1 `AuthenticationController` — `Api/V1/Authentication/*`

| # | Endpoint | Auth | Cases | Notes on the non-obvious cases |
| --- | --- | --- | --- | --- |
| **BE-API-AUTH-001** | `POST /Register` (multipart) | anonymous | H, IV, CF, DF, BE | IV: missing voice file, missing picture, weak password (Identity rules: ≥8, upper, lower, digit, non-alphanumeric), malformed email. CF: duplicate email. DF: SMTP throws → 400 not 500. BE: voice > 3 MB → `Error:FileTooLarge`; image > 5 MB; `.exe` renamed `.mp3` → `Error:FakeVoice` |
| **BE-API-AUTH-002** | `POST /Login` | anonymous | H, IV, BR, BE | BR: unconfirmed email, Banned, Rejected, locked-out (403 + `lockoutEnd`). BE: 5 consecutive wrong passwords → lockout for 15 min (`MaxFailedAccessAttempts = 5`) |
| **BE-API-AUTH-003** | `POST /SubmitMbti` | `[Authorize]` Active | H, NA, IA, IV | IA: a `Pending` token → 403 |
| **BE-API-AUTH-004** | `POST /ForgotPassword` | anonymous | H, IV, BE | BE: unknown email returns the *same* 200 message as a known one (no enumeration) |
| **BE-API-AUTH-005** | `PUT /UpdateFcmToken` | `VerificationOnly` | H, NA, IA, IV, CF | Token may arrive via `?fcmToken=` **or** body; both are tested, query wins. IV: neither supplied → 400 "FCM token is required." IA: an *unauthenticated* call → 401; a Banned user's token → 401 (lockout). CF: token already held by another user → stolen |
| **BE-API-AUTH-006** | `POST /ResendOtp` | anonymous | H, MR, BR | Body is a bare JSON string. MR: unknown email → 400. BR: already-confirmed email → 400 |
| **BE-API-AUTH-007** | `GET /ConfirmEmail?email=&otpCode=` | anonymous | H, IV, MR, CF | IV: wrong/expired OTP. CF: replaying a consumed OTP → rejected |
| **BE-API-AUTH-008** | `POST /ResetPassword` | anonymous | H, IV, MR, BE | BE: new password failing Identity rules → errors surfaced, old password still works |
| **BE-API-AUTH-009** | `POST /ReRecordVoice` (multipart) | `VerificationOnly` | H, NA, IA, IV, BR | **Security-critical:** the email comes from the JWT, never the body. IV: no file → 400. BR: status ≠ `ReRecord` → 400 |
| **BE-API-AUTH-010** | `PUT /UpdatePassword` | `[Authorize]` Active | H, NA, IA, IV, BR | BR: wrong current password. BE: after a change, the old refresh token behaviour is asserted (currently **not** invalidated — recorded in D.12) |
| **BE-API-AUTH-011** | `DELETE /DeleteAccount` | `[Authorize]` Active | H, NA, IA, BR, DF | See BE-DB-013 / BE-FLOW-005. DF: LiveKit down → account still deleted |
| **BE-API-AUTH-012** | `POST /RefreshToken` | anonymous | H, IV, BR, CF | BR: expired, unknown, or belonging to a locked-out user (403 + token burned). CF: replaying a rotated refresh token → 400 |
| **BE-API-AUTH-013** | `POST /RevokeToken` | `[Authorize]` Active | H, NA, IA, CF | CF: second revoke → "Token already revoked." |

### D.2 `RoomsController` — `Api/V1/Room/*` (class-level `[Authorize]`)

| # | Endpoint | Cases | Notes |
| --- | --- | --- | --- |
| **BE-API-ROOM-001** | `POST /Create` (multipart) | H, NA, IA, IV, DF, BE | IV: `DurationHours` ∉ {2,3}; `TotalCapacity` outside 2–1000; `StageCapacity` outside 1–20; `DefaultSpeakerDurationMinutes` outside 1–60; missing `Category`; title > 100 chars. DF: LiveKit `CreateRoom` fails → room still created (`EnsureRoomExistsAsync` is best-effort). BE: `ScheduledStartDate` in the past → created `Live`, not `Scheduled` |
| **BE-API-ROOM-002** | `POST /{roomId:guid}/Join` | H, NA, IA, MR, BR, CF, BE | MR: unknown roomId → 400 "Room not found." IV: non-GUID route → **404** (route constraint, not 400). BR: Scheduled/Ended/Cancelled; Kicked. CF: joining twice while Active → token re-issued, no duplicate row. BE: room at `TotalCapacity` → "Room is full." |
| **BE-API-ROOM-003** | `POST /{roomId:guid}/Approve/{userId:guid}` | H, NA, IA, FB, MR, CF | FB: non-host → 400 "Only the host can approve join requests." MR: no pending request → 400. CF: already `Active` → success, no duplicate notification |
| **BE-API-ROOM-004** | `GET /{roomId:guid}/State` | H, NA, IA, FB, MR, BR | FB: non-participant → 400 and **no token minted**. BR: room not `Live` → 400 |
| **BE-API-ROOM-005** | `GET /Feed` | H, NA, IA, BE | BE: `pageSize = 500` clamps to 50; `pageNumber = 0` clamps to 1; `pageNumber = 9999` → empty list not error; unknown `categoryId` enum value → 400 from model binding |
| **BE-API-ROOM-006** | `POST /{roomId:guid}/toggle-reminder` | H, NA, IA, MR, BR, CF | BR: room not `Scheduled`. CF: toggle twice → set then removed, idempotent per call |
| **BE-API-ROOM-007** | `POST /{roomId:guid}/Start` | H, NA, IA, FB, MR, BR, DF | FB: non-host. BR: already Live / Ended / Cancelled. DF: FCM down → room still goes live and reminders still cleared |
| **BE-API-ROOM-008** | `POST /{roomId:guid}/End` | H, NA, IA, FB, MR, BR, DF | FB: non-host. BR: already ended. DF: LiveKit `DeleteRoom` throws → still 200 |
| **BE-API-ROOM-009** | `GET /{roomId:guid}/Token` | H, NA, IA, FB, MR, BR | Delegates to `GetRoomStateAsync`; response shape is the trimmed `{LiveKitToken, LiveKitServerUrl, IceServers}`. **Security-critical:** non-participant and non-live both yield no token |
| **BE-API-ROOM-010** | `GET /admin/history` | H, NA, IA, FB, BE | FB: `ACTIVE_1` and `COACH_1` → 403 (Admin only). BE: paging clamps identical to Feed |

### D.3 `AdminController` — `Api/V1/Admin/*` (class `[Authorize(Roles = "Admin,Coach")]`)

| # | Endpoint | Cases | Notes |
| --- | --- | --- | --- |
| **BE-API-ADMIN-001** | `GET /Users` | H, NA, IA, FB, BE | Coach **allowed**. FB: `ACTIVE_1` → 403. BE: `search` matching nothing → empty page with correct `totalCount`; `pageSize = 0`/negative behaviour asserted as-coded (no clamp in this action — recorded in D.12) |
| **BE-API-ADMIN-002** | `GET /User/{id}` | H, NA, IA, FB, MR | MR: unknown id → **400** "User not found" (not 404) |
| **BE-API-ADMIN-003** | `PUT /User/ChangeStatus/{id}` | H, NA, IA, FB, IV, MR, CF, BR | FB: Coach → 403 (`Authorize(Roles="Admin")` on the action). BR: admin changing **their own** status → 400. IV: `(UserStatus)99` → 400. CF: same status → 400. H also asserts the `ForceDisconnect` fan-out for Banned/Rejected |
| **BE-API-ADMIN-004** | `PUT /Users/BulkChangeStatus` | H, NA, IA, FB, IV, BE | IV: empty `UserIds` → 400; invalid status → 400. BE: 201 ids → 400; 200 ids → accepted; duplicates de-duplicated; self-id yields a per-item failure while the rest succeed (200 overall) |
| **BE-API-ADMIN-005** | `GET /Dashboard/Stats` | H, NA, IA, FB, BE | Coach allowed. BE: empty database → all zeros, not a 500 |
| **BE-API-ADMIN-006** | `POST /BlockDeviceAndEmail` | H, NA, IA, FB, IV, MR, BR | FB: Coach → 403. BR: admin targeting their **own** email → 400. MR: unknown email → 404. H asserts devices blocked + `ForceDisconnect` |

### D.4 `RolesController` — `Api/V1/Roles/*` (class `[Authorize(Roles = "Admin")]`)

| # | Endpoint | Cases | Notes |
| --- | --- | --- | --- |
| **BE-API-ROLE-001** | `GET /List` | H, NA, IA, FB | FB: Coach **and** User → 403 |
| **BE-API-ROLE-002** | `GET /{id}` | H, NA, IA, FB, MR | MR: unknown role id → 400 |
| **BE-API-ROLE-003** | `POST /ManageUser` | H, NA, IA, FB, IV, MR, BR, CF | **BR (P0):** any attempt to assign `"Admin"` (any casing) → 400 "Cannot assign the Admin role through this endpoint." IV: non-existent role name → 400. MR: unknown user → 400. CF: submitting the user's current roles → 400 "No changes detected." |
| **BE-API-ROLE-004** | `GET /Users/{roleName}` | H, NA, IA, FB, MR | MR: unknown role → 400 |

### D.5 `ProfileController` — `api/Profile/*` + `Api/V1/Profile/update-avatar-preset`

| # | Endpoint | Cases | Notes |
| --- | --- | --- | --- |
| **BE-API-PROF-001** | `GET /api/Profile/me` | H, NA, IA | IA: Pending token → 403 |
| **BE-API-PROF-002** | `GET /api/Profile/{targetUserId:guid}` | H, NA, IA, MR, BR, BE | BR: own id → 400. BE (**P0**): non-friend response must omit `Bio` and `MBTI` |
| **BE-API-PROF-003** | `PUT /api/Profile/update` | H, NA, IA, IV | IV: `FirstName`/`LastName` over 50 chars or empty → 400 |
| **BE-API-PROF-004** | `POST /api/Profile/upload-picture` | H, NA, IA, IV, DF, BE | IV: no file → 400. BE: 6 MB file → `Error:FileTooLarge`; `.png`-named text file → `Error:FakeImage`; `.gif` → `Error:InvalidExtension` (allowed set is jpg/jpeg/png only). DF: S3 throws → 400 and the **old** picture is retained |
| **BE-API-PROF-005** | `PUT /Api/V1/Profile/update-avatar-preset` | H, NA, IA, IV | IV: missing/blank `AvatarPresetKey` → 400 |

### D.6 `FriendsController` — `api/Friends/*`

| # | Endpoint | Cases | Notes |
| --- | --- | --- | --- |
| **BE-API-FRIEND-001** | `GET /search/{targetId:guid}` | H, NA, IA, MR, BE | BE: status string is `None` / `RequestSent` / `RequestReceived` / `Friends` depending on direction — `[Theory]` over all four |
| **BE-API-FRIEND-002** | `POST /send-request` | H, NA, IA, IV, MR, CF, BR | BR: self-request. CF: duplicate pending, already friends. IV: missing `TargetUserId` |
| **BE-API-FRIEND-003** | `POST /respond-request/{senderId:guid}?accept=` | H, NA, IA, FB, MR, CF | FB: a third party cannot accept someone else's request (there is no pending request *to them*, so it surfaces as 400 — asserted as-is). `[Theory]` accept=true/false. CF: responding twice |
| **BE-API-FRIEND-004** | `DELETE /remove/{targetId:guid}` | H, NA, IA, MR, CF | H covers both *unfriend* and *cancel pending*. CF: second call → 400 |

### D.7 `ChatController` / `NotificationsController` / `BlockController`

| # | Endpoint | Cases | Notes |
| --- | --- | --- | --- |
| **BE-API-CHAT-001** | `GET /api/Chat/friends-list` | H, NA, IA, BE | BE: `pageSize = 500` clamps to 100; `pageNumber = 0` clamps to 1 |
| **BE-API-CHAT-002** | `GET /api/Chat/history/{friendId:guid}` | H, NA, IA, FB, MR, BE | FB (**P0**): blocked pair → 400, no messages leak. BE: pageSize clamp at 100 |
| **BE-API-CHAT-003** | `PUT /api/Chat/mark-read/{friendId:guid}` | H, NA, IA, BE | BE: no unread messages → still 200, zero rows updated |
| **BE-API-NOTIF-001** | `GET /api/Notifications/my-notifications` | H, NA, IA, BE | BE: clamps at 100; ordered by `CreatedAt` descending |
| **BE-API-NOTIF-002** | `PUT /api/Notifications/read-notification/{id:guid}` | H, NA, IA, FB, MR | **FB (P0 — IDOR):** another user's notification id → 404 "Notification not found." and the row stays unread. The `ExecuteUpdateAsync` is scoped by `UserId` — this test pins that |
| **BE-API-NOTIF-003** | `PUT /api/Notifications/mark-all-read` | H, NA, IA, BE | BE: only the caller's rows change; another user's unread rows are untouched |
| **BE-API-BLOCK-001** | `POST /Api/V1/Users/block/{target}` | H, NA, IA, IV, MR, CF, BR | `{target}` accepts a GUID **or** an email — `[Theory]` over both. BR: self-block by id and by email. CF: blocking twice → success, one row |
| **BE-API-BLOCK-002** | `DELETE /Api/V1/Users/unblock/{target}` | H, NA, IA, MR, CF | CF: unblocking someone never blocked → success, no row |

### D.8 `SupportController` — `Api/V1/Support/*`

| # | Endpoint | Auth | Cases | Notes |
| --- | --- | --- | --- | --- |
| **BE-API-SUP-001** | `POST /Ticket` (multipart) | `[AllowAnonymous]` | H, IV, BE | H twice: anonymous (`UserId` null) **and** authenticated (`UserId` bound from the JWT). BE: oversized screenshot |
| **BE-API-SUP-002** | `POST /Report` (multipart) | `[Authorize]` | H, NA, IA, IV, BR | BR: neither `ReportedUserId` nor `ReportedRoomId` → 400 |
| **BE-API-SUP-003** | `GET /admin/reports` | Admin | H, NA, IA, FB, BE | BE: filter by `category` and by `status`, and by both; unknown category enum → 400 |
| **BE-API-SUP-004** | `PUT /admin/reports/{id:guid}/status` | Admin | H, NA, IA, FB, IV, MR | MR: unknown report → 404 |
| **BE-API-SUP-005** | `POST /admin/reports/{id:guid}/action` | Admin | H, NA, IA, FB, IV, MR, BR | H is a `[Theory]` over `WarnUser`, `Mute24h`, `BanUser`, `RejectReport`. BR: user-targeting actions on a room-only report → 400. IV: undefined action enum → 400 |
| **BE-API-SUP-006** | `POST /chat/send` | `[Authorize]` | H, NA, IA, IV, BR | BR: 4th message on a Pending chat → 400. IV: empty `Content` |
| **BE-API-SUP-007** | `POST /chat/{chatId:guid}/claim` | Admin | H, NA, IA, FB, MR, CF | CF: claiming an already-Active chat → 400 |
| **BE-API-SUP-008** | `POST /chat/{chatId:guid}/reply` | Admin | H, NA, IA, FB, MR, BR | **FB (P0):** an admin who did not claim the chat → 400 "You are not assigned to this chat." BR: chat not Active |
| **BE-API-SUP-009** | `POST /chat/{chatId:guid}/close` | Admin | H, NA, IA, FB, MR, CF | CF: already closed → 400 |
| **BE-API-SUP-010** | `GET /chat/pending` | Admin | H, NA, IA, FB, BE | BE: `pageSize` clamped to 1–50, `pageNumber` ≥ 1 |
| **BE-API-SUP-011** | `GET /chat/active` | Admin | H, NA, IA, FB, BE | BE: scoped to the calling admin — another admin's active chats are absent |
| **BE-API-SUP-012** | `GET /chat/history` | `[Authorize]` | H, NA, IA, BE | BE: scoped to the caller; another user's chats are absent |
| **BE-API-SUP-013** | `GET /chat/my-chat` | `[Authorize]` | H, NA, IA, MR | MR: no open chat → 404 |

### D.9 `EventsController`, `AnalyticsController`, `LiveKitWebhookController`

| # | Endpoint | Cases | Notes |
| --- | --- | --- | --- |
| **BE-API-EVENT-001** | `POST /api/events/track` | H, NA, IA, IV, BR | H: `[Theory]` over the three allowed types — `room_create_started`, `notification_opened`, `feature_viewed`. **BR (P0):** any server-owned type (`activation_completed`, `user_registered`, `room_created`, `room_ended`) → 400 "EventType is not permitted from clients." IV: null body or blank `EventType` → 400 |
| **BE-API-ANL-001** | 30 × `GET /Api/V1/Analytics/*` | H, NA, IA, FB, IV, BE | One parameterised suite over every route in `Router.AnalyticsRouting`. **H:** 200 with a well-formed DTO against the seeded analytics fixture. **FB:** `ACTIVE_1` → 403 on all; `COACH_1` → 403 on the six Admin-only routes (`Safety/ReportRate`, `Review/Latency`, `Support`, `Mbti/Dichotomies`, `System/Health`, `Metrics/Registry`, `System/Backfill`) and 200 on the rest. **IV:** `from` later than `to`; unparseable dates; negative `limit`/`weeks`. **BE:** an empty database returns zeroed DTOs, never a 500 |
| **BE-API-ANL-002** | `POST /Api/V1/Analytics/System/Backfill` | H, NA, IA, FB, IV, BE | Admin only. IV: reversed range. BE: `force = false` skips covered dates; `force = true` reprocesses; a huge range is bounded rather than hanging |
| **BE-API-LKWH-001** | `POST /Api/V1/Webhooks/LiveKit` | see Section E | Signature-authenticated, `[AllowAnonymous]` — full case list in E.4 |

### D.10 SignalR hubs (functional, over `TestServer`)

| # | Hub method | Cases | Notes |
| --- | --- | --- | --- |
| **BE-HUB-001** | `RoomHub.JoinRoom` | H, NA, IA, MR, BR, BE | BR `[Theory]`: room not Live, no participant row, `PendingApproval`, `Kicked`, `Rejected` — each throws the documented `HubException` **before** any token is generated, and each emits the matching `operation_failed` reason (`room_not_live`, `not_a_participant`, `pending_host_approval`, `blocked_from_room`). H: caller receives `LiveKitToken` with `ServerUrl` + `IceServers`; the group receives `UserJoined` |
| **BE-HUB-002** | `RoomHub.JoinRoom` rejoin semantics | H, BE | A `Left` participant is re-activated: `JoinedAt` **unchanged**, `LastJoinedAt` set, `RejoinCount + 1`, `LeftAt` cleared, and `room_joined` carries `isRejoin = true` |
| **BE-HUB-003** | `RoomHub.JoinRoom` replaces a stale connection | BE | A second connection for the same (user, room) removes the first from the group and the tracking map |
| **BE-HUB-004** | `RoomHub.OnDisconnectedAsync` — host | H | Grace stamped once, `HostDisconnected` broadcast with an absolute `ReconnectDeadline`, host's row → `Left`, room stays **Live** |
| **BE-HUB-005** | `RoomHub.OnDisconnectedAsync` — participant | H | Row → `Left`, group receives `UserLeft`, room unaffected |
| **BE-HUB-006** | `RoomHub` host reconnect | H | `HostReconnected` broadcast, `HostDisconnectedAt` cleared |
| **BE-HUB-007** | `RoomHub.RaiseHand` / `LowerHand` | H, FB, BE | FB: non-participant → `HubException`. BE: an on-stage user's raise is a no-op; a repeat raise still emits `hand_raised` with `wasAlreadyRaised = true` |
| **BE-HUB-008** | `RoomHub.ApproveToStage` | H, FB, BR, BE | **FB (P0):** non-host → "Only the host can approve speakers to the stage." BR: stage at `StageCapacity` → `HubException` **and** an `operation_failed` with `stage_at_capacity` carrying occupancy. H: `UpdateStagePermissionAsync(target, true)` called, `StageUpdated` broadcast, participant starts **muted** |
| **BE-HUB-009** | `RoomHub.MoveToAudience` | H, FB, BE | FB: non-host. H: mic segment closed if unmuted, `UpdateStagePermissionAsync(target, false)`, both `StageUpdated` and `MicStatusChanged` broadcast |
| **BE-HUB-010** | `RoomHub.ToggleMic` | H, FB, BR, BE | FB: not on stage → silent no-op. **BR (P0):** a non-host whose `TotalSpokenSeconds` exceeds `(DefaultSpeakerDurationMinutes + ExtraMinutesGranted) × 60` → `HubException` and `speaker_time_exhausted`; the **host is exempt**. BE: unmute→mute accumulates the segment and `RemainingSeconds` never goes below 0 |
| **BE-HUB-011** | `RoomHub.GrantExtraTime` | H, FB, IV | FB: non-host. IV `[Theory]`: 0, −5, 31 → `HubException`; 1 and 30 accepted |
| **BE-HUB-012** | `RoomHub.KickUser` | H, FB, BR | FB: non-host. BR: host kicking themselves → `HubException`. H: row → `Kicked` with `LeftAt`, `RemoveParticipantAsync` called, connection removed from the group, `UserKicked` broadcast, `participant_kicked` emitted |
| **BE-HUB-013** | `RoomHub.KickUser` LiveKit failure | DF | `RemoveParticipantAsync` throws → the kick still commits and the host is not told it failed |
| **BE-HUB-014** | `RoomHub.EndRoom` | H, FB | FB: non-host → `HubException` from the service message. H: `RoomEnded` broadcast and room connections purged |
| **BE-HUB-015** | `RoomHub.SendRoomGroupMessage` | H, FB, IV | FB: non-`Active` participant → `SendMessageError`, message not broadcast. IV: empty content. **BE:** group messages deliberately **ignore** `UserBlock` — a blocked pair still both receive it |
| **BE-HUB-016** | `RoomHub.SendRoomPrivateMessage` | H, FB, IV | **FB (P0):** blocked pair → `SendMessageError`, nothing persisted. H: persisted via `ChatService`, `ReceivePrivateMessage` to the target and `PrivateMessageSent` to the caller |
| **BE-HUB-017** | `ChatHub.SendMessage` | H, FB, IV | IV: unparseable receiver id → `SendMessageError`. FB: blocked pair. H: `ReceiveMessage` to the target, `MessageSent` to the caller |
| **BE-HUB-018** | `SupportHub.OnConnectedAsync` | H, FB | An Admin lands in the `Admins` group and receives `NewPendingChatAlert`; a non-admin does **not** |

### D.11 Declared-but-unimplemented routes (explicitly out of scope)

These constants exist in `Router.cs` with **no matching controller action**. No tests are written for
them; they are listed so the gap is visible rather than mistaken for missing coverage:

- `AdminRouting.Update` (`PUT /Admin/User/{id}`), `AdminRouting.Delete` (`DELETE /Admin/User/{id}`),
  `AdminRouting.ResetPassword` (`POST /Admin/ResetPassword/{id}`)
- `RolesRouting.Create`, `RolesRouting.Update`, `RolesRouting.Delete`

**BE-API-000** · Route-inventory guard · Integration · **P2**
> Enumerate `EndpointDataSource` at startup and assert the live route set equals a checked-in expected
> list. A new endpoint added without a test entry fails this test — which is how the matrix stays
> current instead of drifting.

### D.12 Behaviours the matrix pins as-is (findings, not test failures)

Recorded during the audit; tests assert **current** behaviour so a deliberate change is a conscious
test edit rather than a silent break:

1. `NotFound` responses from several services are flattened to HTTP 400 by `return BadRequest(result)`
   in `RoomsController`, `AdminController`, `RolesController`, `FriendsController`.
2. `AdminController.GetAllUsers` does not clamp `page`/`pageSize`, unlike every other paged endpoint.
3. `UpdatePasswordAsync` does not invalidate the existing refresh token, so a password change does not
   end other sessions.
4. `RoomsController.Create` returns 400 for **every** failure, including a would-be 500.

---

## E. LiveKit Test Plan

LiveKit is a **real-time external dependency whose credential we cannot revoke**. A LiveKit access
token is a stateless signed JWT: nothing on the media server records that it was issued, so a kicked
user holds a working credential until it expires. The system has three defences, and each needs a
different kind of test:

1. **Issuance discipline** — never mint a token for someone who should not have one (unit).
2. **`participant_joined` webhook enforcement** — the database overrules the token (integration).
3. **Token TTL + room empty-timeout** — the bounded fallback if the webhook feed fails (config guard).

### E.1 Tier A — mocks and fakes (`Cocorra.Tests`, every PR)

Token *construction* is deterministic and offline: `AccessToken.ToJwt()` needs no server. These run
against the real `LiveKitService` with test credentials, decoding the JWT to assert the grants.

**BE-LIVEKIT-001** · Token carries the correct identity and room · Unit · **P0** `[EXISTS — extend]`
> **Given** roomId R, userId U, name N
> **When** `GenerateToken(R, U, N, canPublish)` is called
> **Then** the decoded JWT has `sub = U`, `name = N`, `video.room = R.ToString()`, `video.roomJoin = true`
> and `video.canSubscribe = true`.

**BE-LIVEKIT-002** · `canPublish` is honoured in both directions · Unit · **P0**
> **Given** `canPublish = true` then `false`
> **Then** `video.canPublish` matches exactly. *(A listener token that can publish is an open
> microphone for someone who was never given the stage.)*

**BE-LIVEKIT-003** · Token TTL comes from configuration · Unit · **P0**
> **Given** `TokenTtlMinutes = 240` **Then** `exp − iat ≈ 240 min` (±60 s).
> **Given** `TokenTtlMinutes = 0` or negative **Then** the floor of 1 minute is applied
> (`Math.Max(1, …)`), so a misconfiguration cannot mint an already-expired token.

**BE-LIVEKIT-004** · Token signature is verifiable with the configured secret · Unit · **P0**
> **Then** the JWT validates against `ApiSecret` and **fails** validation against a different secret.

**BE-LIVEKIT-005** · Two tokens for the same user differ · Unit · **P2**
> **Then** consecutive calls produce different `iat`/`exp`, so a fresh token is genuinely fresh.

**BE-LIVEKIT-006** · `ToHttpHost` scheme translation · Unit · **P1**
> `wss://host` → `https://host`; `ws://host` → `http://host`; `https://host` unchanged. `[Theory]`.

**BE-LIVEKIT-007** · Audit logging never affects issuance · Unit · **P1**
> **Given** a logger that throws on every call, and a `null` logger
> **When** a token is generated **Then** a valid token is still returned and no exception escapes.

**BE-LIVEKIT-008** · `EnsureRoomExistsAsync` swallows failures by contract · Unit · **P0**
> **Given** the room-service client throws **Then** `false` is returned, **not** an exception — a media
> hiccup must never block a host going live while `auto_create` is on.
> **And** `emptyTimeout` is floored at 60 s (`Math.Max(60, …)`).

**BE-LIVEKIT-009** · `CloseRoomAsync` / `RemoveParticipantAsync` rethrow · Unit · **P1**
> **Then** both propagate exceptions — the *callers* decide, and both call sites already log and
> swallow deliberately (BE-ROOM-023, BE-HUB-013).

**Fake used everywhere else.** `FakeLiveKitService` implements `ILiveKitService`, mints a
structurally valid JWT, and records every call as `(Method, RoomId, UserId, CanPublish)`. Every
service, hub and API test asserts against that call log — that is how BE-ROOM-014, BE-FLOW-003 and
BE-FLOW-005 prove "no token was issued" and "the room was really torn down".

### E.2 Tier B — integration against a test LiveKit instance

**Environment:** `livekit/livekit-server` container (the repo already carries
`livekit/docker-compose.livekit.yml` and `livekit/livekit.yaml`) on a test API key/secret, with
`auto_create` explicitly **disabled** so the hardening is observable. Collection:
`LiveKitIntegration`. **Nightly and pre-release only** — not on every PR.

**BE-LIVEKIT-020** · Room lifecycle round-trip · Integration · **P1**
> **Environment:** LiveKit container. **Data:** a fresh room GUID.
> **Then** `EnsureRoomExistsAsync` creates it (`ListRooms` shows it with the configured
> `EmptyTimeout`); calling it again is idempotent and still returns `true`; `CloseRoomAsync` removes it;
> `ListRooms` no longer shows it.

**BE-LIVEKIT-021** · Issued token actually connects · Integration · **P1**
> **Then** a token from `GenerateToken` lets a headless client join the room, and the server reports
> the participant identity as the Cocorra user GUID — the correlation the webhook and analytics rely on.

**BE-LIVEKIT-022** · Publish grant is enforced by the server · Integration · **P1**
> **Then** a `canPublish = false` token cannot publish a track; after
> `UpdateStagePermissionAsync(canPublish: true)` the same **connected** participant can — proving stage
> promotion does not require a reconnect.

**BE-LIVEKIT-023** · `RemoveParticipantAsync` disconnects one participant only · Integration · **P0**
> **Given** two connected participants **When** one is removed **Then** that connection drops and the
> other stays connected with the room still alive.

**BE-LIVEKIT-024** · `CloseRoomAsync` disconnects everyone · Integration · **P0**
> **Given** two connected participants **When** the room is deleted **Then** both are disconnected.

**BE-LIVEKIT-025** · Expired token is refused at connect time · Integration · **P0**
> **Given** a token minted with `TokenTtlMinutes = 1`, held until it expires
> **Then** the connection attempt is refused. *(Establishes the outer bound on an unrevokable
> credential.)*

**BE-LIVEKIT-026** · With `auto_create` off, a token for a deleted room is refused · Integration · **P0**
> **Steps:** create the room → mint a token → `CloseRoomAsync` → connect with the still-valid token.
> **Then** the connection is refused and the room is **not** resurrected.
> *(This is the whole justification for `EnsureRoomExistsAsync`; it is the test that tells the team
> whether `auto_create` can safely be turned off in production.)*

**BE-LIVEKIT-027** · `RoomEmptyTimeoutMinutes` outlasts the longest bookable room · Integration · **P1**
> **Then** a room created with the configured empty timeout survives a quiet period longer than
> `3h + RoomOvertimeGraceMinutes`. Paired with the static guard BE-LIVEKIT-031.

**BE-LIVEKIT-028** · Webhook is really emitted and really verified · Integration · **P0**
> **Steps:** point the LiveKit container's webhook URL at the `TestServer`; connect a participant.
> **Then** `POST /Api/V1/Webhooks/LiveKit` receives a `participant_joined` event whose signature the
> `WebhookReceiver` accepts, and a `media_session_event` is tracked.

### E.3 Token-refresh and configuration guards

**BE-LIVEKIT-030** · Refresh path yields a usable token mid-session · Integration · **P0**
> **Given** an `Active` participant in a `Live` room
> **When** `GET /Api/V1/Room/{roomId}/Token` is called (the client's documented recovery after an
> unexpected disconnect)
> **Then** a **new** token is returned with a fresh `exp`, and the previously issued token is unaffected.
> **And** the same call for an `Ended` room or a non-participant returns no token at all.

**BE-LIVEKIT-031** · Configuration invariant guard · Unit · **P0**
> **Given** the bound `LiveKitSettings` and `RoomLifecycleSettings` from `appsettings.json`
> **Then** `TokenTtlMinutes ≥ (3 × 60) + RoomOvertimeGraceMinutes` and
> `RoomEmptyTimeoutMinutes ≥ (3 × 60) + RoomOvertimeGraceMinutes`.
> *(A static test, no container. If someone shortens the TTL to 60 minutes, participants in a 3-hour
> room silently lose the ability to reconnect — this fails the build instead.)*

**BE-LIVEKIT-032** · `IceServers` reach the client on every credential path · Integration · **P1**
> **Then** `POST /Join`, `GET /State`, `GET /Token` and the hub's `LiveKitToken` message all carry the
> configured STUN **and** TURN entries. *(Without TURN, users behind symmetric NAT get a token that can
> never connect — a failure that is invisible server-side.)*

### E.4 `LiveKitWebhookController` — enforcement (integration, every PR, faked signature)

`Cocorra.API/Controllers/LiveKitWebhookController.cs`

**BE-LIVEKIT-040** · Unsigned / wrongly-signed body is rejected · Integration · **P0**
> **Given** no `Authorization` header, a header signed with a different secret, or a valid signature
> over a **different** body (checksum mismatch)
> **Then** **401** in each case, and no event is tracked and no participant evicted.
> *(The endpoint is `[AllowAnonymous]`; the signature *is* the authentication. Without this, anyone
> could disconnect participants at will.)* `[Theory]`.

**BE-LIVEKIT-041** · Empty body · Integration · **P2** → 400.

**BE-LIVEKIT-042** · Kicked participant reconnecting is evicted · Integration · **P0**
> **Given** a `Live` room and a participant whose row is `Kicked`
> **When** a correctly signed `participant_joined` arrives for them
> **Then** `RemoveParticipantAsync(room, user)` is called and a warning naming
> `participant_kicked` is logged.

**BE-LIVEKIT-043** · Eviction reason matrix · Integration · **P0**
> `[Theory]`: room missing → `room_not_found`; room `Ended`/`Cancelled`/`Scheduled` → `room_{status}`;
> no participant row → `not_a_participant`; participant `Left`/`Rejected`/`PendingApproval` →
> `participant_{status}`. Each evicts. An `Active` participant in a `Live` room is **not** evicted.

**BE-LIVEKIT-044** · Enforcement runs regardless of the analytics flag · Integration · **P0**
> **Given** `Analytics:EnableNewEventEmission = false`
> **When** a `participant_joined` for a kicked user arrives
> **Then** the eviction **still happens** and the response is 200 (acknowledged so LiveKit does not
> retry). *(Gating a security control behind a reporting flag is the exact failure this pins.)*

**BE-LIVEKIT-045** · Database failure during enforcement does not 500 · Integration · **P1**
> **Given** the repository throws **Then** the endpoint returns 200, the error is logged, and no retry
> storm is invited against an already-struggling database.

**BE-LIVEKIT-046** · Telemetry payload · Integration · **P2**
> **Given** the flag on and a `participant_left` with a `DisconnectReason`
> **Then** `media_session_event` is tracked with `roomId`, `livekitEvent`, `disconnectReason` and
> `trackType`, and `UserEvent.RoomId` is populated from the room name.

**BE-LIVEKIT-047** · Unparseable room name / identity · Integration · **P2**
> **Given** a non-GUID `room.name` or `participant.identity` **Then** no enforcement is attempted, the
> event is still tracked with nulls, and the response is 200.

### E.5 Tier C — manual, requires the real mobile client

Covered in Section H: **MQ-LK-001…012**. These are the behaviours no backend test can reach —
actual audio, real network transitions, OS-level backgrounding, and whether the Flutter client
re-fetches a token after a drop.

---

## F. Firebase / Push Notification Test Plan

`FirebaseAdmin` is initialised only when `firebase-config.json` exists, and
`FirebaseMessaging.DefaultInstance` returns **null** (it does not throw) when it was never created —
which the service already guards. Tests therefore **never initialise Firebase**; the automated tier
proves *what payload would be sent*, and delivery behaviour on a handset is manual.

### F.1 Payload construction (unit — `Cocorra.Tests`)

`PushNotificationService` exposes `BuildDisplayBody` via `InternalsVisibleTo("Cocorra.Tests")`; the
rest is asserted by capturing the `Message` handed to Firebase (extract a seam or assert via the
recording fake used at integration level).

**BE-PUSH-001** · Alert push shape · Unit · **P0**
> **Given** a non-empty title and body
> **Then** `message.Notification` is attached with that title/body; `Android.Priority = High`;
> `Apns.Headers["apns-push-type"] = "alert"` and `["apns-priority"] = "10"`;
> `Aps.ContentAvailable = false`; and `Data` additionally carries `title` and `body`.

**BE-PUSH-002** · Data-only push shape · Unit · **P0**
> **Given** empty title **and** empty body (the `account_locked` / `account_rejected` contract)
> **Then** `message.Notification` is **null**; `apns-push-type = "background"`; `apns-priority = "5"`;
> `Aps.ContentAvailable = true`; `Data` does **not** gain `title`/`body` keys.
> *(Firebase treats *any* `Notification` object — even with empty strings — as a display notification,
> which produces blank pop-ups on Android 13+ and blocks silent handling on iOS.)*

**BE-PUSH-003** · Partial alert still counts as an alert · Unit · **P1**
> **Given** a title with an empty body, and an empty title with a body
> **Then** both are treated as alerts (`hasAlert` is a logical OR). `[Theory]`.

**BE-PUSH-004** · Caller's dictionary is never mutated · Unit · **P0**
> **Given** one `Dictionary<string,string>` reused across three recipients (exactly what
> `StartScheduledRoomAsync`'s reminder loop does)
> **When** three pushes are sent **Then** the caller's dictionary still has its original key set — the
> `title`/`body` mirror does not leak between sends.

**BE-PUSH-005** · Serialized payload never reaches the tray · Unit · **P0** `[EXISTS — extend]`
> **Given** a body of `{"chatId":"…"}` or `["a","b"]`
> **Then** the body becomes `OpaqueBodyFallback` ("You have a new notification."), a warning is logged,
> and the structured value remains available in `Data`.
> **Given** a body that merely *mentions* braces (`"use {curly} braces"`) **Then** it is left alone.

**BE-PUSH-006** · Long bodies are truncated with an ellipsis · Unit · **P1**
> **Given** a 400-character body **Then** the result is ≤ `MaxBodyLength` (240) and ends with `…`;
> a 240-character body is untouched. Boundary `[Theory]`: 239, 240, 241.

**BE-PUSH-007** · Null/whitespace body passes through untouched · Unit · **P2**

**BE-PUSH-008** · Missing token is a **recorded failure**, not a silent no-op · Unit · **P0**
> **Given** `fcmToken` is null/empty/whitespace
> **Then** `push_send_attempted` **and** `push_send_result` are emitted with
> `success = false, errorCode = "missing_token"`, and a warning is logged.
> *(The token-clearing regression this guard exists for was invisible precisely because a missing token
> looked like nothing happening.)*

**BE-PUSH-009** · Firebase not initialised is recorded · Unit · **P0**
> **Given** `FirebaseMessaging.DefaultInstance == null`
> **Then** the result event carries `errorCode = "firebase_not_initialised"`, an error is logged, and
> **no exception escapes to the caller**.

**BE-PUSH-010** · Attempt/result correlation · Unit · **P1**
> **Then** every send emits exactly one `push_send_attempted` and one `push_send_result` sharing a
> `correlationId`; a hang would therefore appear as an attempt with no result — which a success-only
> counter could never reveal.

**BE-PUSH-011** · Invalid-token classification · Unit · **P0**
> **Given** a `FirebaseMessagingException` with `MessagingErrorCode.Unregistered`, then
> `InvalidArgument`, then `Unavailable`, then `QuotaExceeded`
> **Then** `tokenInvalidated` is **true** for the first two and **false** for the rest.
> *(Dead token → clean up; transient outage → retry. Opposite responses, so the split must hold.)*
> `[Theory]`.

**BE-PUSH-012** · Token is never logged in full · Unit · **P0**
> **Given** a failure with a 160-character token **Then** the log contains only the last 8 characters.

**BE-PUSH-013** · Unexpected exceptions are swallowed · Unit · **P1**
> **Given** Firebase throws `TimeoutException` **Then** the result event records
> `errorCode = "TimeoutException"` with a latency, and the caller's `await` completes normally.

**BE-PUSH-014** · `targetUserId` extraction · Unit · **P2**
> **Given** `data["userId"]` present and parseable **Then** events are attributed to that user;
> missing or unparseable → attributed to null without throwing.

### F.2 Device targeting (unit + integration)

**BE-PUSH-020** · One device, one recipient · Integration · **P0**
> **Steps:** user A registers FCM token T → user B registers the **same** T (device resold or app
> reinstalled under a new account) → a chat message is sent to A.
> **Then** A's `FcmToken` is null, B's is T, and **no push is attempted for A**
> (`missing_token` recorded). *(Wraps BE-AUTH-021 in the real DB.)*

**BE-PUSH-021** · Banned/Rejected users stop receiving pushes · Integration · **P0**
> **Then** after a ban, `FcmToken` is null, so the next room reminder targeting them records
> `missing_token` rather than delivering to a device that may now belong to someone else.

**BE-PUSH-022** · Reminder fan-out targets only reminder-holders with tokens · Integration · **P1**
> **Data:** 5 reminders, 3 of which have FCM tokens.
> **When** the host starts the room **Then** 5 `Notification` rows exist, 3 pushes are attempted, all
> 5 reminders are deleted, and every push carries `type = room` with the `roomId`.

**BE-PUSH-023** · `type` routing contract · Integration · **P1**
> `[Theory]` asserting the `data.type` value each producer sends, since the mobile client routes on it:
> `chat` (+ `senderId`), `room` (+ `roomId`), `support_chat` (+ `chatId`), `report` (+ `reportId`),
> `general`, `account_activated`, `account_locked` (+ `lockout_end`), `account_rejected`, `reRecord`.
> A producer that changes its `type` string silently breaks deep-linking; this is the contract test.

**BE-PUSH-024** · No push producer blocks its caller · Integration · **P0**
> **Given** a push fake that throws on every call
> **Then** room create/join/approve/start, friend request/accept, chat send, support send/reply, and
> every admin status change all still succeed. *(`RoomService` and `AdminService` `await` the push
> **without** a local try/catch, relying on the service's internal catch-all — this test is what
> guarantees that reliance is sound.)*

### F.3 Automated vs manual boundary

| Behaviour | Automated backend | Manual mobile |
| --- | --- | --- |
| Payload shape (`Notification` vs `Data`, APNs headers, priority) | ✅ BE-PUSH-001…007 | — |
| Recipient selection, token stealing, token clearing | ✅ BE-PUSH-020…022 | — |
| Failure classification and telemetry | ✅ BE-PUSH-008…014 | — |
| `data.type` deep-link contract | ✅ BE-PUSH-023 (shape only) | ✅ MQ-PUSH-005 (does it actually navigate?) |
| **Android foreground** — app open, notification suppressed, in-app UI updates | ❌ | ✅ MQ-PUSH-001 |
| **Android background** — app backgrounded, tray notification appears | ❌ | ✅ MQ-PUSH-002 |
| **Android terminated** — app swiped away, tray notification still arrives and opens the right screen | ❌ | ✅ MQ-PUSH-003 |
| **Data-only handling** — `account_locked` produces **no** tray pop-up and routes to the banned screen | ❌ | ✅ MQ-PUSH-004 |
| Doze / battery-optimisation delivery delay | ❌ | ✅ MQ-PUSH-006 |
| Notification permission denied (Android 13+) | ❌ | ✅ MQ-PUSH-007 |

Nothing in the backend can observe any row in the bottom half of that table: FCM decides tray
rendering from the payload, and Flutter's handlers only run in the foreground. Attempting to automate
them server-side would produce tests that pass while the handset shows nothing — the failure mode the
existing `fcm_bug_report.md` and `reversed_fcm_bug_report.md` document.

---

## G. Security Test Plan

BE-SEC-001…010 are defined in C.6 (authentication and authorization flows). This section adds the
rest. All run in `Cocorra.IntegrationTests` unless marked Unit.

### G.1 JWT and session

**BE-SEC-011** · Tampered claims are rejected · Integration · **P0**
> **Given** a valid token whose `VerificationStatus` payload is edited from `Pending` to `Active`
> without re-signing **Then** 401 — the signature check catches it before authorization.

**BE-SEC-012** · Role claim cannot be self-elevated · Integration · **P0**
> **Given** a valid `ACTIVE_1` token re-signed *with the real key* but with an added
> `role = Admin` claim — i.e. simulating a leaked signing key
> **Then** the request **succeeds**, and this is recorded as the documented consequence: there is no
> second factor on role. The test exists to make the blast radius of a key leak explicit and to pin
> BE-SEC-020 (secret hygiene) as its only mitigation.

**BE-SEC-013** · A revoked refresh token cannot resurrect a session · Integration · **P0**
> **Steps:** log in → `POST /RevokeToken` → attempt `POST /RefreshToken` with the old value
> **Then** 400, and no new access token is issued.

**BE-SEC-014** · Refresh-token rotation prevents reuse · Integration · **P0**
> **Steps:** refresh once, then replay the **original** refresh token **Then** 400.

**BE-SEC-015** · Account lockout after repeated failures · Integration · **P1**
> **Given** 5 consecutive wrong passwords (`MaxFailedAccessAttempts = 5`)
> **Then** the 6th attempt returns `Forbidden` with a `lockoutEnd` ≈ 15 minutes out, **even with the
> correct password**; a successful login before the 5th resets the counter.

**BE-SEC-016** · Password change / ban consequences on live sessions · Integration · **P1**
> Documents which live credentials survive: after a **ban**, the JWT dies at the next request
> (`OnTokenValidated`) and the refresh token is nulled; after a **password change**, the JWT and
> refresh token currently survive (finding D.12.3). Asserted as-is.

### G.2 IDOR and resource ownership

Each of these mints a token for user A and targets a resource owned by user B.

**BE-SEC-017** · IDOR sweep · Integration · **P0**
> `[Theory]` over every ownership-scoped resource:
>
> | Target | Expected |
> | --- | --- |
> | `PUT /api/Notifications/read-notification/{B's id}` | 404, B's row stays unread |
> | `GET /Api/V1/Room/{id}/State` where A is not a participant | 400, **no token** |
> | `GET /Api/V1/Room/{id}/Token` where A is not a participant | 400, **no token** |
> | `POST /Api/V1/Room/{id}/Approve/{x}` where A is not the host | 400 |
> | `POST /Api/V1/Room/{id}/End` where A is not the host | 400 |
> | `POST /Api/V1/Room/{id}/Start` where A is not the host | 400 |
> | `POST /Api/V1/Support/chat/{B's chat}/reply` as another admin | 400 |
> | `POST /Api/V1/Support/chat/{B's chat}/close` as another admin | 400 |
> | `GET /Api/V1/Support/chat/history` | only A's chats returned |
> | `GET /Api/V1/Support/chat/active` as admin X | only X's claimed chats |
> | `GET /api/Chat/history/{B}` where A and B are blocked | 400, no messages |
> | `POST /api/Friends/respond-request/{s}` for a request addressed to B | 400 |

**BE-SEC-018** · Manipulated identifiers · Integration · **P1**
> `[Theory]` over `Guid.Empty`, a random GUID, a non-GUID string, a very long string, a
> SQL-injection-shaped string, and a path-traversal-shaped string, against the GUID-constrained routes.
> **Then** route-constrained endpoints return **404** (constraint rejects before the action), never a
> 500 and never a stack trace.

**BE-SEC-019** · Hub-level ownership · Integration · **P0**
> **Then** `ApproveToStage`, `MoveToAudience`, `GrantExtraTime`, `KickUser` and `EndRoom` all reject a
> non-host caller, and `JoinRoom` rejects a user with no participant row — a client cannot bypass
> `POST /Room/{id}/Join` by going straight to the hub.

### G.3 Secrets and configuration

**BE-SEC-020** · No production secret is committed · Unit (static) · **P0**
> **Given** tracked `appsettings*.json` files
> **Then** none of `ConnectionStrings:DefaultConnection` (with a password), `JWTSetting:securityKey`,
> `EmailSettings:SmtpPass`, `LiveKit:ApiSecret`, TURN `credential`, `Minio:SecretKey`,
> `SeedAdmin:Password` or `Analytics:IpHashSalt` holds a non-placeholder value.
> **Current state: this test fails.** `Cocorra.API/appsettings.json` contains live values for all of
> them. The remediation (move to environment variables / user-secrets, mirroring the existing
> `Analytics:IpHashSalt` pattern, and rotate) is tracked as a prerequisite; the test is what prevents
> the next one. `ProductionReadinessTests` already implements this shape for the salt alone — extend it.

**BE-SEC-021** · Startup fails without the IP-hash salt · Integration · **P0** `[EXISTS]`
> **Then** building the host without `Analytics:IpHashSalt` throws with the guidance message — no
> silent fallback to a guessable salt.

**BE-SEC-022** · JWT signing key strength · Unit · **P1**
> **Then** the configured `securityKey` is ≥ 32 bytes, so HMAC-SHA256 is not keyed with a short secret.

**BE-SEC-023** · CORS does not reflect arbitrary origins when configured · Integration · **P1**
> **Given** `Cors:AllowedOrigins` populated
> **Then** a request from `https://evil.example` receives no `Access-Control-Allow-Origin`, while a
> configured origin and any `localhost`/`127.0.0.1`/`[::1]` origin do.
> **And** with the section **absent**, the permissive fallback (`SetIsOriginAllowed(_ => true)`) is
> asserted as-is, with the risk recorded — combined with `AllowCredentials`, a misdeployment without
> that section allows any site to call the API with the caller's credentials.

**BE-SEC-024** · Errors do not leak internals in Production · Integration · **P0**
> **Given** the factory configured with `ASPNETCORE_ENVIRONMENT = Production` and an action forced to
> throw **Then** the response is the generic 500 JSON envelope with no stack trace, no exception type
> and no SQL text.

### G.4 Device blocking, bans and abuse

**BE-SEC-025** · Device block survives re-registration under a new account · Integration · **P0**
> **Steps:** ban user A (blocking device D) → register a brand-new user B sending `X-Device-Id: D`.
> **Then** every request from D is **403** at the middleware, so the new account is unusable — the
> ban-evasion control.

**BE-SEC-026** · Device id is never taken from the request body · Integration · **P0**
> **Then** `POST /Admin/BlockDeviceAndEmail` blocks only device ids from the **registry** written at
> the target's login; a `deviceId` supplied in the body has no effect. *(Otherwise an admin could only
> ever block their own device, or an attacker could poison the block list.)*

**BE-SEC-027** · Over-long device headers are truncated, not rejected · Integration · **P2**
> **Given** a 5000-character `X-Device-Id` **Then** it is truncated to 200 characters
> (`DeviceHeaderExtensions`) and the request proceeds — no 500, no unbounded column write.

**BE-SEC-028** · Banned user is evicted from a live room in real time · Integration · **P0**
> Covered end-to-end by BE-FLOW-004; listed here as the security-owned assertion.

**BE-SEC-029** · Client cannot forge funnel events · Integration · **P0**
> **Then** `POST /api/events/track` refuses every event type outside
> `{room_create_started, notification_opened, feature_viewed}`, so activation and room metrics cannot be
> manipulated by a client.

**BE-SEC-030** · Rate limiter engages · Integration · **P1**
> **Environment:** a dedicated factory instance with the production limiter (100 req/min/IP).
> **Then** the 101st request within the window returns **429**, and a different partition key is
> unaffected. Isolated in its own collection so it cannot poison other tests.

**BE-SEC-031** · Anonymous ticket endpoint cannot be used to enumerate or spam · Integration · **P2**
> **Then** `POST /Support/Ticket` without auth succeeds with `UserId = null`, is subject to the rate
> limiter, and rejects oversized screenshots.

### G.5 Upload safety

**BE-SEC-032** · Magic-byte validation cannot be bypassed by extension or content type · Unit · **P0**
> `[Theory]`: a PHP/EXE payload named `.png` with `Content-Type: image/png` → `Error:FakeImage`; a text
> file named `.mp3` with `Content-Type: audio/mpeg` → `Error:FakeVoice`; a genuine PNG named `.png` →
> accepted. *(`UploadImage.IsValidImageSignature` / `UploadVoice.IsValidVoiceSignature`.)*

**BE-SEC-033** · Stored filename is server-generated · Unit · **P0**
> **Given** a filename of `../../etc/passwd.png`
> **Then** the object key is `Uploads/img/{subFolder}/{newGuid}.png` — the client name is never used,
> so path traversal is structurally impossible.

**BE-SEC-034** · `subFolder` cannot escape the prefix · Unit · **P1**
> `[Theory]` over `"../../"`, `"/abs"`, `"Uploads/Voices"` → the resulting key always stays under
> `Uploads/`.

**BE-SEC-035** · Size limits · Unit · **P1**
> Image > 5 MB → `Error:FileTooLarge`; voice > 3 MB → `Error:FileTooLarge`; boundary cases at exactly
> the limit are accepted.

### G.6 Replay-sensitive flows

**BE-SEC-036** · OTP is single-use · Integration · **P0**
> **Steps:** confirm an email with OTP X → replay the same X.
> **Then** the second attempt fails. Likewise for password reset: the same OTP cannot reset the
> password twice.

**BE-SEC-037** · LiveKit webhook replay · Integration · **P1**
> **Given** the same signed `participant_joined` body posted twice
> **Then** both are accepted (the SDK does not deduplicate) but the effect is idempotent: an allowed
> participant is never evicted, and a disallowed one is simply evicted again. Documented explicitly so
> no one assumes replay protection that does not exist.

**BE-SEC-038** · Refresh-token replay after rotation · Integration · **P0** — see BE-SEC-014.

**BE-SEC-039** · Duplicate event submission · Integration · **P1**
> **Then** two client `track` calls for the same logical action create two rows (no client-supplied
> idempotency key exists); only server-side `eventKey` paths such as `activation_completed` are
> deduplicated. Pinned so analytics consumers do not assume otherwise.

---

## H. Manual QA Plan

These cannot be validated by backend automation: they need real audio, real handsets, real network
transitions, or a human judging a subjective outcome. Executed before each release (see Section I).

**Environments:** `STAGING` = staging API + staging LiveKit + staging Firebase project;
`PROD-SMOKE` = production with disposable accounts. Devices: at least one physical Android handset
(the primary target) plus one iOS device where the case is cross-platform.

### H.1 Real-time audio and room lifecycle

**MQ-LK-001** · Two-way audio in a public room · **P0**
> **Environment:** STAGING, 2 physical Android devices, separate networks (one Wi-Fi, one cellular).
> **Preconditions:** two Active accounts, A and B.
> **Steps:** 1) A creates a 2-hour public room. 2) B opens the feed and joins. 3) A speaks. 4) B raises
> a hand; A approves to stage; B unmutes and speaks.
> **Expected:** B hears A within ~2 s of A speaking; after promotion A hears B; both see the correct
> stage/mic indicators; no audio artefacts or echo.

**MQ-LK-002** · Cellular-only participant behind CGNAT (TURN relay) · **P0**
> **Environment:** STAGING, one device on mobile data with Wi-Fi off.
> **Steps:** join a live room and speak.
> **Expected:** audio connects. If it fails, the TURN credentials in `LiveKit:IceServers` are the first
> suspect — this is the case a server-side test can never detect.

**MQ-LK-003** · Host loses connection and returns inside the grace window · **P0**
> **Preconditions:** live room, host A, listener B. `HostReconnectGraceSeconds = 90`.
> **Steps:** 1) Put A's device in airplane mode. 2) Observe B's screen. 3) After 60 s restore
> connectivity.
> **Expected:** B sees a "host disconnected" state with a countdown to the absolute deadline; the room
> stays open; when A returns, B sees the room resume and audio is restored without B rejoining.

**MQ-LK-004** · Host never returns · **P0**
> **Steps:** as MQ-LK-003 but leave A offline past 90 s + one sweep interval (≤ 10 s).
> **Expected:** within ~100 s B receives "The host lost connection and did not return. This room has
> ended.", is returned to the feed, and the room disappears from the live list. B's audio stops.

**MQ-LK-005** · Host ends the room while others are speaking · **P0**
> **Expected:** every participant is disconnected from audio **immediately** — not merely shown an
> ended screen. Verify by having two participants talking at the moment the host ends: neither can hear
> the other afterwards.

**MQ-LK-006** · Kicked participant loses audio at once · **P0**
> **Steps:** host kicks an on-stage speaker who is mid-sentence.
> **Expected:** the kicked user's audio cuts within ~2 s, they see a removal message, and attempting to
> rejoin is refused. **Critically:** the remaining participants can no longer hear them.

**MQ-LK-007** · Kicked user cannot reconnect with a cached token · **P0**
> **Steps:** kick a user, then force-close and reopen their app so the client reuses its cached LiveKit
> token.
> **Expected:** they are evicted by the `participant_joined` webhook within a webhook round-trip and
> cannot hear the room. *(The one manual test that validates the whole enforcement design.)*

**MQ-LK-008** · Room reaches its duration limit · **P1**
> **Environment:** STAGING with `RoomLifecycle` temporarily shortened (e.g. treat a 2-hour room with a
> reduced overtime allowance) or a genuine long-running session.
> **Expected:** at duration + overtime, all participants receive "This session has reached its scheduled
> length and has ended." and audio stops.

**MQ-LK-009** · Network handover mid-session · **P1**
> **Steps:** while speaking, switch from Wi-Fi to cellular; walk into a lift; ride a metro tunnel.
> **Expected:** the client reconnects automatically without being ejected; if the drop exceeds the
> token TTL the client re-fetches via `GET /Room/{id}/Token` rather than failing permanently.

**MQ-LK-010** · Speaker time limit and extra time · **P1**
> **Steps:** a non-host speaks past `DefaultSpeakerDurationMinutes`; the host grants 5 extra minutes.
> **Expected:** the mic is refused with a clear message at the limit; after the grant the user can
> unmute again; the host is never time-limited.

**MQ-LK-011** · Stage capacity full · **P2**
> **Expected:** with `StageCapacity` speakers on stage, approving another shows "Stage is full" to the
> host and the waiting user remains in the audience with their hand still visible to the host.

**MQ-LK-012** · Host deletes their account mid-session · **P1**
> **Expected:** the room ends for everyone, audio stops, and participants are returned to the feed.

**MQ-LK-013** · Private room approval on a real device · **P2**
> **Expected:** the requester sees a waiting state and hears nothing; the host receives a push and an
> in-app notification; on approval the requester enters and hears audio.

### H.2 Push notifications on device

**MQ-PUSH-001** · Android **foreground** · **P0**
> **Environment:** STAGING, physical Android, app open on any screen.
> **Steps:** another user sends a chat message.
> **Expected:** no duplicate OS tray notification fights the in-app UI; the in-app unread badge and
> conversation update; if an in-app banner is shown it is the app's own.

**MQ-PUSH-002** · Android **background** · **P0**
> **Steps:** press Home (app backgrounded, not killed); another user sends a chat message.
> **Expected:** a tray notification appears with the sender's name as title and a **prose preview** as
> body — never raw JSON; tapping opens the conversation.

**MQ-PUSH-003** · Android **terminated** · **P0**
> **Steps:** swipe the app away from recents; send a room-start notification (set a reminder, have the
> host start the room).
> **Expected:** the tray notification still arrives; tapping cold-starts the app **and lands on the
> room**, not the home screen. *(This is the path where Flutter handlers never run, so the payload
> alone must be correct.)*

**MQ-PUSH-004** · Data-only ban notification produces no pop-up · **P0**
> **Steps:** with the app backgrounded, an admin bans the account.
> **Expected:** **no** visible tray notification; on opening, the app routes to the banned screen, and
> the persisted notification is readable in the notifications list. Repeat for `Mute24h` and check the
> app distinguishes a 24-hour mute from a permanent ban using `lockout_end`.

**MQ-PUSH-005** · Deep-link routing per `type` · **P1**
> **Steps:** trigger one of each: chat, room, support_chat, report, general, account_activated, reRecord.
> **Expected:** each lands on the correct screen with the correct entity loaded.

**MQ-PUSH-006** · Doze / battery optimisation · **P1**
> **Steps:** leave the device idle and screen-off for 30+ minutes, then trigger a room-start push.
> **Expected:** it arrives promptly (`Android.Priority = High` is set) rather than being deferred to
> the next maintenance window.

**MQ-PUSH-007** · Notification permission denied (Android 13+) · **P2**
> **Expected:** the app degrades gracefully — in-app notifications still work and no crash occurs.

**MQ-PUSH-008** · Reinstall / token rotation · **P1**
> **Steps:** uninstall, reinstall, log in as a **different** user on the same device, then have someone
> message the **first** user.
> **Expected:** the first user's message does **not** appear on this device. *(The misdelivery bug
> class that `UpdateFcmTokenAsync`'s token-stealing exists to prevent.)*

### H.3 Verification, moderation and account lifecycle

**MQ-QA-001** · Full onboarding on a real handset · **P0**
> **Steps:** register with a device-recorded voice clip and a camera photo → receive the OTP email →
> confirm → log in (restricted) → confirm full features are blocked → admin activates → log in again.
> **Expected:** each stage behaves as specified, the OTP email renders correctly in Gmail and a common
> Arabic-locale client, and the restricted session cannot reach room features.

**MQ-QA-002** · Re-record flow · **P1**
> **Steps:** admin sets `ReRecord` → user receives the Arabic push and email → user re-records.
> **Expected:** the Arabic text renders correctly (RTL, no mojibake) in the tray, in-app and in email;
> the status returns to `Pending`.

**MQ-QA-003** · Ban while in a live room · **P0**
> **Steps:** user is speaking on stage; admin bans them from the dashboard.
> **Expected:** their client is force-disconnected within ~2 s, audio stops for the room, they are
> routed to the banned screen, and re-login is refused.

**MQ-QA-004** · Device ban blocks a new account on the same handset · **P0**
> **Steps:** ban an account via *Block device and email* → on the same handset, register a new account.
> **Expected:** every request is refused with the device-blocked message.

**MQ-QA-005** · Account deletion is complete from the user's view · **P1**
> **Expected:** after deletion the user is logged out, cannot log in, their hosted room has ended for
> its participants, and they no longer appear in other users' friend lists or chats.

**MQ-QA-006** · Support chat on device · **P2**
> **Steps:** user opens support, sends 3 messages (4th refused), admin claims and replies from the
> dashboard.
> **Expected:** the user receives the reply in real time when the app is open and as a push when it is
> not; the dashboard alert fires for other admins and clears on claim.

**MQ-QA-007** · Admin dashboard end-to-end · **P1**
> **Steps:** exercise user list/search/paging, status changes, bulk status change, reports triage with
> each action, and the analytics pages.
> **Expected:** every panel loads against the real API, actions take effect, and a Coach account sees
> exactly the Coach-permitted subset.

**MQ-QA-008** · Arabic and emoji content integrity · **P2**
> **Steps:** send Arabic text and emoji through chat, room group chat, support chat, room titles and
> report descriptions.
> **Expected:** stored and re-rendered intact everywhere including push bodies (collation and
> encoding check that automated tests with ASCII fixtures would miss).

---

## I. Regression Strategy

Five gates. Each answers a different question, so each runs a different subset — the point is that a
developer waits 3 minutes, not 25, while nothing untested reaches production.

### I.1 Gate 1 — Pre-commit / local (developer machine, seconds)

**Runs:** `dotnet build` + `dotnet test Cocorra.Tests` (unit tier only).
**Budget:** < 60 s.
**Why:** the unit tier has no I/O and no containers, so it is fast enough to run on every save. It
catches the majority of logic regressions before a push.

### I.2 Gate 2 — Pull request (required status check)

**Runs:**
1. `dotnet build -warnaserror` and NuGet audit (the `SQLitePCLRaw` pin in `Cocorra.Tests.csproj`
   exists because an audit failure already bit this repo — keep it enforced).
2. **Full unit tier** — all of `Cocorra.Tests`.
3. **Integration tier minus the nightly-only collections** — `Database`, `Api`, `Hubs` against the
   Testcontainers SQL Server. Excludes `LiveKitIntegration` and `Storage`.
4. Coverage collection with a **ratchet**: line coverage on `Cocorra.BLL` may not fall below the
   previous `main` value. No absolute percentage target — that number gets gamed and says nothing
   about whether the right things are tested.
5. All **P0** tests must pass. A failing P1+ also fails the build; the priority drives *authoring*
   order, not tolerance for red.

**Budget:** ≤ 8 minutes. **Why:** this gate must be fast enough that nobody is tempted to bypass it,
so the container-heavy suites are deliberately excluded.

### I.3 Gate 3 — Merge to `main` / nightly

**Runs:** everything in Gate 2, **plus**
- `LiveKitIntegration` collection (real LiveKit container) — BE-LIVEKIT-020…028.
- `Storage` collection (real MinIO container) — BE-INT-001, BE-INT-002.
- BE-SEC-030 rate-limiter suite (isolated — it deliberately exhausts a partition).
- Load smoke (K.5) at low VU count against staging.
- Flaky detection: run the suite twice and flag any test whose result differs.

**Budget:** ≤ 30 minutes. **Why:** these need extra containers or wall-clock time and would make the
PR gate unusable — but a regression in LiveKit room lifecycle must not survive a day.

### I.4 Gate 4 — Staging / pre-release regression

Triggered when a release branch is cut, **before** merging to `prod`.

1. Full automated suite against a staging deployment, all collections included.
2. **Migration rehearsal**: restore a production-shaped snapshot into staging, apply migrations,
   assert success and record duration. *(Nothing rehearses this today — migrations run for the first
   time in production.)*
3. **Manual QA pass**: every **P0** manual case — MQ-LK-001…007, MQ-PUSH-001…004, MQ-QA-001,
   MQ-QA-003, MQ-QA-004.
4. **Targeted manual regression by change area:**

   | Release touches | Mandatory manual cases |
   | --- | --- |
   | Room / hub / LiveKit | MQ-LK-001…013 |
   | Auth / Identity / user status | MQ-QA-001…005 |
   | Push / notifications | MQ-PUSH-001…008 |
   | Admin / moderation | MQ-QA-003, MQ-QA-004, MQ-QA-007 |
   | Analytics / events | MQ-QA-007 + BE-API-ANL-001 against staging data |

5. Load test at the target concurrency profile (K.5).

**Why:** the release gate is the only place real media, real handsets and a real migration are
exercised together.

### I.5 Gate 5 — Production smoke (immediately after deploy)

Small, read-mostly, self-cleaning, using disposable accounts. Runs automatically after the `deploy`
job and is the deploy's success criterion.

| # | Check | Expectation |
| --- | --- | --- |
| **BE-SMOKE-001** | `GET /` | 200 (the Docker healthcheck already uses this) |
| **BE-SMOKE-002** | `GET /swagger/v1/swagger.json` | 200 and parses |
| **BE-SMOKE-003** | `POST /Login` with the smoke account | 200 + token |
| **BE-SMOKE-004** | `GET /api/Profile/me` with that token | 200 |
| **BE-SMOKE-005** | `GET /Api/V1/Room/Feed` | 200 |
| **BE-SMOKE-006** | `GET /api/Profile/me` **without** a token | 401 — proves authz survived the deploy |
| **BE-SMOKE-007** | `GET /Api/V1/Admin/Users` as a non-admin | 403 |
| **BE-SMOKE-008** | `GET /Api/V1/Analytics/System/Health` as admin | 200; drop and dead-letter counts within threshold |
| **BE-SMOKE-009** | Create a room → fetch its token → end it | 200s, non-empty `LiveKitToken`, room `Ended` — the only write, fully reverted |
| **BE-SMOKE-010** | DB connectivity + pending migrations | zero pending |

**Rollback trigger:** any P0 smoke failure. **Why:** these catch what only appears with production
configuration — a missing environment variable, an unreachable LiveKit host, an unapplied migration.

### I.6 Regression-guard policy

- **Every production bug fix ships with a failing-first test** naming the incident. There is precedent
  worth formalising: `fcm_bug_report.md`, `reversed_fcm_bug_report.md` and `room_image_bug_report.md`
  describe fixed defects whose guards belong in the suite permanently.
- Tests that pin *current* rather than *desired* behaviour (D.12) carry
  `// PINS CURRENT BEHAVIOUR — see D.12.n`, so changing them is a deliberate, reviewed edit.
- No test is deleted to make a build green — it is fixed, or re-specified in review.

---

## J. Test Data Strategy

Governing rule: **no shared mutable state between tests.** Every integration test class gets a freshly
reset database (Respawn) and creates what it needs through builders. The standard cast below is
re-seeded per class, never shared across a run, so no test can depend on another's side effects and
the suite parallelises by class.

### J.1 Builders (`Cocorra.Tests/Builders/`, shared with the integration project)

Fluent, deterministic defaults, explicit overrides. Fixed-seed `Bogus` for names/emails so a failure
reproduces exactly.

```
UserBuilder         .AsActive() .AsPending() .AsReRecord() .AsBanned() .AsRejected()
                    .WithRole("Admin"|"Coach"|"User") .WithFcmToken(t) .WithMbti("INTJ")
                    .WithRefreshToken(value, expiry) .LockedOutUntil(when) .WithAge(n)
RoomBuilder         .Live() .Scheduled(at) .Ended() .Cancelled() .Private()
                    .HostedBy(user) .WithCapacity(total, stage) .ForHours(2|3)
                    .WentLiveAt(t) .HostDisconnectedAt(t) .InCategory(c)
                    .WithSpeakerMinutes(n)
ParticipantBuilder  .Active() .PendingApproval() .Kicked() .Left() .Rejected()
                    .OnStage() .Unmuted().Since(t) .HandRaised()
                    .WithSpokenSeconds(s) .WithExtraMinutes(m) .RejoinedTimes(n)
DeviceBuilder       .ForUser(u) .WithId("device-A") .Blocked(at) .LastSeen(t)
ReportBuilder       .AgainstUser(u) .AgainstRoom(r) .WithCategory(c) .Open() .Resolved()
SupportChatBuilder  .Pending() .ActiveWith(admin) .Closed() .WithMessages(n)
FriendshipBuilder   .Pending(a → b) .Accepted(a, b) .Rejected(a → b)
EventBuilder        .OfType(t) .ForUser(u) .InRoom(r) .At(when) .WithProperties(o)
```

### J.2 Standard cast (seeded per integration test class)

| Key | Entity | Purpose |
| --- | --- | --- |
| `ADMIN_1` | Active, role `Admin`, FCM token | Admin-authorized calls; actor in moderation tests |
| `ADMIN_2` | Active, role `Admin` | Cross-admin authorization (BE-SUP-013, BE-API-SUP-008) |
| `COACH_1` | Active, role `Coach` | The Admin-vs-Coach boundary (BE-SEC-008) |
| `ACTIVE_1` | Active, `User`, FCM token, MBTI set | Default happy-path actor / room host |
| `ACTIVE_2` | Active, `User`, FCM token | Second party: joins, friends, chat, blocks |
| `ACTIVE_3` | Active, `User`, **no** FCM token | Proves push paths skip tokenless users cleanly |
| `PENDING_1` | `Pending`, email confirmed | Restricted-token authorization tests |
| `RERECORD_1` | `ReRecord` | Re-record flow |
| `BANNED_1` | `Banned`, locked out to `MaxValue`, refresh token nulled | Login/refresh refusal |
| `REJECTED_1` | `Rejected` | Status-branch coverage |
| `LOCKED_1` | Active but locked out for 24 h | `Mute24h` semantics |
| `UNCONFIRMED_1` | Active status, `EmailConfirmed = false` | Login refusal before confirmation |

### J.3 Rooms

| Key | Shape |
| --- | --- |
| `ROOM_LIVE_PUBLIC` | `Live`, public, `WentLiveAt = now−10m`, 2 h, host `ACTIVE_1` on stage unmuted |
| `ROOM_LIVE_PRIVATE` | `Live`, `IsPrivate = true`, host `ACTIVE_1` |
| `ROOM_LIVE_FULL` | `Live`, `TotalCapacity = 2`, already at capacity |
| `ROOM_LIVE_STAGE_FULL` | `Live`, `StageCapacity = 1`, one speaker on stage |
| `ROOM_SCHEDULED_FUTURE` | `Scheduled`, `StartDate = now+2h`, 3 reminders held |
| `ROOM_SCHEDULED_PAST` | `Scheduled`, `StartDate = now−1h` (late start → positive `minutesFromScheduledStart`) |
| `ROOM_ENDED` | `Ended`, participants already `Left` |
| `ROOM_CANCELLED` | `Cancelled` |
| `ROOM_HOST_DROPPED` | `Live`, `HostDisconnectedAt = now−120s` (grace expired) |
| `ROOM_HOST_DROPPED_FRESH` | `Live`, `HostDisconnectedAt = now−10s` (inside grace) |
| `ROOM_OVERDUE` | `Live`, 2 h, `WentLiveAt = now−2h20m` (past duration + overtime) |
| `ROOM_LEGACY_NO_WENTLIVE` | `Live`, `WentLiveAt = null` (exempt from duration enforcement) |

### J.4 Participants, devices, and the rest

- **Participants** for `ROOM_LIVE_PUBLIC` — one of each status: `Active` on stage unmuted with
  `LastUnmutedAt = now−30s`; `Active` in audience muted; `Active` with hand raised; `PendingApproval`;
  `Kicked`; `Left` with `RejoinCount = 2`. One fixture serves the state-transition, capacity and
  event-emission tests.
- **Devices** — `DEVICE_CLEAN` (registered, unblocked, `ACTIVE_1`); `DEVICE_BLOCKED` (blocked,
  `BANNED_1`); `DEVICE_SHARED` (one `DeviceId` registered under both `ACTIVE_1` and `ACTIVE_2`) for
  ban-evasion and the non-unique-index test; `DEVICE_NULL_ID` rows for the filtered-index test.
- **Blocked pair** — `ACTIVE_1` blocks `ACTIVE_3`; used by every chat/profile block assertion.
- **Deleted user** — created and deleted *inside* the test that needs it, never pre-seeded, because
  deletion is the behaviour under test.
- **Analytics fixture** — 30 days of `UserEvent` rows from `EventBuilder` over a fixed range with a
  fixed seed, plus matching rooms and participants. **All timestamps derive from a single injected
  `DateTime` anchor**, never `DateTime.UtcNow` at assertion time.

### J.5 Invalid-data catalogue (shared `[Theory]` member data)

One reusable source, so every endpoint faces the same hostile inputs:

| Category | Values |
| --- | --- |
| Identifiers | `Guid.Empty`, unknown GUID, `"not-a-guid"`, 5000-char string, `"../../etc/passwd"`, `"' OR 1=1--"`, `"<script>alert(1)</script>"` |
| Strings | `null`, `""`, `"   "`, one over each `MaxLength` (50 / 100 / 250), Arabic text, emoji, 4-byte Unicode |
| Numbers | `0`, `-1`, `int.MaxValue`, and each documented boundary ±1 — capacity 2/1000, stage 1/20, speaker minutes 1/60, duration 2/3, extra time 1/30, paging 1/20/50/100 |
| Dates | far past, far future, `DateTime.MinValue`, reversed `from`/`to` |
| Enums | undefined values: `(UserStatus)99`, `(RoomCategory)99`, `(AdminReportAction)99` |
| Files | 0 bytes, 1 byte over each limit, wrong magic bytes for the extension, hostile filename, double extension (`a.png.exe`) |
| Tokens | empty, `"Bearer "`, malformed JWT, `alg:none`, wrong signature, expired, wrong issuer/audience |

### J.6 Anti-patterns banned in review

1. No test reads or writes a row it did not create (beyond its own class's standard cast).
2. No `DateTime.UtcNow` in an assertion — inject the anchor or assert a tolerance window.
3. No ordering dependency between test methods or classes.
4. No shared static mutable state. **Exception that must be managed:** `RoomHub._connections` *is* a
   static `ConcurrentDictionary`, so hub tests call `PurgeUserConnections` in teardown or run in a
   non-parallel collection. That is a constraint of the production design, not a test smell.
5. No sleeps waiting on background services — call `SweepAsync` / `PerformAggregationCycleAsync` /
   `PerformBatchedPurgeAsync` / `CaptureSnapshotAsync` directly. All four are `public` precisely so a
   test can drive one cycle.
6. No assertion on a log message unless the log *is* the contract (the LiveKit eviction audit line).

---

## K. CI/CD Execution Strategy

Three workflows. The existing `deploy.yml` keeps its job but gains a gate.

### K.1 `.github/workflows/pr.yml` — pull requests to `main`

```
on: pull_request [main]
runs-on: ubuntu-latest            # Docker present -> Testcontainers works unchanged
concurrency: cancel superseded runs
steps:
  - checkout
  - setup-dotnet 10.0.x
  - dotnet restore                          # NuGet audit failures fail the build
  - dotnet build -c Release -warnaserror
  - dotnet test Cocorra.Tests -c Release --no-build
        --collect:"XPlat Code Coverage" --logger trx
  - dotnet test Cocorra.IntegrationTests -c Release --no-build
        --filter "Category!=LiveKit&Category!=Storage&Category!=RateLimit"
        --collect:"XPlat Code Coverage" --logger trx
  - ReportGenerator -> coverage summary as a PR comment
  - coverage ratchet: fail if Cocorra.BLL line coverage < main baseline
  - publish TRX results as check annotations
env:
  Analytics__IpHashSalt: <CI-only random value>   # the startup guard requires it
```

Set as a required status check on `main`.

### K.2 `.github/workflows/nightly.yml`

```
on: schedule (daily) + workflow_dispatch
jobs:
  full-suite:     everything from pr.yml with NO category filter
                  (LiveKit + Storage + RateLimit included)
  flaky-detect:   run the full suite twice, diff results, open an issue on divergence
  load-smoke:     k6 run Cocorra.LoadTests/room-join.js against staging, low VU
  security-scan:  dotnet list package --vulnerable --include-transitive   (fail on High)
                  + secret scan over tracked files (same rule as BE-SEC-020)
```

### K.3 `.github/workflows/deploy.yml` — existing pipeline, gated

Today it checks out, writes `appsettings.Production.json` from a secret, SCPs the tree to the VPS and
runs `docker compose up --build -d`. It never runs a test. Two additions:

1. **A `test` job** running the full PR suite, with `deploy` declaring `needs: test`. A red suite can
   then no longer reach production.
2. **A `smoke` job** with `needs: deploy`, running BE-SMOKE-001…010 against
   `https://api.cocorraapp.com` and failing loudly. Rollback stays manual (`docker compose down` +
   redeploy the previous image) but is now *triggered by a signal* rather than a user report.

Kept as-is: the pinned third-party action SHAs (`actions/checkout`, `appleboy/scp-action`,
`appleboy/ssh-action`) — that is already correct supply-chain hygiene.

### K.4 Categorisation and parallelism

xUnit traits drive the filters, applied per class as `[Trait("Category", "…")]`:

| Category | Runs at | Approx. count when complete |
| --- | --- | --- |
| *(none)* — unit | every gate | 468 existing + ~180 new |
| `Integration` | PR and later | ~150 |
| `LiveKit` | nightly, pre-release | ~10 |
| `Storage` | nightly, pre-release | ~4 |
| `RateLimit` | nightly, isolated | 1 |
| `Smoke` | post-deploy | 10 |

`[Collection]` attributes prevent container contention: `Database`, `Api`, `Hubs`,
`LiveKitIntegration`, `Storage`, plus a non-parallel collection for anything touching
`RoomHub._connections`.

### K.5 Performance and load testing

`loadtest.js` already exists at the repository root and becomes the basis of `Cocorra.LoadTests/`.
Load tests never run on PRs; they run nightly against staging and as a pre-release gate.

**BE-PERF-001** · Room-join surge · Load · **P1**
> **Scenario:** 200 VUs join one `Live` room over 60 s — `POST /Room/{id}/Join` → hub `JoinRoom` →
> hold 5 minutes.
> **Why first:** it is the real product shape — a popular host starts a room and everyone arrives at
> once. It exercises the capacity count, per-join token minting and `_connections` simultaneously.
> **Thresholds:** p95 join < 1500 ms; error rate < 1%; no drift between the DB participant count and
> the hub group.

**BE-PERF-002** · Feed read throughput · Load · **P2**
> **Scenario:** 500 VUs polling `GET /Room/Feed` with 40 active rooms. **Threshold:** p95 < 800 ms.
> **Watch:** `GetRoomsFeedAsync` issues **two queries per scheduled room inside a loop**
> (`GetRoomRemindersCountAsync` + `GetRoomReminderAsync`) — an N+1 that grows with the scheduled-room
> count. This test makes it visible before users do.

**BE-PERF-003** · Event-pipeline saturation · Load · **P0**
> **Scenario:** drive traffic until the bounded channel (`EventChannelCapacity`, default 10 000) is
> pressured, with both `Analytics:Enable*` flags on.
> **Assert:** `GET /Api/V1/Analytics/System/Health` reports the drop rate; API p95 does **not** degrade
> (the producer must stay non-blocking); drops are counted, never silent.
> **Why P0:** `docs/dashboard-discovery/28-production-analytics-activation.md` requires a measured
> baseline drop rate before either flag is enabled in production. This test produces that number.

**BE-PERF-004** · Concurrent rooms under background sweeps · Load · **P2**
> **Scenario:** 50 concurrent live rooms while `HostReconnectGraceService` (10 s) and
> `RoomDurationLimitService` (60 s) sweep. **Assert:** each sweep finishes well inside its interval, no
> room ends incorrectly, no deadlocks.

**BE-PERF-005** · Chat write throughput · Load · **P3**
> **Scenario:** 100 VUs exchanging messages. **Watch:** the push fan-out in `SaveMessageAsync` is
> `await`ed on the request path, so FCM latency becomes user-visible send latency.

**BE-PERF-006** · Analytics queries at volume · Load · **P2**
> **Scenario:** all 30 analytics `GET` endpoints against ~5 M `UserEvent` rows.
> **Threshold:** p95 < 3 s per endpoint; anything slower is a missing-index finding.

---

## L. Complete Test Backlog

Every planned test, ordered by execution wave. Full Given/When/Then detail lives in sections B–H
under the same ID; this is the executable index.

**Column conventions.** *Type*: U = unit, I = integration, A = API (integration over HTTP),
H = hub (integration over SignalR), M = manual, L = load, S = static/config.
*Deps*: `INFRA-n` = an infrastructure task below; a `BE-…` id means that test must exist first;
`—` means none. *Pre* uses the fixture keys from Section J.

### Wave 0 — Infrastructure (blocks everything else)

| ID | Task | Type | Pri | Preconditions | Expected result | Deps |
| --- | --- | --- | --- | --- | --- | --- |
| **INFRA-001** | Add `public partial class Program { }` to `Cocorra.API/Program.cs` | — | P0 | — | `WebApplicationFactory<Program>` compiles | — |
| **INFRA-002** | Create `Cocorra.IntegrationTests` project, add to `Cocorra.sln` | — | P0 | INFRA-001 | Builds, references API/BLL/DAL | INFRA-001 |
| **INFRA-003** | `SqlServerFixture` — Testcontainers MsSql, migrations applied once per run | — | P0 | Docker available | Container starts, `MigrateAsync` succeeds | INFRA-002 |
| **INFRA-004** | `CocorraApiFactory : WebApplicationFactory<Program>` with DI overrides | — | P0 | INFRA-003 | Real pipeline, faked LiveKit/FCM/SMTP/S3 | INFRA-003 |
| **INFRA-005** | `DatabaseReset` via Respawn, per test class | — | P0 | INFRA-003 | Clean DB between classes, < 1 s | INFRA-003 |
| **INFRA-006** | `AuthTestClient` — real-login and raw-JWT-mint helpers (expired, wrong key, wrong issuer, tampered claims) | — | P0 | INFRA-004 | Any auth scenario expressible in one line | INFRA-004 |
| **INFRA-007** | Fakes: `FakeLiveKitService` (call log), `FakePushNotificationService` (payload log), `RecordingEmailService` (OTP capture), `InMemoryS3Client` | — | P0 | INFRA-002 | Assertable call/payload logs | INFRA-002 |
| **INFRA-008** | `Builders/` — the 9 builders in J.1, shared by both test projects | — | P0 | — | Deterministic fixtures, no inline entity construction | — |
| **INFRA-009** | Standard-cast seeder (J.2–J.4) | — | P0 | INFRA-008 | Per-class seed in < 2 s | INFRA-008 |
| **INFRA-010** | Add FluentAssertions, Respawn, Bogus, `Mvc.Testing`, `Testcontainers.MsSql`, SignalR client | — | P0 | — | Restore succeeds, audit clean | — |
| **INFRA-011** | `[Trait("Category", …)]` + `[Collection]` conventions applied | — | P1 | INFRA-002 | Filters in K.1/K.2 select correctly | INFRA-002 |
| **INFRA-012** | Invalid-data catalogue as shared `MemberData` (J.5) | — | P1 | — | One source feeds every `IV` case | — |
| **INFRA-013** | `.github/workflows/pr.yml` | — | P0 | INFRA-004 | Required check on `main`, ≤ 8 min | INFRA-004 |
| **INFRA-014** | Gate `deploy.yml` behind a `test` job; add a `smoke` job | — | P0 | INFRA-013 | Red suite cannot deploy | INFRA-013 |
| **INFRA-015** | `.github/workflows/nightly.yml` | — | P1 | INFRA-013 | Full suite + flaky detect + scans | INFRA-013 |
| **INFRA-016** | LiveKit + MinIO test containers and their collections | — | P1 | INFRA-003 | `LiveKit`/`Storage` categories run nightly | INFRA-003 |
| **INFRA-017** | `Cocorra.LoadTests/` from the existing `loadtest.js` | — | P2 | staging URL | k6 runs against staging | — |
| **INFRA-018** | Coverage ratchet script + PR comment | — | P2 | INFRA-013 | `Cocorra.BLL` coverage cannot regress | INFRA-013 |

### Wave 1 — P0 security, authentication and authorization

| ID | Feature | Scenario | Type | Pri | Preconditions | Expected result | Deps |
| --- | --- | --- | --- | --- | --- | --- | --- |
| BE-SEC-001 | Auth | Register → OTP → login → authorized call | A | P0 | Empty DB, recording email fake | Each step succeeds; final call 200 | INFRA-004,006,007 |
| BE-SEC-002 | Authz | Restricted token blocked from full-access endpoints | A | P0 | `PENDING_1` | 403 on Active-only; 200 on `VerificationOnly` | INFRA-006 |
| BE-SEC-003 | Authz | Ban invalidates a live JWT at the next request | A | P0 | `ACTIVE_1` logged in, `ADMIN_1` | 401 after ban | INFRA-006 |
| BE-SEC-004 | JWT | Expired token | A | P0 | Minted with past `exp` | 401 | INFRA-006 |
| BE-SEC-005 | JWT | Wrong signing key | A | P0 | Foreign-key token | 401 | INFRA-006 |
| BE-SEC-006 | JWT | Wrong issuer / audience | A | P0 | Two crafted tokens | 401 each | INFRA-006 |
| BE-SEC-007 | JWT | `alg:none` and algorithm confusion | A | P0 | Crafted tokens | 401 each | INFRA-006 |
| BE-SEC-008 | Authz | Role matrix across 6 representative endpoints | A | P0 | `ACTIVE_1`, `COACH_1`, `ADMIN_1` | Matches each action's attribute; Coach 403 on Admin-only | INFRA-009 |
| BE-SEC-009 | Hubs | Hub auth via `?access_token=`; ignored on REST | H | P0 | Valid + absent token | Hub connects only with token; REST ignores query token | INFRA-004 |
| BE-SEC-010 | Middleware | `DeviceBlockingMiddleware` blocks before the controller | A | P0 | `DEVICE_BLOCKED` | 403 + JSON body on anon and authed routes; absent header passes | INFRA-009 |
| BE-SEC-011 | JWT | Tampered `VerificationStatus` claim | A | P0 | Edited unsigned payload | 401 | BE-SEC-005 |
| BE-SEC-012 | JWT | Self-elevated role with the real key (blast-radius doc) | A | P0 | Re-signed token | Succeeds; recorded as the key-leak consequence | BE-SEC-020 |
| BE-SEC-013 | Session | Revoked refresh token cannot resurrect a session | A | P0 | Logged-in user | 400 on refresh after revoke | — |
| BE-SEC-014 | Session | Rotated refresh token cannot be replayed | A | P0 | One refresh performed | 400 on the original value | — |
| BE-SEC-017 | IDOR | 12-target ownership sweep | A | P0 | A and B with owned resources | Each target refused; no data leaks | INFRA-009 |
| BE-SEC-019 | Hubs | Host-only hub methods reject non-hosts; `JoinRoom` needs a participant row | H | P0 | `ROOM_LIVE_PUBLIC` | `HubException` on each | INFRA-004 |
| BE-SEC-020 | Secrets | No production secret in tracked `appsettings*.json` | S | P0 | Repo checkout | Fails today; passes after remediation | — |
| BE-SEC-021 | Config | Startup fails without `Analytics:IpHashSalt` | I | P0 | Host built without the key | Throws with guidance | — |
| BE-SEC-024 | Errors | Production env leaks no internals | A | P0 | Factory in Production, forced throw | Generic 500 envelope only | INFRA-004 |
| BE-SEC-025 | Devices | Device ban survives re-registration under a new account | A | P0 | `BANNED_1` + `DEVICE_BLOCKED` | New account unusable from that device | BE-SEC-010 |
| BE-SEC-026 | Devices | Device ids come from the registry, never the body | A | P0 | `ACTIVE_1` with 3 devices | Body-supplied id ignored | INFRA-009 |
| BE-SEC-029 | Events | Client cannot forge server-owned event types | A | P0 | `ACTIVE_1` | 400 for every disallowed type | — |
| BE-SEC-032 | Uploads | Magic-byte validation cannot be bypassed | U | P0 | Crafted files | `Error:FakeImage` / `Error:FakeVoice` | — |
| BE-SEC-033 | Uploads | Stored filename is server-generated | U | P0 | Hostile filename | Key is `Uploads/…/{guid}{ext}` | — |
| BE-SEC-036 | Replay | OTP is single-use (confirm and reset) | A | P0 | Unconfirmed user | Second use fails | BE-SEC-001 |
| BE-SEC-038 | Replay | Refresh replay after rotation | A | P0 | — | 400 | BE-SEC-014 |
| BE-AUTH-006 | Auth | Lockout checked before password verification | U | P0 | Locked-out user, wrong password | `Forbidden`; `CheckPasswordAsync` never called | — |
| BE-AUTH-007 | Auth | No user enumeration on login | U | P0 | Unknown vs wrong-password | Identical 400 message | — |
| BE-AUTH-009 | Auth | Pending/ReRecord receive a restricted token | U | P0 | `PENDING_1`, `RERECORD_1` | Claim matches status; device registered | — |
| BE-AUTH-010 | Auth | Banned/Rejected refused, no device registration | U | P0 | `BANNED_1`, `REJECTED_1` | 400 each; no token | — |
| BE-AUTH-011 | Auth | Active login happy path | U | P0 | `ACTIVE_1` | JWT + rotated refresh + device registered | — |
| BE-AUTH-012 | Auth | JWT claim contract | U | P0 | User in 2 roles | All 7 claim types + role claims; unique `jti` | — |
| BE-AUTH-013 | Auth | Expired refresh token | U | P0 | Past expiry | 400 | — |
| BE-AUTH-014 | Auth | Unknown refresh token | U | P0 | — | 400 | — |
| BE-AUTH-015 | Auth | Locked-out refresh is refused and burned | U | P0 | Banned user, valid refresh | `Forbidden`; stored token nulled | — |
| BE-AUTH-021 | Auth | FCM token stolen from a stale owner | U | P0 | A and B share token T | B nulled, A set | — |
| BE-AUTH-023 | Auth | Invalid OTP cannot reset a password | U | P0 | Wrong OTP | 400; password unchanged | — |
| BE-AUTH-024 | Auth | ForgotPassword does not enumerate | U | P0 | Unknown email | Generic 200; no email sent | — |
| BE-API-AUTH-002 | Auth API | `POST /Login` — H, IV, BR, BE | A | P0 | Standard cast | Documented statuses; 5 failures → lockout | BE-SEC-001 |
| BE-API-AUTH-012 | Auth API | `POST /RefreshToken` — H, IV, BR, CF | A | P0 | `ACTIVE_1`, `BANNED_1` | Per D.1 | BE-SEC-014 |
| BE-API-AUTH-005 | Auth API | `PUT /UpdateFcmToken` — query vs body, CF | A | P0 | `ACTIVE_1`, `ACTIVE_2` | Query wins; token stolen; 400 when absent | BE-AUTH-021 |
| BE-API-AUTH-009 | Auth API | `POST /ReRecordVoice` — email from JWT only | A | P0 | `RERECORD_1` | Cannot target another user's recording | INFRA-006 |
| BE-API-ROLE-003 | Roles API | `Admin` role cannot be self-assigned | A | P0 | `ADMIN_1`, target user | 400 for any casing of "Admin" | INFRA-009 |
| BE-API-NOTIF-002 | Notif API | IDOR on mark-as-read | A | P0 | A and B notifications | 404; B's row unchanged | INFRA-009 |
| BE-API-EVENT-001 | Events API | Only 3 client event types permitted | A | P0 | `ACTIVE_1` | 400 for server-owned types | BE-SEC-029 |

### Wave 2 — P0 room lifecycle, LiveKit and real-time

| ID | Feature | Scenario | Type | Pri | Preconditions | Expected result | Deps |
| --- | --- | --- | --- | --- | --- | --- | --- |
| BE-ROOM-001 | Rooms | Create live room | U | P0 | Valid DTO, 2 h | Live + `WentLiveAt` + host on stage + `EnsureRoomExists` + 2 events | — |
| BE-ROOM-005 | Rooms | Public join issues a listener token | U | P0 | `ROOM_LIVE_PUBLIC` | Active/muted row; `canPublish = false` | — |
| BE-ROOM-006 | Rooms | Private join queues, issues no token | U | P0 | `ROOM_LIVE_PRIVATE` | PendingApproval; host notified; no token | — |
| BE-ROOM-007 | Rooms | Host rejoin keeps publish rights | U | P0 | Host already Active | `canPublish = true`; no duplicate row | — |
| BE-ROOM-008 | Rooms | Kicked user refused | U | P0 | Kicked participant | 400; no token | — |
| BE-ROOM-011 | Rooms | Non-host cannot approve | U | P0 | `ROOM_LIVE_PRIVATE` | 400; status unchanged | — |
| BE-ROOM-014 | Rooms | Non-live room mints no token | U | P0 | `ROOM_ENDED` | 400; `GenerateToken` never called | — |
| BE-ROOM-015 | Rooms | Non-participant gets no state or token | U | P0 | `ROOM_LIVE_PUBLIC` | 400; no token | — |
| BE-ROOM-018 | Rooms | Non-host cannot start | U | P0 | `ROOM_SCHEDULED_FUTURE` | 400 | — |
| BE-ROOM-019 | Rooms | Scheduled start full path | U | P0 | 3 reminders, 3 tokens | Live + host + notifications + pushes + reminders cleared | — |
| BE-ROOM-021 | Rooms | Non-host cannot end | U | P0 | `ROOM_LIVE_PUBLIC` | 400; `CloseRoomAsync` not called | — |
| BE-ROOM-022 | Rooms | End performs full teardown | U | P0 | Room with 1 unmuted speaker | All transitions + events + `CloseRoomAsync` last | — |
| BE-ROOM-023 | Rooms | LiveKit teardown failure does not fail the host | U | P0 | `CloseRoomAsync` throws | Still `Success`; logged | BE-ROOM-022 |
| BE-ROOM-026 | Rooms | Host-disconnect stamped once only | U | P0 | Already-stamped room | `false`; timestamp unchanged | — |
| BE-ROOM-028 | Rooms | Host reconnect clears the countdown | U | P0 | Stamped room | `true`; null afterwards | — |
| BE-ROOM-029 | Rooms | Grace sweep ends through `EndRoomAsync` | U | P0 | 2 expired rooms | Both ended with `host_disconnected` | BE-ROOM-022 |
| BE-ROOM-030 | Rooms | Duration sweep respects per-room deadline | U | P0 | 2 h and 3 h rooms | Only the overdue one ends | BE-ROOM-022 |
| BE-LIVEKIT-001 | LiveKit | Token identity, room and grants | U | P0 | Test credentials | `sub`/`name`/`room`/`roomJoin`/`canSubscribe` correct | — |
| BE-LIVEKIT-002 | LiveKit | `canPublish` honoured both ways | U | P0 | — | Matches the argument exactly | — |
| BE-LIVEKIT-003 | LiveKit | TTL from config, floored at 1 min | U | P0 | TTL 240, then 0 | `exp − iat` correct; floor applied | — |
| BE-LIVEKIT-004 | LiveKit | Signature verifiable only with the real secret | U | P0 | Two secrets | Validates / fails | — |
| BE-LIVEKIT-008 | LiveKit | `EnsureRoomExistsAsync` never throws | U | P0 | Client throws | Returns `false`; 60 s floor | — |
| BE-LIVEKIT-031 | LiveKit | TTL and empty-timeout exceed max room length | S | P0 | Bound settings | ≥ 3 h + overtime | — |
| BE-LIVEKIT-040 | Webhook | Unsigned / wrong-signature / checksum mismatch | A | P0 | Signed bodies | 401 each; no eviction, no event | INFRA-004 |
| BE-LIVEKIT-042 | Webhook | Kicked participant is evicted on rejoin | A | P0 | Kicked row, Live room | `RemoveParticipantAsync` called | INFRA-007 |
| BE-LIVEKIT-043 | Webhook | Eviction reason matrix | A | P0 | All room/participant states | Correct reason; Active+Live not evicted | BE-LIVEKIT-042 |
| BE-LIVEKIT-044 | Webhook | Enforcement ignores the analytics flag | A | P0 | Flag off | Still evicts; returns 200 | BE-LIVEKIT-042 |
| BE-LIVEKIT-023 | LiveKit | `RemoveParticipant` disconnects one only | I | P0 | LiveKit container, 2 participants | One drops, room survives | INFRA-016 |
| BE-LIVEKIT-024 | LiveKit | `CloseRoom` disconnects everyone | I | P0 | LiveKit container, 2 participants | Both dropped | INFRA-016 |
| BE-LIVEKIT-025 | LiveKit | Expired token refused at connect | I | P0 | TTL 1 min | Connection refused | INFRA-016 |
| BE-LIVEKIT-026 | LiveKit | `auto_create` off: token for a deleted room refused | I | P0 | `auto_create: false` | Refused; room not resurrected | INFRA-016 |
| BE-LIVEKIT-030 | LiveKit | Mid-session token refresh | A | P0 | Active participant | New `exp`; refused when ended or non-participant | INFRA-004 |
| BE-HUB-001 | Hubs | `JoinRoom` rejection matrix + `operation_failed` reasons | H | P0 | All participant states | Throws before any token; correct reason | INFRA-004 |
| BE-HUB-008 | Hubs | `ApproveToStage` host-only + capacity | H | P0 | `ROOM_LIVE_STAGE_FULL` | 403-equivalent; `stage_at_capacity` emitted | INFRA-004 |
| BE-HUB-010 | Hubs | `ToggleMic` speaker-time limit; host exempt | H | P0 | Exhausted speaker | `HubException` + `speaker_time_exhausted`; host unaffected | INFRA-004 |
| BE-HUB-012 | Hubs | `KickUser` full effect | H | P0 | On-stage target | Kicked row + `RemoveParticipant` + broadcast + event | INFRA-007 |
| BE-HUB-016 | Hubs | Private room message respects blocks | H | P0 | Blocked pair | `SendMessageError`; nothing persisted | — |
| BE-FLOW-001 | E2E | Create → join → stage → speak → end | A+H | P0 | 2 users | All transitions, call log and event sequence correct | INFRA-004,007 |
| BE-FLOW-002 | E2E | Private room approval flow | A+H | P0 | 2 users | No credential before approval | BE-FLOW-001 |
| BE-FLOW-003 | E2E | Kick removes from DB, hub and media | A+H | P0 | Live room, 2 users | Rejoin and token both refused | BE-FLOW-001 |
| BE-FLOW-004 | E2E | Ban force-disconnects an in-room user | A+H | P0 | Connected user, admin | `ForceDisconnect`, map purged, JWT 401s | BE-SEC-003 |
| BE-FLOW-005 | E2E | Host account deletion tears down the room | A | P0 | Host + listener | Room Ended, LiveKit closed, token refused | BE-DB-013 |

### Wave 3 — P0 persistence, jobs and moderation

| ID | Feature | Scenario | Type | Pri | Preconditions | Expected result | Deps |
| --- | --- | --- | --- | --- | --- | --- | --- |
| BE-DB-001 | Schema | Migrations apply to an empty DB | I | P0 | Empty container | Success; zero pending | INFRA-003 |
| BE-DB-002 | Schema | Model matches migrations | I | P0 | Migrated DB | No pending model changes | BE-DB-001 |
| BE-DB-003 | Constraints | `FriendRequest` unique pair | I | P0 | 2 users | Duplicate rejected; reverse allowed | BE-DB-001 |
| BE-DB-004 | Constraints | `BlockedDevices` filtered unique index | I | P0 | 1 user | Duplicate rejected; two NULL ids allowed | BE-DB-001 |
| BE-DB-006 | Constraints | `UserEvents.EventId` unique | I | P0 | — | Duplicate rejected | BE-DB-001 |
| BE-DB-011 | FKs | Cannot delete a user with participations | I | P0 | Participant row | Delete refused (Restrict) | BE-DB-001 |
| BE-DB-012 | FKs | Cannot delete a room host | I | P0 | Hosted room | Delete refused | BE-DB-001 |
| BE-DB-013 | Deletion | `DeleteAccountAsync` end-to-end | A | P0 | Fully-populated user | Rooms ended, FK rows cleared, events `UserId = NULL` | INFRA-004,007 |
| BE-DB-015 | Transactions | Register rolls back and deletes both blobs | A | P0 | Role removed | No user row; both files deleted | INFRA-007 |
| BE-DB-018 | Devices | Re-login never clears an existing block | I | P0 | Blocked (user, device) | `IsBlocked` and `BlockedAt` unchanged | BE-DB-004 |
| BE-DB-021 | Queries | Expired-grace filter | I | P0 | 4 rooms in different states | Only the expired one; tracked | BE-DB-001 |
| BE-DB-022 | Queries | Live-started-before filter | I | P0 | Mixed rooms | Non-null `WentLiveAt` before cutoff only | BE-DB-001 |
| BE-DB-023 | Blocks | `IsBlockedAsync` is symmetric | I | P0 | A blocks B | True in both directions | BE-DB-001 |
| BE-JOB-001 | Jobs | Grace sweep ends only abandoned rooms | I | P0 | 4 rooms | R1 ended + notified; others untouched | BE-ROOM-029 |
| BE-JOB-003 | Jobs | Reconnect inside the window cancels the sweep | I | P0 | Stamped then cleared | Room stays Live | BE-JOB-001 |
| BE-JOB-004 | Jobs | Duration sweep per-room deadline | I | P0 | 4 rooms | Only the overdue one ends | BE-ROOM-030 |
| BE-JOB-006 | Jobs | Event batch persists | I | P0 | N queued events | N rows with `RoomId` promoted | BE-DB-001 |
| BE-JOB-007 | Jobs | Duplicate falls back to per-row insert | I | P0 | 10 events, 1 colliding | 9 persisted, 1 discarded, 0 dead-lettered | BE-DB-006 |
| BE-JOB-008 | Jobs | Duplicate-key classification on SQL Server | I | P0 | Real 2601/2627 + NOT NULL | True / false respectively | BE-DB-006 |
| BE-JOB-009 | Jobs | Permanent failure dead-letters | I | P0 | Unwritable table | Dead-letter rows with reason | BE-JOB-006 |
| BE-JOB-012 | Jobs | Aggregation cycle is re-runnable | I | P0 | Seeded day | Identical rows; checkpoint advances once | BE-DB-008 |
| BE-JOB-013 | Jobs | Snapshot idempotent per (Date, MetricKey) | I | P0 | Seeded state | One row per key; no gap | BE-DB-008 |
| BE-ADMIN-001 | Moderation | Ban applies every consequence | U | P0 | Active user with token | Lockout, voice deleted, tokens cleared, data-only push, event | — |
| BE-ADMIN-002 | Moderation | Re-activation clears all lockout state | U | P0 | Banned user | No ghost-ban | — |
| BE-ADMIN-009 | Moderation | Bulk de-duplicates and excludes self | U | P0 | `[A,A,admin,B]` | A once; admin fails; B succeeds | — |
| BE-ADMIN-011 | Moderation | Block device and email | U | P0 | User with 3 devices | Banned + 3 devices blocked + tokens cleared | — |
| BE-SUP-003 | Moderation | `Mute24h` consistency | U | P0 | Open report | Single instant shared by lockout and push | — |
| BE-SUP-004 | Moderation | `BanUser` action | U | P0 | Open report | Lockout, force logout, data-only push, token cleared last | — |
| BE-SUP-009 | Support | `IsFromAdmin` is server-determined | U | P0 | User message | Always `false` | — |
| BE-SUP-013 | Support | Wrong admin cannot reply | U | P0 | Chat claimed by X | 400 for Y | — |
| BE-CHAT-001 | Chat | Block enforced in both directions | U | P0 | Blocked pair | 400; nothing persisted or pushed | — |
| BE-CHAT-005 | Chat | Push body is a preview, never raw content | U | P0 | JSON content | `MessagePreview` used | — |
| BE-PROF-001 | Profile | Non-friends see no bio or MBTI | U | P0 | Non-friend pair | Both null | — |
| BE-BLOCK-002 | Blocks | Blocking burns the target's session | U | P0 | Target with refresh token | Token nulled; event tracked | — |
| BE-DEVICE-002 | Devices | Registry failure never breaks login | U | P0 | Repo throws | `false`; login unaffected | — |
| BE-EVENT-001 | Events | `Track` never throws | U | P0 | Unserializable payload | No exception | — |
| BE-EVENT-002 | Events | Channel-full drop is counted | U | P0 | Capacity-1 full channel | Metric + warning | — |
| BE-EVENT-005 | Events | IP hash is salted; null without a salt | U | P0 | Two salts / none | Different hashes / null | — |
| BE-PUSH-001 | Push | Alert payload shape | U | P0 | Title + body | Notification attached; APNs alert/10 | — |
| BE-PUSH-002 | Push | Data-only payload shape | U | P0 | Empty title + body | No Notification; background/5; `ContentAvailable` | — |
| BE-PUSH-004 | Push | Caller dictionary never mutated | U | P0 | One dict, 3 sends | Original keys only | — |
| BE-PUSH-005 | Push | Serialized payload never reaches the tray | U | P0 | JSON body | `OpaqueBodyFallback` + warning | — |
| BE-PUSH-008 | Push | Missing token is a recorded failure | U | P0 | Empty token | `missing_token` attempt + result | — |
| BE-PUSH-009 | Push | Firebase uninitialised is recorded | U | P0 | `DefaultInstance` null | `firebase_not_initialised`; no throw | — |
| BE-PUSH-011 | Push | Invalid-token classification | U | P0 | 4 error codes | `tokenInvalidated` only for Unregistered/InvalidArgument | — |
| BE-PUSH-012 | Push | Token never logged in full | U | P0 | 160-char token | Last 8 chars only | — |
| BE-PUSH-020 | Push | One device, one recipient | A | P0 | A then B register T | A tokenless; no push to A | BE-AUTH-021 |
| BE-PUSH-021 | Push | Banned users stop receiving pushes | A | P0 | Ban applied | `missing_token` recorded | BE-ADMIN-001 |
| BE-PUSH-024 | Push | No push producer blocks its caller | A | P0 | Throwing push fake | Every producer still succeeds | INFRA-007 |
| BE-AUTH-003 | Auth | Voice upload failure aborts cleanly | U | P0 | `Error:FakeVoice` | No user; image never uploaded | — |
| BE-AUTH-004 | Auth | Image failure deletes the orphaned voice | U | P0 | Image error | One `DeleteVoice` | — |
| BE-AUTH-005 | Auth | Mid-flight failure deletes both blobs | U | P0 | Role assignment throws | Rollback + both deletes | — |
| BE-AUTH-018 | Auth | Deletion ends hosted rooms properly | U | P0 | Live + Scheduled hosted | `EndRoomAsync` twice with the right reason | — |
| BE-AUTH-019 | Auth | Deletion clears Restrict-FK rows first | U | P0 | Populated user | Four `ExecuteDeleteAsync` before delete | — |
| BE-API-ROOM-002 | Room API | `POST /Join` full case set | A | P0 | Room fixtures | Per D.2 | BE-FLOW-001 |
| BE-API-ROOM-004 | Room API | `GET /State` full case set | A | P0 | Room fixtures | Per D.2; no token when refused | BE-FLOW-001 |
| BE-API-ROOM-009 | Room API | `GET /Token` full case set | A | P0 | Room fixtures | Trimmed shape; refusals mint nothing | BE-API-ROOM-004 |
| BE-API-ROOM-008 | Room API | `POST /End` incl. LiveKit failure | A | P0 | `ROOM_LIVE_PUBLIC` | 200 even when teardown throws | BE-ROOM-023 |
| BE-API-ADMIN-003 | Admin API | `ChangeStatus` incl. self-guard and fan-out | A | P0 | `ADMIN_1`, targets | Per D.3 | BE-FLOW-004 |
| BE-API-ADMIN-006 | Admin API | `BlockDeviceAndEmail` | A | P0 | `ADMIN_1`, target with devices | Per D.3 | BE-ADMIN-011 |
| BE-API-SUP-005 | Support API | Report actions (4-way theory) | A | P0 | Open reports | Per D.8 | BE-SUP-003,004 |
| BE-API-SUP-008 | Support API | Unassigned admin cannot reply | A | P0 | Claimed chat | 400 | BE-SUP-013 |
| BE-API-AUTH-011 | Auth API | `DELETE /DeleteAccount` incl. LiveKit down | A | P0 | Populated user | Deleted regardless | BE-DB-013 |
| BE-SMOKE-001…010 | Deploy | Production smoke suite | A | P0 | Deployed build, smoke account | Per I.5 | INFRA-014 |
| BE-PERF-003 | Perf | Event-pipeline saturation baseline | L | P0 | Staging, flags on | Drop rate measured; API p95 stable | INFRA-017 |

### Wave 4 — P1 (high)

| ID | Feature | Scenario | Type | Pri | Preconditions | Expected result | Deps |
| --- | --- | --- | --- | --- | --- | --- | --- |
| BE-ROOM-002 | Rooms | Future start → Scheduled | U | P1 | Future date | No host row, no LiveKit call | — |
| BE-ROOM-003 | Rooms | Invalid `DurationHours` | U | P1 | {0,1,4,24,−1} | 400; nothing persisted | — |
| BE-ROOM-009 | Rooms | Capacity boundary counts PendingApproval | U | P1 | `ROOM_LIVE_FULL` | N−1 admits, N refuses | — |
| BE-ROOM-010 | Rooms | Join blocked by room status | U | P1 | Scheduled/Ended/Cancelled | Correct message; no token | — |
| BE-ROOM-012 | Rooms | Approve happy path | U | P1 | Pending participant | Active + notification + push + event | — |
| BE-ROOM-016 | Rooms | Stage member gets a publishing token | U | P1 | On-stage non-host | `canPublish = true` | — |
| BE-ROOM-027 | Rooms | Disconnect-stamp guards | U | P1 | 3 invalid cases | `false`; no write | — |
| BE-ROOM-031 | Rooms | Null `WentLiveAt` exempt from duration | U | P1 | Legacy row | Skipped | — |
| BE-ROOM-032 | Rooms | Leave closes the mic segment | U | P1 | Unmuted participant | Seconds accrued; `mic_deactivated` | — |
| BE-AUTH-002 | Auth | Duplicate email | U | P1 | Existing email | 400; no upload | — |
| BE-AUTH-008 | Auth | Unconfirmed email blocks login | U | P1 | `UNCONFIRMED_1` | 400; no token | — |
| BE-AUTH-016 | Auth | Refresh rotation | U | P1 | Valid refresh | New value, +7 days, device touched | — |
| BE-AUTH-017 | Auth | Revoke clears refresh + FCM | U | P1 | Both set | Both nulled; second call idempotent | — |
| BE-AUTH-020 | Auth | Clean error on remaining FK refs | U | P1 | `DbUpdateException` | Friendly 400, not 500 | — |
| BE-AUTH-022 | Auth | Re-record only in `ReRecord` | U | P1 | 4 statuses | Refused except ReRecord | — |
| BE-AUTH-025 | Auth | Wrong current password | U | P1 | Bad password | Identity errors surfaced | — |
| BE-ADMIN-003 | Moderation | Idempotent activation event | U | P1 | Activation | Deterministic `eventKey` | — |
| BE-ADMIN-005 | Moderation | Undefined status enum | U | P1 | `(UserStatus)99` | 400 | — |
| BE-ADMIN-006 | Moderation | Push/email failure does not fail the transition | U | P1 | Both throw | Still `Success` | — |
| BE-ADMIN-008 | Moderation | Bulk batch cap | U | P1 | 201 / 200 ids | 400 / accepted | — |
| BE-ADMIN-010 | Moderation | Partial bulk failure returns 200 | U | P1 | 3 ids, 1 unknown | Counts + per-item messages | — |
| BE-SUP-001 | Support | Report needs a target | U | P1 | Both nulls | 400 | — |
| BE-SUP-006 | Support | Room-only report rejects user actions | U | P1 | Room-only report | 400 for 3 actions | — |
| BE-SUP-008 | Support | Pending-chat message cap | U | P1 | 3 pending messages | 4th refused | — |
| BE-SUP-010 | Support | Pending chats do not push | U | P1 | `AdminId` null / set | 0 / 1 push | — |
| BE-SUP-011 | Support | Only pending chats claimable | U | P1 | Active chat | 400 | — |
| BE-SUP-012 | Support | Concurrent claim loses gracefully | U | P1 | Concurrency exception | Friendly 400 | — |
| BE-SUP-016 | Support | Support push never throws | U | P1 | Push throws | Message still saved | — |
| BE-CHAT-006 | Chat | Push failure does not fail the message | U | P1 | Push throws | Persisted + 200 | — |
| BE-CHAT-007 | Chat | Blocked pair cannot read history | U | P1 | Blocked pair | 400 | — |
| BE-FRIEND-002 | Friends | Duplicate pending / already friends | U | P1 | Existing relation | Correct 400 | — |
| BE-FRIEND-003 | Friends | Rejected relation revived, not duplicated | U | P1 | Rejected row | Same row → Pending | — |
| BE-FRIEND-004 | Friends | Unique-index race rolls back | U | P1 | `DbUpdateException` | No orphan notification | — |
| BE-FRIEND-006 | Friends | Only the receiver can respond | U | P1 | No pending request | 400 | — |
| BE-BLOCK-001 | Blocks | Self-block by id and by email | U | P1 | Own id/email | 400 each | — |
| BE-PROF-003 | Profile | Old picture deleted only after success | U | P1 | Success / failure | Correct blob deleted | — |
| BE-DEVICE-001 | Devices | Null device is a silent no-op | U | P1 | No headers | `false`; no repo call | — |
| BE-DEVICE-003 | Devices | Blank device id is not blocked | U | P1 | `""`/`" "`/null | `false` | — |
| BE-EVENT-003 | Events | Deterministic GUID derivation | U | P1 | Same key twice | Stable, RFC-4122 v5 | — |
| BE-EVENT-004 | Events | `roomId` promotion, case-insensitive | U | P1 | Both casings | Column populated | — |
| BE-EVENT-006 | Events | Flags are conjunctive | U | P1 | 4 combinations | High-freq requires new-event | — |
| BE-EVENT-007 | Events | `MessagePreview` behaviour | U | P1 | JSON / long / prose | Fallback / truncated / passthrough | — |
| BE-PUSH-003 | Push | Partial alert counts as an alert | U | P1 | Title-only, body-only | Both alerts | — |
| BE-PUSH-006 | Push | Body truncation at 240 | U | P1 | 239/240/241 chars | Ellipsis only past the limit | — |
| BE-PUSH-010 | Push | Attempt/result correlation | U | P1 | Any send | One of each, shared id | — |
| BE-PUSH-013 | Push | Unexpected exception recorded | U | P1 | Timeout thrown | Result event + normal return | — |
| BE-PUSH-022 | Push | Reminder fan-out targeting | A | P1 | 5 reminders, 3 tokens | 5 rows, 3 pushes, reminders cleared | BE-ROOM-019 |
| BE-PUSH-023 | Push | `data.type` routing contract | A | P1 | Each producer | Documented type + payload keys | INFRA-007 |
| BE-LIVEKIT-006 | LiveKit | `ToHttpHost` translation | U | P1 | 3 schemes | Correct mapping | — |
| BE-LIVEKIT-007 | LiveKit | Audit logging never breaks issuance | U | P1 | Throwing/null logger | Token still returned | — |
| BE-LIVEKIT-009 | LiveKit | Close/Remove rethrow | U | P1 | Client throws | Propagates | — |
| BE-LIVEKIT-020 | LiveKit | Room lifecycle round-trip | I | P1 | Container | Create/idempotent/delete | INFRA-016 |
| BE-LIVEKIT-021 | LiveKit | Issued token connects; identity = user GUID | I | P1 | Container | Correlation holds | INFRA-016 |
| BE-LIVEKIT-022 | LiveKit | Publish grant enforced; live promotion works | I | P1 | Container | No reconnect needed | INFRA-016 |
| BE-LIVEKIT-027 | LiveKit | Empty timeout outlasts the longest room | I | P1 | Container | Room survives the quiet period | BE-LIVEKIT-031 |
| BE-LIVEKIT-028 | LiveKit | Real webhook is emitted and verified | I | P1 | Container + TestServer | Signature accepted; event tracked | INFRA-016 |
| BE-LIVEKIT-032 | LiveKit | ICE servers on every credential path | A | P1 | Config with TURN | STUN + TURN present in all 4 | INFRA-004 |
| BE-LIVEKIT-045 | Webhook | DB failure during enforcement | A | P1 | Repo throws | 200; logged; no retry storm | BE-LIVEKIT-042 |
| BE-HUB-002 | Hubs | Rejoin preserves `JoinedAt` | H | P1 | `Left` participant | `LastJoinedAt`, `RejoinCount+1`, `isRejoin` | INFRA-004 |
| BE-HUB-004 | Hubs | Host disconnect broadcasts the countdown | H | P1 | Live room | `HostDisconnected` + absolute deadline | INFRA-004 |
| BE-HUB-006 | Hubs | Host reconnect broadcast | H | P1 | Stamped room | `HostReconnected` | BE-HUB-004 |
| BE-HUB-007 | Hubs | Raise/lower hand semantics | H | P1 | Participant | Repeat raise still emitted | INFRA-004 |
| BE-HUB-009 | Hubs | `MoveToAudience` | H | P1 | On-stage target | Segment closed; permission revoked | INFRA-004 |
| BE-HUB-011 | Hubs | `GrantExtraTime` bounds | H | P1 | 0/−5/31/1/30 | Refused / accepted | INFRA-004 |
| BE-HUB-013 | Hubs | Kick survives a LiveKit failure | H | P1 | Remove throws | Kick still commits | BE-HUB-012 |
| BE-HUB-014 | Hubs | `EndRoom` host-only + purge | H | P1 | Live room | Broadcast + connections purged | INFRA-004 |
| BE-HUB-017 | Hubs | `ChatHub.SendMessage` | H | P1 | 2 users | Error paths + delivery | INFRA-004 |
| BE-DB-005 | Constraints | `DeviceId` not unique across users | I | P1 | 2 users, 1 device | Both allowed; block applies to the device | BE-DB-004 |
| BE-DB-008 | Constraints | Read-model grain uniqueness | I | P1 | 6 read models | Duplicates rejected | BE-DB-001 |
| BE-DB-016 | Transactions | Friend-request rollback leaves no notification | I | P1 | Racing insert | No orphan | BE-DB-003 |
| BE-DB-017 | Transactions | Concurrent device registration | I | P1 | 5 parallel calls | One row; all succeed | BE-DB-004 |
| BE-DB-020 | Queries | Feed ordering | I | P1 | Mixed rooms | Live first, by activity, then start date | BE-DB-001 |
| BE-DB-028 | Queries | Analytics T-SQL runs on SQL Server | I | P1 | 30-day fixture | Every method returns a valid DTO | INFRA-009 |
| BE-JOB-002 | Jobs | Grace sweep idempotent | I | P1 | Already swept | 0 ended; no second event | BE-JOB-001 |
| BE-JOB-005 | Jobs | A failing cycle does not kill the loop | I | P1 | First sweep throws | Next cycle succeeds | BE-JOB-001 |
| BE-JOB-010 | Jobs | Shutdown drains the channel | I | P1 | 250 buffered | All persisted or dead-lettered | BE-JOB-006 |
| BE-JOB-011 | Jobs | Retention purge | I | P1 | 100 old, 50 new | 100 deleted | BE-DB-001 |
| BE-SEC-015 | Security | Lockout after 5 failures | A | P1 | `ACTIVE_1` | 6th refused even if correct | BE-AUTH-006 |
| BE-SEC-016 | Security | Session survival matrix | A | P1 | Ban / password change | Documented as-is | BE-SEC-003 |
| BE-SEC-018 | Security | Manipulated identifiers | A | P1 | Hostile id set | 404, never 500 | INFRA-012 |
| BE-SEC-022 | Security | Signing-key length ≥ 32 bytes | S | P1 | Config | Passes | — |
| BE-SEC-023 | Security | CORS allow-list behaviour | A | P1 | Configured + absent | No header for foreign origins | INFRA-004 |
| BE-SEC-027 | Security | Over-long device headers truncated | A | P1 | 5000-char header | Truncated to 200; no 500 | BE-SEC-010 |
| BE-SEC-030 | Security | Rate limiter engages | A | P1 | Production limiter | 429 at 101 | INFRA-011 |
| BE-SEC-034 | Uploads | `subFolder` cannot escape the prefix | U | P1 | Traversal inputs | Key stays under `Uploads/` | — |
| BE-SEC-035 | Uploads | Size limits | U | P1 | At/over each limit | Correct accept/reject | — |
| BE-SEC-037 | Replay | Webhook replay is idempotent | A | P1 | Same signed body twice | No incorrect eviction | BE-LIVEKIT-042 |
| BE-SEC-039 | Replay | Client events are not deduplicated | A | P1 | Same call twice | Two rows; documented | BE-SEC-029 |
| BE-INT-002 | Storage | Voice round-trip + magic-byte rejection | I | P1 | MinIO container | Upload succeeds; fake rejected, nothing written | INFRA-016 |
| BE-INT-003 | Storage | S3 outage → error string, not exception | U | P1 | Throwing S3 | `Error:ServerException` → 400 | — |
| BE-INT-004 | Email | SMTP failure does not 500 registration | A | P1 | Throwing email | 400 with rollback | BE-DB-015 |
| BE-FLOW-006 | E2E | Support chat lifecycle | A+H | P1 | 2 admins, 1 user | Transitions + alerts correct | BE-SUP-013 |
| BE-FLOW-007 | E2E | Report → Mute24h → login refused → expiry | A | P1 | Open report | Forbidden then allowed | BE-SUP-003 |
| BE-FLOW-008 | E2E | Block prevents chat both ways | A+H | P1 | 2 users | Both refused; restored on unblock | BE-CHAT-001 |
| BE-API-* (P1 rows) | All APIs | Remaining H/NA/IA/FB/IV/MR/CF/BR/DF/BE cases in D.1–D.9 not listed in Waves 1–3 | A | P1 | Standard cast | Per the matrix | INFRA-009 |
| BE-PERF-001 | Perf | Room-join surge | L | P1 | Staging | p95 < 1500 ms; < 1% errors | INFRA-017 |

### Wave 5 — P2 / P3 (medium and low)

| ID range | Feature | Scenario summary | Type | Pri |
| --- | --- | --- | --- | --- |
| BE-ROOM-004, 013, 017, 020, 024, 025, 033, 034, 035, 036 | Rooms | Image-failure tolerance, idempotent approve, participant filtering, terminal-state starts, double-end, legacy airtime fallback, no-op leave, feed counts, reminder rules, reminder-off telemetry | U | P2 |
| BE-AUTH-026 | Auth | MBTI for an unknown user | U | P3 |
| BE-ADMIN-004, 007, 012, 013 | Moderation | No-op transition, ReRecord copy, zero-device ban message, unknown email | U | P2 |
| BE-SUP-002, 005, 007, 014, 015 | Support | Report happy path, warn action, reject action, admin reply happy path, close guards | U | P2 |
| BE-CHAT-002, 003, 004 | Chat | Self-send, empty content, first-message flag | U | P2 |
| BE-FRIEND-001, 005, 007 | Friends | Self-request, accept path, pending-removal cleanup | U | P2 |
| BE-BLOCK-003, 004 | Blocks | Unknown target, idempotent unblock | U | P2 |
| BE-PROF-002, 004 | Profile | Self-redirect, avatar preset | U | P3 |
| BE-DEVICE-004, 005 | Devices | Promote existing row, already-blocked no-op | U | P2/P3 |
| BE-PUSH-007, 014 | Push | Null body passthrough, `targetUserId` extraction | U | P2 |
| BE-LIVEKIT-005, 041, 046, 047 | LiveKit | Token freshness, empty webhook body, telemetry payload, unparseable ids | U/A | P2 |
| BE-HUB-003, 005, 015, 018 | Hubs | Stale-connection replacement, participant disconnect, group chat ignores blocks, SupportHub admin group | H | P2 |
| BE-DB-007, 009, 010, 014, 024, 025, 026, 027 | Persistence | Composite keys, legacy column names, room cascade, report FK behaviour, device block timestamps, unblock demotion, support query sets, chat summaries | I | P2 |
| BE-JOB-014 | Jobs | Backfill resume/skip accounting | I | P2 |
| BE-SEC-031 | Security | Anonymous ticket abuse surface | A | P2 |
| BE-INT-001 | Storage | Image round-trip to MinIO | I | P2 |
| BE-API-000 | API | Route-inventory guard | A | P2 |
| BE-API-ANL-001/002 | Analytics API | 30 routes × H/NA/IA/FB/IV/BE + backfill | A | P2 |
| BE-PERF-002, 004, 006 | Perf | Feed N+1, sweeps under load, analytics at volume | L | P2 |
| BE-PERF-005 | Perf | Chat write throughput | L | P3 |

### Wave 6 — Manual QA (per release)

| ID | Feature | Scenario | Type | Pri | Environment / preconditions | Expected result | Deps |
| --- | --- | --- | --- | --- | --- | --- | --- |
| MQ-LK-001 | Audio | Two-way audio, public room | M | P0 | STAGING, 2 Android, different networks | Audio both ways < 2 s; indicators correct | BE-FLOW-001 |
| MQ-LK-002 | Audio | Cellular-only participant (TURN relay) | M | P0 | STAGING, mobile data only | Audio connects | BE-LIVEKIT-032 |
| MQ-LK-003 | Rooms | Host returns inside the grace window | M | P0 | Live room, 90 s grace | Countdown shown; room resumes | BE-JOB-003 |
| MQ-LK-004 | Rooms | Host never returns | M | P0 | As above, > 100 s | Room ends with the grace message | BE-JOB-001 |
| MQ-LK-005 | Rooms | Host ends while others speak | M | P0 | 2 speakers talking | Everyone disconnected from audio | BE-ROOM-022 |
| MQ-LK-006 | Moderation | Kick cuts audio immediately | M | P0 | On-stage target | < 2 s cut; others cannot hear them | BE-FLOW-003 |
| MQ-LK-007 | Security | Kicked user cannot reconnect with a cached token | M | P0 | Kick then app restart | Evicted by the webhook | BE-LIVEKIT-042 |
| MQ-LK-008 | Rooms | Duration limit reached | M | P1 | Long session | Ended with the duration message | BE-JOB-004 |
| MQ-LK-009 | Network | Wi-Fi ↔ cellular, lift, tunnel | M | P1 | Live session | Auto-reconnect; token re-fetch on expiry | BE-LIVEKIT-030 |
| MQ-LK-010 | Rooms | Speaker time limit and extra time | M | P1 | Non-host speaker | Refused then granted; host exempt | BE-HUB-010 |
| MQ-LK-011 | Rooms | Stage capacity full | M | P2 | Stage at capacity | Host sees "Stage is full" | BE-HUB-008 |
| MQ-LK-012 | Auth | Host deletes account mid-session | M | P1 | Live room + listener | Room ends for all | BE-FLOW-005 |
| MQ-LK-013 | Rooms | Private room approval on device | M | P2 | Private room | Waiting state, push, then audio | BE-FLOW-002 |
| MQ-PUSH-001 | Push | Android foreground | M | P0 | App open | No conflicting tray pop-up; in-app updates | BE-PUSH-001 |
| MQ-PUSH-002 | Push | Android background | M | P0 | App backgrounded | Tray notification with prose body; deep link works | BE-PUSH-005 |
| MQ-PUSH-003 | Push | Android terminated | M | P0 | App swiped away | Notification arrives; cold start lands on the room | BE-PUSH-001 |
| MQ-PUSH-004 | Push | Data-only ban notification | M | P0 | App backgrounded, ban applied | No tray pop-up; banned screen; mute vs ban distinguished | BE-PUSH-002 |
| MQ-PUSH-005 | Push | Deep-link routing per `type` | M | P1 | One of each type | Correct screen and entity | BE-PUSH-023 |
| MQ-PUSH-006 | Push | Doze delivery | M | P1 | Idle 30+ min | Prompt delivery | BE-PUSH-001 |
| MQ-PUSH-007 | Push | Notification permission denied | M | P2 | Android 13+ denial | Graceful degradation | — |
| MQ-PUSH-008 | Push | Reinstall / token rotation | M | P1 | Reinstall + new account | No cross-delivery | BE-PUSH-020 |
| MQ-QA-001 | Onboarding | Full registration → activation | M | P0 | STAGING, real device | Every stage correct; email renders | BE-SEC-001 |
| MQ-QA-002 | Verification | Re-record flow, Arabic copy | M | P1 | `ReRecord` set | RTL text correct everywhere | BE-ADMIN-007 |
| MQ-QA-003 | Moderation | Ban while in a live room | M | P0 | User on stage | Force-disconnect < 2 s; re-login refused | BE-FLOW-004 |
| MQ-QA-004 | Security | Device ban blocks a new account | M | P0 | Banned handset | All requests refused | BE-SEC-025 |
| MQ-QA-005 | Auth | Account deletion from the user's view | M | P1 | Populated account | Logged out; room ended; absent from lists | BE-DB-013 |
| MQ-QA-006 | Support | Support chat on device | M | P2 | User + admin | Real-time and push both work | BE-FLOW-006 |
| MQ-QA-007 | Admin | Dashboard end-to-end incl. Coach scope | M | P1 | Admin + Coach accounts | Correct permitted subsets | BE-SEC-008 |
| MQ-QA-008 | i18n | Arabic and emoji integrity | M | P2 | Multilingual content | Intact everywhere incl. push bodies | — |

### Backlog totals

| Wave | Content | Backlog items | Concrete tests (approx.) |
| --- | --- | --- | --- |
| 0 | Infrastructure | 18 tasks | — |
| 1 | P0 security / auth | 44 | ~95 |
| 2 | P0 rooms / LiveKit / real-time | 41 | ~85 |
| 3 | P0 persistence / jobs / moderation | 62 | ~110 |
| 4 | P1 | ~95 | ~160 |
| 5 | P2 / P3 | ~70 | ~120 |
| 6 | Manual QA | 29 | 29 |
| — | Load | 6 | 6 |
| **Total** | | **~365 backlog items** | **~605 test cases** (468 existing unit tests continue to run alongside) |

---

## Prioritisation rationale

Priority is assigned by **what breaks in production if the behaviour is wrong**, not by how easy the
test is to write.

- **P0 — critical production, business or security risk.** Anything that can (a) let an unauthorised
  party act or listen, (b) issue or fail to revoke a media credential, (c) lose or corrupt data,
  (d) leave a room live forever or end it wrongly, (e) let a banned user back in, or (f) break the
  deployment gate itself. These must pass on every PR; a failure blocks merge and blocks release.
- **P1 — high.** Correctness of the main user journeys and the resilience paths that keep a failure in
  one dependency (FCM, SMTP, S3, LiveKit) from becoming a user-visible outage. A failure blocks
  release but not necessarily merge to a feature branch.
- **P2 — medium.** Secondary behaviours, pagination, telemetry payload detail, idempotency niceties.
  Tracked and fixed, but a release can proceed with a known, documented failure.
- **P3 — low.** Cosmetic or rarely-hit paths kept for completeness.

There is deliberately **no coverage percentage target**. The ratchet in K.1 prevents regression; what
is actually tested is governed by this backlog, because a percentage can be satisfied by testing
getters while `GetRoomStateAsync`'s token guard goes unverified.

---

## Execution order and verification

**Sequencing.** Wave 0 → Wave 1 → Wave 2 → Wave 3 in order; Waves 4–5 run continuously alongside
feature work; Wave 6 executes per release. Wave 0 is a hard prerequisite: without
`CocorraApiFactory` and the SQL Server fixture, roughly two-thirds of the backlog cannot be written
at all.

**Two items should be done first, before any test is written**, because they change what the other
tests can assume:

1. **INFRA-001** — the one-line `Program.cs` change.
2. **BE-SEC-020 remediation** — move the secrets in `Cocorra.API/appsettings.json` to environment
   variables / user-secrets following the existing `Analytics:IpHashSalt` pattern, and rotate them.
   Until then BE-SEC-020 is a known-failing test, and the CI workflows would otherwise need the real
   production credentials to run.

**How to verify this plan has been executed correctly:**

| Step | Command / action | Expected |
| --- | --- | --- |
| 1 | `dotnet build Cocorra.sln -c Release -warnaserror` | Clean, no NuGet audit failures |
| 2 | `dotnet test Cocorra.Tests -c Release` | 468 existing + new unit tests green, < 60 s |
| 3 | `dotnet test Cocorra.IntegrationTests -c Release --filter "Category!=LiveKit&Category!=Storage&Category!=RateLimit"` | Green; SQL Server container starts and migrates |
| 4 | `dotnet test Cocorra.IntegrationTests -c Release` (nightly set) | Green with LiveKit + MinIO containers |
| 5 | Open a throwaway PR | `pr.yml` runs, posts coverage, is a required check |
| 6 | Push to `prod` on a branch with a deliberately failing test | `deploy` is **skipped** — the gate works |
| 7 | Deploy to staging, run `BE-SMOKE-001…010` | All 10 pass |
| 8 | Break one guard on purpose — e.g. delete the `room.Status != Live` check in `GetRoomStateAsync` | **BE-ROOM-014**, **BE-API-ROOM-004** and **BE-API-ROOM-009** fail. If they do not, the suite is not yet doing its job |
| 9 | Run `k6 run Cocorra.LoadTests/room-join.js` against staging | Thresholds in BE-PERF-001 met |
| 10 | Execute the Wave 6 P0 manual set on a physical Android handset | All pass; results recorded against the MQ ids |

Step 8 is the real acceptance criterion: a test suite is only worth its runtime if removing a
production guard turns it red.

---

## Status and maintenance

**Status:** approved roadmap, not yet executed. No test code has been written and no production code
has been changed as part of producing this document. The only production edit the plan calls for is
**INFRA-001** (`public partial class Program { }`), which happens when Wave 0 begins.

**Keeping it current.** The two mechanisms that stop this document drifting from the codebase:

- **BE-API-000** (route-inventory guard) fails the build when an endpoint is added without a
  corresponding entry in the API matrix (Section D).
- Every new test file carries an `// Owns: BE-XXX-NNN` header, so a backlog id can be traced to the
  code that satisfies it and unclaimed ids are visible.

When a test is written, mark its backlog row done rather than deleting it — the backlog is the record
of what was decided to be worth testing, including the items deliberately left at P2/P3.

**Source of truth.** Every endpoint, route, setting, constraint and service behaviour cited here was
read from the repository at the commit this document was written against. Routes declared in
`Cocorra.DAL/AppMetaData/Router.cs` with no implementing action are listed in D.11 and deliberately
untested. Behaviours that differ from apparent intent are pinned as-is and recorded in D.12 rather
than silently corrected by a test.

