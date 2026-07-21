# Frontend Handoff Document — Admin Dashboard Module (Cocorra)

## Context

This document is a **handoff deliverable** for the Angular team building the Admin Dashboard against the existing .NET 9 / Clean-Architecture backend (`Cocorra.API` → `Cocorra.BLL` → `Cocorra.DAL`). It maps the real, shipped controllers, DTOs, routes, and authorization rules to the Angular routing, component architecture, and state-management directives the frontend needs.

It is grounded in the actual source. A few points are worth calling out up front:

- Roles are **`Admin`** and **`Coach`**, not `SuperAdmin`/`Moderator`.
- **Enums are now serialized as strings** (a global `JsonStringEnumConverter` is registered in `Program.cs`). `ChangeStatus` now expects `{ "newStatus": "Banned" }`. Reading still tolerates the old integer form, so this is backward-compatible.
- The paginated list endpoints now return a **`PagedResponse<T>`** with first-class `totalCount`, `currentPage`, `pageSize`, **`totalPages`**, **`hasNextPage`**, and `hasPreviousPage` — no more client-side computation from a loose `Meta` bag.
- A **bulk status endpoint** now exists (`PUT /Api/V1/Admin/Users/BulkChangeStatus`) with per-item partial-success reporting (see §5).
- The Users list endpoint supports **search + pagination only** — there is still **no server-side sorting** and no advanced/column filtering.

Source of truth:
- `Cocorra.API/Controllers/AdminController.cs`, `AnalyticsController.cs`, `SupportController.cs`, `RolesController.cs`
- `Cocorra.DAL/AppMetaData/Router.cs` (all route strings)
- `Cocorra.DAL/DTOS/AdminDto/*`, `AnalyticsDto/*`
- `Cocorra.BLL/Base/Response.cs`, `Cocorra.BLL/Services/AdminService/AdminService.cs`

---

## 1. Admin Module Overview & RBAC

### Purpose
The Admin Dashboard is the operator console for Cocorra (a voice-based social/rooms platform). It covers four functional areas:

1. **User Management & Voice Verification** — review users, listen to their voice-verification sample, and move them through the verification lifecycle (`Pending → Active / Rejected / ReRecord / Banned`).
2. **Analytics** — platform KPIs, growth, room/participation metrics, funnels, retention, and drop-off.
3. **Report Moderation** — triage user reports and take moderation actions (lives on `SupportController`, admin-only).
4. **Role Management** — assign/revoke roles (lives on `RolesController`, admin-only).

### Roles & Permissions

Two roles gate this module. Authorization is enforced at the controller/action level via `[Authorize(Roles = ...)]` on JWT role claims.

| Capability | Endpoint(s) | `Admin` | `Coach` |
|---|---|:---:|:---:|
| View users list | `GET /Admin/Users` | ✅ | ✅ |
| View user details (+ voice) | `GET /Admin/User/{id}` | ✅ | ✅ |
| View dashboard stats | `GET /Admin/Dashboard/Stats` | ✅ | ✅ |
| **Change user status** (verify/ban/reject/re-record) | `PUT /Admin/User/ChangeStatus/{id}` | ✅ | ❌ |
| **Block device + email** (hard ban) | `POST /Admin/BlockDeviceAndEmail` | ✅ | ❌ |
| View all analytics | `GET /Analytics/*` | ✅ | ✅ |
| View / filter reports | `GET /Support/admin/reports` | ✅ | ❌ |
| Update report status | `PUT /Support/admin/reports/{id}/status` | ✅ | ❌ |
| Take action on report | `POST /Support/admin/reports/{id}/action` | ✅ | ❌ |
| Support chat (claim/reply/close/pending) | `POST/GET /Support/chat/*` | ✅ | ❌ |
| Role management (list/assign) | `/Roles/*` | ✅ | ❌ |

