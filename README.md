# Cocorra Backend

ASP.NET Core backend for Cocorra — a voice-first social app built around live audio rooms. It handles
account signup with voice verification, LiveKit-backed audio rooms, direct and support chat, friend
graphs, moderation/reporting, push notifications, and product analytics.

- **Runtime:** .NET 10 (`net10.0`)
- **Database:** SQL Server via EF Core 10
- **Realtime:** SignalR hubs + LiveKit for audio
- **Storage:** MinIO (S3-compatible) for uploads
- **Push:** Firebase Cloud Messaging (FCM)
- **API docs:** Swagger UI at `/swagger` (enabled in **all** environments, including production)

---

## Solution layout

```
Cocorra.sln
├── Cocorra.API/     Controllers, SignalR hubs, middleware, seeders, DI wiring (Program.cs)
├── Cocorra.BLL/     Business logic — one folder per service under Services/
├── Cocorra.DAL/     EF Core models, AppDbContext, migrations, repositories, DTOs, enums
└── Cocorra.Tests/   xUnit + Moq + EF InMemory
```

Dependencies flow one way: `API → BLL → DAL`. Controllers stay thin and delegate to a BLL service;
services talk to the database through repositories, not `AppDbContext` directly (with a few
deliberate exceptions — `AdminService` holds a context to query `UserEvents` when deciding whether an
activation has already been recorded).

**Response envelope.** BLL services return `Response<T>` / `PagedResponse<T>` built through
`Cocorra.BLL.Base.ResponseHandler` — `Success`, `Created`, `Deleted`, `BadRequest`, `NotFound`,
`Unauthorized`, `Forbidden`, `UnprocessableEntity`, `Paginated`. Controllers unwrap with
`StatusCode((int)result.StatusCode, result)`, so every endpoint returns the same JSON shape:

```json
{ "statusCode": 200, "meta": null, "succeeded": true, "message": "", "errors": null, "data": {} }
```

**Repositories.** `GenericRepositoryAsync<T>` is the base. Be aware that its write methods
(`AddAsync`, `UpdateAsync`, `DeleteAsync`, …) each call `SaveChangesAsync()` internally, so a single
"unit of work" spanning several repository calls needs an explicit transaction via
`BeginTransaction()` — see `FriendService` for the pattern.

---

## Getting started

### Prerequisites

- .NET 10 SDK
- SQL Server (local instance, container, or remote)
- A MinIO bucket (or any S3-compatible endpoint) for uploads
- A Firebase service-account JSON, if you need push notifications locally

### 1. Configure

`Cocorra.API/appsettings.json` holds the key structure but not usable secrets. Supply real values
through User Secrets or environment variables rather than editing the tracked file:

```bash
cd Cocorra.API
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost;Database=CocorraDb;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True;"
dotnet user-secrets set "Analytics:IpHashSalt" "<any-long-random-string>"
```

Every configuration key the app reads:

| Key | Purpose |
|---|---|
| `ConnectionStrings:DefaultConnection` | SQL Server connection string |
| `JWTSetting:securityKey` | HMAC signing key for access tokens |
| `JWTSetting:ValidIssuer` / `ValidAudience` | Validated on every request |
| `EmailSettings:SmtpServer` / `SmtpPort` / `SmtpUser` / `SmtpPass` / `FromEmail` | Transactional email (MailKit) |
| `SeedAdmin:Email` / `Password` | Admin account created by `IdentitySeeder` at startup |
| `LiveKit:ServerUrl` / `ApiKey` / `ApiSecret` | Audio room tokens |
| `Minio:Endpoint` / `AccessKey` / `SecretKey` / `BucketName` / `PublicUrl` | Upload storage |
| `Analytics:IpHashSalt` | **Required.** Salt for IP pseudonymization |

> `Analytics:IpHashSalt` is a hard startup requirement — `Program.cs` throws if it is missing or
> blank. Without it, IP hashes would fall back to a public value and become reversible. Any
> non-empty random string works for local development.

### 2. Create the database

Migrations live in `Cocorra.DAL`, so both projects must be named:

```bash
dotnet tool install --global dotnet-ef
dotnet ef database update --project Cocorra.DAL --startup-project Cocorra.API
```

### 3. Run

```bash
dotnet restore
dotnet build
dotnet run --project Cocorra.API
```

`/` redirects to `/swagger`. Role and admin seeding runs automatically on startup.

### 4. Test

```bash
dotnet test
```

---

## Firebase / push notifications

`Program.cs` looks for `firebase-config.json` in the API project's `ContentRootPath` at startup. The
file is **gitignored and not in the repository** — it must be supplied per environment.

If it is absent, `FirebaseApp.Create` is skipped and the app still boots. `FirebaseMessaging.DefaultInstance`
then returns `null`, and `PushNotificationService` logs an error for every send attempt instead of
delivering anything. If pushes are silently missing, check this first.

- **Local:** drop `firebase-config.json` into `Cocorra.API/`.
- **Docker/VPS:** `docker-compose.yml` bind-mounts `./firebase-config.json` next to the compose file
  into `/app/firebase-config.json`. Because the file is gitignored, the deploy workflow's `scp` step
  does **not** carry it — place it on the server manually once, at `/root/cocorra-app/firebase-config.json`.

`PushNotificationService` never throws: it logs and returns. Callers can `await` it directly without
a `try/catch`. It also sets platform config per message — Android `Priority.High`, and APNs headers
that switch between `alert`/priority 10 (when a title or body is present) and `background`/priority 5
with `content-available` (for data-only pushes), since Apple rejects background pushes sent at
priority 10.

---

## Authentication & authorization

JWT bearer tokens, ASP.NET Core Identity with `ApplicationUser` / `IdentityRole<Guid>`.

Users move through a `UserStatus` lifecycle — `Pending → Active | Rejected | Banned | ReRecord` —
driven by voice verification and admin review. This status is mirrored into a `VerificationStatus`
claim, which the authorization policies enforce:

- **Default policy** (every bare `[Authorize]`): authenticated **and** `VerificationStatus == "Active"`.
- **`"VerificationOnly"` policy:** allows `Pending`, `ReRecord`, or `Active` — for endpoints in the
  verification flow itself, such as re-submitting a voice sample.

Two things worth knowing:

- `OnTokenValidated` re-checks `IsLockedOutAsync` on **every** request, so banning or muting a user
  takes effect immediately rather than at token expiry.
- SignalR clients can't set headers, so `OnMessageReceived` also accepts `?access_token=` for the
  `/hubs/chat`, `/hubs/rooms`, and `/hubs/support` paths.

Identity rules: unique email required; 8+ char passwords with upper, lower, digit, and symbol;
lockout after 5 failed attempts for 15 minutes.

**Rate limiting** is global — a fixed window of 100 requests/minute partitioned by remote IP,
returning `429` when exceeded.

---

## API surface

Swagger UI at `/swagger` is the authoritative reference. Controllers:

| Controller | Area |
|---|---|
| `AuthenticationController` | Register, login, refresh, OTP, FCM token registration |
| `ProfileController` | Profile read/update, avatar and voice uploads |
| `RoomsController` | Create/join/leave rooms, topics, votes, reminders, LiveKit tokens |
| `ChatController` | Direct messages |
| `FriendsController` | Requests, accept/reject, friend list |
| `NotificationsController` | `my-notifications`, mark read, mark all read |
| `SupportController` | Support tickets/chat, reports, admin report actions |
| `AdminController` | User status changes, bulk status, dashboard stats, device blocking |
| `BlockController` | User-to-user blocking |
| `RolesController` | Role management |
| `AnalyticsController` / `EventsController` | Product metrics and event ingestion |

**Routing is not uniform, and this catches people out.** Two conventions coexist:

- **Most controllers** (Admin, Authentication, Rooms, Support, Roles, Block, Analytics, Events) have
  **no** `[Route]` attribute. Each action's path comes from a constant in
  `Cocorra.DAL/AppMetaData/Router.cs`, which builds `Api/V1/{Area}/...` — e.g.
  `[HttpPut(Router.AdminRouting.ChangeStatus)]` resolves to `PUT Api/V1/Admin/User/ChangeStatus/{id}`.
