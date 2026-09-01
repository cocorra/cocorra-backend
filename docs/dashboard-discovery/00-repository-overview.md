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