**RBAC rules for the Angular team:**
- **`Coach` is effectively read-only.** It can view the Users grid, user details, dashboard stats, and *all* analytics — but **every mutation is `Admin`-only**. Hide (don't just disable) all action buttons — Change Status, Block Device, Report actions, Role management, Support chat — when the current user is not `Admin`.
- Report moderation, support chat, and role management are **`Admin`-only** and must not appear in the `Coach` nav at all.
- **Self-action guards (server-enforced, mirror on client):**
  - An admin **cannot change their own status** — `ChangeStatus` returns `400` if `route id == caller's NameIdentifier claim`.
  - An admin **cannot block their own device/email** — `BlockDeviceAndEmail` returns `400` if `model.Email == caller's email claim`.
- The role claim is in the JWT. Decode it once at login, store roles in the auth/state store, and drive both the route guards and the `*ngIf`/directive-level button visibility from it.

---

## 2. Angular Routing & UI Layout Structure

### Suggested route tree (lazy-loaded `AdminModule`)

```
/admin                                  → AdminShellComponent (layout: sidebar + topbar)
  /admin/dashboard                      → DashboardOverviewComponent   (stats cards + summary charts)
  /admin/users                          → UsersListComponent           (grid: search + pagination)
  /admin/users/:id                      → UserDetailsComponent         (profile, voice player, status actions)
  /admin/analytics                      → AnalyticsComponent           (tabbed: growth/rooms/participation/reports/funnel/retention)
  /admin/reports                        → ReportsListComponent         (Admin only)
  /admin/reports/:id                    → ReportDetailComponent        (Admin only)
  /admin/support                        → SupportInboxComponent        (Admin only, SignalR live)
  /admin/roles                          → RolesComponent               (Admin only)
```

### Route guards
- `authGuard` (`CanActivate`) on `/admin` — valid JWT + `Active` verification status.
- `roleGuard` (`CanActivate` / `CanMatch`) — require role `Admin` **or** `Coach` for `/admin/**`; require role `Admin` for `/admin/reports`, `/admin/support`, `/admin/roles`. Prefer `CanMatch` so the lazy chunk isn't even downloaded for unauthorized roles.
- Use **`canDeactivate`** on `UserDetailsComponent` / report actions to warn on unsaved moderation decisions.

### Component architecture (Smart / Dumb split)

**Smart / Container components** (own state, API calls, pagination, RBAC decisions):
- `UsersListComponent` — holds `search`, `page`, `pageSize`, `totalCount`; calls the users query; owns optimistic grid updates after mutations.
- `UserDetailsComponent` — fetches one user, orchestrates the ChangeStatus / BlockDevice commands, reacts to results.
- `AnalyticsComponent` — owns the shared `from`/`to`/`granularity` filter state and fans out to the analytics endpoints.
- `ReportsListComponent`, `SupportInboxComponent` — own their grids and SignalR subscriptions.

**Dumb / Presentational components** (`@Input()`/`@Output()` only, `OnPush`):
- `DataTableComponent` — generic paginated grid (columns config, row templates, emits `pageChange`, `rowClick`). ⚠️ Build client-side sort only if needed (see §3).
- `PaginatorComponent` — pure; takes `totalCount`, `page`, `pageSize`, emits `pageChange`.
- `SearchBarComponent` — debounced (300ms) text input, emits `searchChange`.
- `StatusBadgeComponent` — maps `UserStatus` → color/label.
- `VoicePlayerComponent` — `<audio>` wrapper for the absolute `voicePath` URL.
- `ConfirmActionModalComponent` — reusable confirm modal for destructive actions (ban, block device, delete role).
- `StatCardComponent`, chart wrappers (`TimeSeriesChartComponent`, `DonutChartComponent`, `BarChartComponent`).

### State management
Use a feature store (NgRx / Signals store / component-store) per smart container. Keep the **users grid slice** (`items`, `totalCount`, `page`, `pageSize`, `search`, `loading`) local to the Users feature so mutations can patch it surgically (see §6). Analytics results are cacheable client-side for the current filter window (backend already caches 10 min).

---

## 3. Data Grids & List Queries (Server-Side Logic)

### 3.1 Users list — `GET /Api/V1/Admin/Users`
Auth: `Admin` or `Coach`.

**Query parameters:**
| Param | Type | Default | Notes |
|---|---|---|---|
| `search` | string? | `null` | Free-text search (server decides matched fields — name/email). |
| `page` | int | `1` | 1-based. |
| `pageSize` | int | `10` | No hard cap enforced in this action — pick a sane client default (10/25/50). |

⚠️ **No `sortBy`/`sortDir` and no per-column filter params exist.** If sorting is a requirement, either (a) sort only the current page client-side and label it clearly, or (b) request a backend change. Do not fabricate query params the API ignores.

**Response envelope — `PagedResponse<T>`** (`Cocorra.BLL/Base/PagedResponse.cs`). The pagination metadata is now first-class and strongly typed — `totalPages` and `hasNextPage` are computed **server-side**, so the client just reads them:

```json
{
  "totalCount": 137,
  "currentPage": 1,
  "pageSize": 10,
  "totalPages": 14,
  "hasNextPage": true,
  "hasPreviousPage": false,
  "statusCode": 200,
  "meta": null,
  "succeeded": true,
  "message": "Operation Successful",
  "errors": null,
  "data": [
    {
      "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "fullName": "Sara Ali",
      "email": "sara@example.com",
      "age": 24,
      "mbti": "INFJ",
      "status": "Pending",
      "createdAt": "2026-06-01T12:00:00Z",
      "voicePath": "https://<host>/Uploads/Voices/xxxx.aac",
      "roles": ["User"]
    }
  ]
}
```

**Frontend contract notes:**
- `data` is the row array (may be `null` on failure — guard).
- **Pagination fields are top-level and ready to use:** `totalCount`, `currentPage`, `pageSize`, `totalPages`, `hasNextPage`, `hasPreviousPage`. No client-side computation needed — bind the paginator directly to these.
- Type it in Angular as `PagedResponse<UserDto>` with those six fields plus the inherited `succeeded`/`message`/`errors`/`data`. (The legacy `meta` field still exists on the base type but is now `null` for paged endpoints — ignore it.)
- `status` is a **string** (`"Pending" | "Active" | "Rejected" | "Banned" | "ReRecord"`), consistent with the string enums now used everywhere (see §5).
- `voicePath` is already an **absolute URL** (base URL prepended server-side) or `null`.

### 3.2 User details — `GET /Api/V1/Admin/User/{id}`
Auth: `Admin` or `Coach`. `id` is a `Guid`. Returns `Response<UserDto>` (same `UserDto` as above, single object in `data`). On not-found → `400` with `succeeded:false`, `message:"User not found"`.

### 3.3 Reports grid — `GET /Api/V1/Support/admin/reports`
Auth: `Admin` only. Filter params: `category` (`ReportCategory` enum) and `status` (string). ⚠️ **This endpoint is filter-based, not paginated** — expect a full filtered list in `data`. Paginate/virtualize client-side for large sets.

### 3.4 Support chat grids — `GET /Api/V1/Support/chat/pending` & `/chat/active`
Auth: `Admin` only. Params `pageNumber` (default 1) and `pageSize` (default 10, **server-clamped to 1–50**). These *are* paginated. (Note the param names differ from the Users grid: `pageNumber`/`pageSize` vs `page`/`pageSize`.)

---

## 4. Analytical & Chart Endpoints

All under `GET /Api/V1/Analytics/*`. Auth: `Admin` or `Coach`. **Global conventions (from `AnalyticsController` doc-comments):**
- All timestamps request/response are **UTC**.
- Default date range = **last 30 days** if `from`/`to` omitted.
- Responses **cached 10 minutes**; concurrent requests share one DB query. → The client can safely re-request on tab switches; also consider a short client cache keyed by `(endpoint, from, to, granularity, limit)`.
- `limit` (where present) must be **1–100**, else `400`.
- Every response is wrapped in `Response<T>` (`data` holds the DTO below).

| Endpoint | Params | `data` shape (key fields) |
|---|---|---|
| `/Summary` | `from`, `to` | `PlatformSummaryDto`: `{ users, rooms, participation, reports, generatedAt }` (nests the four DTOs below) |
| `/Users/Growth` | `granularity`(`daily`\|`monthly`, def `monthly`), `from`, `to`, `limit` | `UserGrowthDto` (time-series, see below) |
| `/Rooms` | `from`, `to`, `limit` | `RoomAnalyticsDto`: totals, `roomsByCategory[]`, `topRooms[]` |
| `/Participation` | `from`, `to`, `limit` | `ParticipationStatsDto`: spoken-time totals, `topSpeakers[]`, `peakHours[]` |
| `/Reports` | `from`, `to`, `limit` | `ReportInsightsDto`: open/resolved counts, `reportsByCategory[]`, `mostReportedUsers[]` |
| `/Funnel` | `steps` (csv), `from`, `to` | funnel step counts |
| `/Retention` | `cohortEvent`, `activeEvent`, `from`, `to` | cohort D1/D7/D30 |
| `/Rooms/Active` | `from`, `to`, `limit` | `TopActiveRoomDto[]`: `joinEvents` vs `uniqueJoiners` |
| `/PeakHours` | `from`, `to` | `HourlyActivityDto[]` (hour 0–23) |
| `/VoiceVerification/DropOff` | `from`, `to` | `VoiceVerificationFunnelDto`: `started`, `completed`, `dropOffRate`, `completionRate` |
| `/Participation/ActiveVsPassive` | `from`, `to` | `ParticipationModeDto`: `activeSpeakers`, `passiveListeners`, `activeRate` |

**Time-series format (drives line/area charts) — `UserGrowthDto`:**
```json
{
  "granularity": "monthly",
  "from": "2026-01-01T00:00:00Z",
  "to": "2026-07-14T00:00:00Z",
  "totalUsersInPeriod": 420,
  "dataPoints": [
    { "period": "2026-06", "newUsers": 60, "activeUsers": 40,
      "pendingUsers": 10, "bannedUsers": 3, "rejectedUsers": 5, "reRecordUsers": 2 }
  ],
  "statusBreakdown": { "Active": 300, "Pending": 50, "Banned": 30 },
  "mbtiDistribution": { "INFJ": 40, "ENTP": 22 },
  "averageAge": 24.7
}
```
- `period` is the **x-axis label**: ISO date `"2026-07-01"` for `daily`, `"2026-07"` for `monthly`.
- `statusBreakdown` / `mbtiDistribution` are **dictionaries** → iterate `Object.entries()` for donut/bar charts (categorical).
- `peakHours` (participation) is a 0–23 hour histogram → bar chart, x = `hour` (UTC — label the axis "UTC" or convert to local consistently).

**Dashboard stat cards — `GET /Admin/Dashboard/Stats` → `DashboardStatsDto`:**
```json
{ "totalUsers": 137, "activeUsers": 90, "pendingUsers": 20,
  "bannedUsers": 15, "rejectedUsers": 8, "reRecordUsers": 4 }
```
Drive the top-of-dashboard KPI cards from this.

---

## 5. Commands & Mutations

✅ **Enums are now string-based across the API.** A global `JsonStringEnumConverter` is registered in `Program.cs`, so **send enum values as their string names** in request bodies (and expect strings in responses). Valid `UserStatus` values: `"Pending" | "Active" | "Rejected" | "Banned" | "ReRecord"`. (The server still *accepts* the legacy integer form on input for backward compatibility, but new code should send strings.)

### 5.1 Change user status — `PUT /Api/V1/Admin/User/ChangeStatus/{id}`
Auth: **`Admin` only**. Route `id` = `Guid`.

**Payload (`ChangeStatusDto`):**
```json
{ "newStatus": "Banned" }
```

**Behavior / side effects (from `AdminService.ChangeUserStatusAsync`):**
- `Active` → clears lockout, deletes voice sample; emits `ActivationCompleted` (first time only); sends welcome email.
- `Banned` → permanent lockout, invalidates refresh token, deletes voice, push notification; **and** the controller force-disconnects the user's live SignalR sessions (see §6).
- `Rejected` → invalidates refresh token, deletes voice, push notification.
- `ReRecord` → deletes voice, push notification to re-record.

**Errors (all `400`, `succeeded:false`, human-readable `message`):**
- Changing your own status → `"You cannot change your own status."`
- User not found → `"User not found"`.
- Same status → `"User is already {Status}"`.
- Invalid enum → `"Invalid status value."`

### 5.2 Bulk change user status — `PUT /Api/V1/Admin/Users/BulkChangeStatus`
Auth: **`Admin` only**. Applies one status to many users in a single call.

**Payload (`BulkChangeStatusDto`):**
```json
{
  "userIds": [
    "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "9c1b2d34-1111-2222-3333-444455556666"
  ],
  "newStatus": "Banned"
}
```
- `userIds` — non-empty list of user GUIDs. Duplicates are de-duplicated server-side. **Max 200 ids per request** (exceeding it returns `400`).
- `newStatus` — string enum (same values as §5.1).
- Each user is processed through the same pipeline as the single endpoint, so **all side effects apply per user** (lockout, refresh-token invalidation, voice cleanup, email, push, and SignalR `ForceDisconnect` for `Banned`/`Rejected`).

**⚠️ Partial success — this is the key handling difference.** The operation can succeed for some users and fail for others. On a valid request the endpoint returns **`200`** with `succeeded: true` **even if some individual users failed** — you must inspect the per-item `results` array. `data` is a `BulkChangeStatusResultDto`:

```json
{
  "statusCode": 200,
  "succeeded": true,
  "message": "1 succeeded, 1 failed.",
  "errors": null,
  "data": {
    "totalRequested": 2,
    "succeededCount": 1,
    "failedCount": 1,
    "results": [
      { "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa6", "succeeded": true,  "message": "User status changed from Pending to Banned" },
      { "userId": "9c1b2d34-1111-2222-3333-444455556666", "succeeded": false, "message": "User is already Banned" }
    ]
  }
}
```

**Angular handling directives:**
- **Whole-request failures** (empty `userIds`, invalid enum, >200 ids) return `400` with `succeeded: false` — surface `message` and abort. `ModelState` validation errors (e.g. missing `userIds`) come back in the ASP.NET `ModelState` shape — handle via the shared interceptor (see below).
- **Per-user failures** live in `data.results[].succeeded` on a `200`. Do **not** treat a `200` as "all done". Drive UI from the per-item results:
  - Patch each **succeeded** row's `status` in the local grid store (see §6); leave failed rows unchanged.
  - Show a summary toast from `succeededCount`/`failedCount` (e.g. "12 updated, 2 failed"). If `failedCount > 0`, offer a detail view listing the failed `userId`s and their `message` (common causes: "User is already X", "User not found", "You cannot change your own status.").
  - An admin's own id is silently rejected as a per-item failure with `"You cannot change your own status."` — filter your own id from the selection client-side before sending to avoid the noise.
- Update `DashboardStatsDto` counters by the number of **succeeded** transitions, not the requested count.

### 5.3 Block device + email (hard ban) — `POST /Api/V1/Admin/BlockDeviceAndEmail`
Auth: **`Admin` only**.

**Payload (`BlockDeviceAndEmailDto`):**
```json
{
  "email": "user@example.com",   // required, validated as email
  "deviceId": "abc-123",         // required
  "deviceName": "Unknown",
  "deviceModel": "Unknown",
  "deviceType": "Unknown",
  "deviceOs": "Unknown"
}
```
Sets user → Banned, locks out, invalidates refresh token, and blocks the device. **Errors:** self-block → `400`; user not found → `404` (`NotFound<string>`).

### 5.4 Report moderation (Admin only)
- **Update status** — `PUT /Api/V1/Support/admin/reports/{id}/status`, body `UpdateReportStatusDto { status }`.
- **Take action** — `POST /Api/V1/Support/admin/reports/{id}/action`, body `TakeReportActionDto` (uses `AdminReportAction` enum — send as its **string** name).
- Both use `ModelState` validation → invalid body returns `400` with the ASP.NET `ModelState` dictionary (different shape from `Response<T>` — handle both error shapes in the HTTP interceptor).

### 5.5 Role management (Admin only)
- `POST /Api/V1/Roles/ManageUser` — `ManageUserRolesDto` to assign/revoke a user's roles.
- `GET /Api/V1/Roles/List`, `/Roles/Users/{roleName}` for pickers.

### Success / error handling & bulk operations
- **Bulk status changes are now a single endpoint** — `PUT /Api/V1/Admin/Users/BulkChangeStatus` (§5.2). A "select-all → bulk ban" UI is now one call, but remember it returns **partial success**: a `200` can still contain per-user failures in `data.results[]`. There is still **no** bulk *delete* endpoint.
- **Uniform envelope:** most endpoints return `Response<T>` (or `PagedResponse<T>` for lists) — check `succeeded` (and/or `statusCode`) before reading `data`; surface `message` / `errors[]` to the user. `SupportController` returns `StatusCode((int)result.StatusCode, result)`, so also branch on HTTP status.
- Build one **HTTP interceptor** that normalizes both error shapes (`Response<T>.message/errors` and `ModelState`) into a single toast/error model.

---

## 6. Business Logic & State Updates (post-mutation directives)

**General principle: never full-reload the grid after a mutation.** Patch the local store slice and keep pagination counts consistent.

- **After Change Status (from Users grid or details):**
  - On `succeeded`, **patch the single row** in the users store (`status = newStatus`) rather than refetching the page. Update the affected `DashboardStatsDto` counters optimistically (decrement old bucket, increment new bucket) or re-fetch `Dashboard/Stats` in the background.
  - If the current grid is filtered by status and the row no longer matches, **remove it from the local list and decrement `meta.totalCount`** (do not change `page`).
  - From the details page, on success either route back to `/admin/users` with the patched row or reflect the new `StatusBadge` in place.

- **After Block Device / hard ban:** same as a Banned status change — patch row to `Banned`, adjust stats.

- **⚠️ Real-time consequence of ban/reject (SignalR):** when a user is Banned or Rejected, the backend calls `RoomHub` → emits **`ForceDisconnect` `{ reason }`** to that user's live connections and purges them. This affects the *target user's* app, not the admin dashboard — but the admin's own SignalR client should also handle `ForceDisconnect` in case an admin's own role/status is ever changed. The admin UI can optimistically show "user disconnected from active rooms."

- **Support inbox is live (Admin only), driven by `SupportHub` group `"Admins"`:**
  - `NewPendingChatAlert` → increment pending badge / refetch pending list head.
  - `ReceiveSupportMessage` (payload = message) → append to the open thread.
  - `ChatClaimed` (payload = `chatId`) → **remove that chat from the pending list** in every other admin's UI.
  - After `claim`/`reply`/`close` REST calls succeed, patch the local chat state; rely on hub events to sync other admins rather than polling.

- **Analytics:** results are backend-cached 10 min. After a mutation that would change a metric (e.g., a ban), **do not** eagerly refetch analytics — show a "data may be up to 10 min delayed" note, or provide a manual refresh. Keep the `from`/`to` filter in the URL query so views are shareable/bookmarkable.

- **Loading & concurrency:** disable the specific action button (not the whole grid) while a per-row mutation is in flight; use `trackBy` on the grid so patched rows don't remount.

---

## Appendix — Quick endpoint reference

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/Api/V1/Admin/Users?search=&page=&pageSize=` | Admin, Coach | Users grid (`PagedResponse<UserDto>`) |
| GET | `/Api/V1/Admin/User/{id}` | Admin, Coach | User details |
| PUT | `/Api/V1/Admin/User/ChangeStatus/{id}` | Admin | Change status (body `{newStatus:"Banned"}`) |
| PUT | `/Api/V1/Admin/Users/BulkChangeStatus` | Admin | Bulk change status (partial success) |
| POST | `/Api/V1/Admin/BlockDeviceAndEmail` | Admin | Hard ban device+email |
| GET | `/Api/V1/Admin/Dashboard/Stats` | Admin, Coach | KPI cards |
| GET | `/Api/V1/Analytics/*` | Admin, Coach | Analytics (see §4) |
| GET | `/Api/V1/Support/admin/reports?category=&status=` | Admin | Reports grid |
| PUT | `/Api/V1/Support/admin/reports/{id}/status` | Admin | Update report status |
| POST | `/Api/V1/Support/admin/reports/{id}/action` | Admin | Take report action |
| GET/POST | `/Api/V1/Support/chat/*` | Admin | Support inbox (live) |
| GET/POST | `/Api/V1/Roles/*` | Admin | Role management |

> Note: `Router.cs` declares `Update`, `Delete`, and `ResetPassword` admin routes, but **no controller actions currently implement them** — treat as not-yet-available and confirm with backend before wiring UI.