- **Chat, Friends, Notifications, and Profile** instead use `[Route("api/[controller]")]` with
  relative action paths — e.g. `GET api/Notifications/my-notifications`.

So add new endpoints to `Router.cs` if you're extending one of the first group, and don't assume a
path from the controller name. Swagger reflects whatever is actually wired up.

Enums serialize as **strings** (`"Banned"`, not `3`) via a global `JsonStringEnumConverter`. Reading
still accepts numbers, so older clients keep working.

### SignalR hubs

| Hub | Path |
|---|---|
| `RoomHub` | `/hubs/rooms` |
| `ChatHub` | `/hubs/chat` |
| `SupportHub` | `/hubs/support` |

Server-initiated actions (force-logout on ban/mute, room events) go out through
`IRealTimeNotifier`, implemented by `SignalRNotifier` in the API layer.

---

## Notifications: two channels

Anything user-facing should generally go out **both** ways, because they fail differently:

1. A `Notification` row via `INotificationRepository` — durable, readable later from
   `GET api/Notifications/my-notifications`.
2. An FCM push via `IPushNotificationService` — immediate, but best-effort.

Persist first, then push. A push that never lands is invisible unless the row exists.

> One caveat: a permanently banned user can't authenticate, so they can't read their own
> notification rows. Email is the only channel that actually reaches them.

---

## Analytics & event tracking

`IEventTracker.Track(...)` is fire-and-forget: it writes to a bounded in-memory `Channel<UserEvent>`
(capacity 10,000, `DropWrite` when full) and returns without touching the database. Two hosted
services drain and maintain it — `EventFlushService` batches into SQL, `EventCleanupService` prunes
old rows. Tracking calls are therefore cheap and safe on request paths, but events are dropped rather
than queued under extreme load.

IP addresses are never stored raw; they are hashed with `Analytics:IpHashSalt`.

See [`USER_TRACKING_PLAN.md`](USER_TRACKING_PLAN.md), [`ANALYTICS_METRICS_PLAN.md`](ANALYTICS_METRICS_PLAN.md),
and [`MOBILE_TRACKING_GUIDE.md`](MOBILE_TRACKING_GUIDE.md).

---

## Docker & deployment

Local container run:

```bash
docker compose up --build
```

The API listens on `8080` in-container, published to `5000` on the host. Uploads persist in the
`cocorra-uploads` volume mounted at `/app/wwwroot/Uploads`.

`Dockerfile` is a two-stage build that **runs `dotnet test` before publishing** — a failing test
fails the image build.

**CI/CD.** `.github/workflows/deploy.yml` triggers on push to the `prod` branch: it writes
`appsettings.Production.json` from the `APPSETTINGS_JSON` secret, `scp`s the tree to
`/root/cocorra-app` on the VPS, then runs `docker compose up --build -d`. Note that `main` does not
deploy — production ships from `prod`.

---

## Conventions

- **Formatting:** CSharpier, pinned in `dotnet-tools.json`. Note this manifest sits at the repo root
  rather than the conventional `.config/dotnet-tools.json`, so `dotnet tool restore` may not pick it
  up automatically.
- **Comments** explain *why*, not *what* — the existing code is consistent about this. Some inline
  comments are in Arabic; both languages appear in user-facing strings too.
- **Nullable reference types** are enabled across all projects. The build currently carries ~11
  pre-existing warnings; don't add more.

---

## Repository docs

| File | Contents |
|---|---|
| [`ADMIN_DASHBOARD_FRONTEND_HANDOFF.md`](ADMIN_DASHBOARD_FRONTEND_HANDOFF.md) | Admin dashboard API contract |
| [`USER_TRACKING_PLAN.md`](USER_TRACKING_PLAN.md) | Event tracking design and privacy rules |
| [`ANALYTICS_METRICS_PLAN.md`](ANALYTICS_METRICS_PLAN.md) | Metric definitions |
| [`MOBILE_TRACKING_GUIDE.md`](MOBILE_TRACKING_GUIDE.md) | Client-side tracking integration |
