# 🔥 FCM Push Notification — Complete Backend Rebuild Guide

**For**: ASP.NET Core Backend Developer
**Date**: 2026-08-24
**Context**: This guide rebuilds the entire FCM push notification system from scratch. All C# code is production-ready and designed to match the Flutter app's expected payloads exactly.

---

## Table of Contents

1. [Phase 1: Setup & Firebase Admin SDK](#phase-1-setup--firebase-admin-sdk)
2. [Phase 2: Initialization (Program.cs)](#phase-2-initialization-programcs)
3. [Phase 3: Token Management](#phase-3-token-management)
4. [Phase 4: The Dispatcher Service](#phase-4-the-dispatcher-service-pushnotificationservicecs)
5. [Phase 5: Dependency Injection Registration](#phase-5-dependency-injection-registration)
6. [Phase 6: Usage Examples](#phase-6-usage-examples)
7. [Appendix: Flutter FCM Payload Contract](#appendix-flutter-fcm-payload-contract)

---

## Phase 1: Setup & Firebase Admin SDK

### 1.1 Install NuGet Package

```bash
dotnet add Cocorra.BLL package FirebaseAdmin
```

> [!IMPORTANT]
> Install `FirebaseAdmin` in the **BLL** project (not the API project), since `PushNotificationService` lives there.

### 1.2 Firebase Service Account JSON

1. Go to [Firebase Console](https://console.firebase.google.com/) → **Project Settings** → **Service accounts** → **Generate new private key**
2. Save the downloaded file as **`firebase-config.json`**
3. Place it in the **API project root** (same folder as `Program.cs`):

```
Cocorra.API/
├── Program.cs
├── firebase-config.json   ← HERE
├── appsettings.json
└── ...
```

4. In Visual Studio, right-click `firebase-config.json` → **Properties**:
   - **Build Action**: `None`
   - **Copy to Output Directory**: `Copy if newer`

Or add this to `Cocorra.API.csproj`:

```xml
<ItemGroup>
  <None Update="firebase-config.json">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

> [!CAUTION]
> **NEVER** commit `firebase-config.json` to Git. Add it to `.gitignore`. On the production server, place it manually in the deployment directory alongside the compiled DLL.

---

## Phase 2: Initialization (Program.cs)

Add this **before** `builder.Build()`:

```csharp
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;

// ── Initialize Firebase Admin SDK ───────────────────────────────────────────
var firebaseConfigPath = Path.Combine(
    builder.Environment.ContentRootPath, "firebase-config.json");

if (File.Exists(firebaseConfigPath))
{
    FirebaseApp.Create(new AppOptions
    {
        Credential = GoogleCredential.FromFile(firebaseConfigPath)
    });
    Console.WriteLine("✅ Firebase Admin SDK initialized successfully.");
}
else
{
    // CRITICAL: If this fires on production, NO push notifications will work.
    Console.WriteLine(
        "❌ WARNING: firebase-config.json NOT FOUND at: " + firebaseConfigPath +
        " — Firebase Admin SDK initialization SKIPPED. Push notifications DISABLED.");
}
```

> [!WARNING]
> If `firebase-config.json` is missing, `FirebaseMessaging.DefaultInstance` will be `null`. The `PushNotificationService` (Phase 4) guards against this, but **no pushes will ever send**.

---

## Phase 3: Token Management

### 3.1 Database Column

Ensure the `ApplicationUser` (Identity) model has an `FcmToken` column:

```csharp
public class ApplicationUser : IdentityUser<Guid>
{
    // ... existing properties ...

    /// <summary>
    /// Firebase Cloud Messaging device token. Updated on each login
    /// and on token refresh from the Flutter app.
    /// </summary>
    public string? FcmToken { get; set; }
}
```

If the column doesn't exist yet, create a migration:

```bash
dotnet ef migrations add AddFcmTokenToUser
dotnet ef database update
```

### 3.2 Controller Endpoint

The Flutter app calls `PUT /Api/V1/Authentication/UpdateFcmToken?fcmToken=<token>` after every login and on token refresh.

```csharp
/// <summary>
/// Receives and persists the device's FCM token.
/// Called by the Flutter app on login and on token refresh.
/// </summary>
[HttpPut("UpdateFcmToken")]
[Authorize]
public async Task<IActionResult> UpdateFcmToken([FromQuery] string fcmToken)
{
    var userId = GetAuthenticatedUserId(); // Your existing helper

    if (string.IsNullOrWhiteSpace(fcmToken))
        return BadRequest("FCM token is required.");

    var user = await _userManager.FindByIdAsync(userId.ToString());
    if (user == null) return BadRequest("User not found.");

    user.FcmToken = fcmToken;
    await _userManager.UpdateAsync(user);

    return Ok(new { succeeded = true, message = "FCM Token updated successfully." });
}
```

> [!NOTE]
> The Flutter app sends the token as **both** a query parameter (`?fcmToken=xxx`) and a JSON body. Binding from `[FromQuery]` is the simplest and most reliable approach.

---

## Phase 4: The Dispatcher Service (PushNotificationService.cs)

### 4.1 Interface

```csharp
// File: Cocorra.BLL/Services/NotificationService/IPushNotificationService.cs

using System.Collections.Generic;
using System.Threading.Tasks;

namespace Cocorra.BLL.Services.NotificationService
{
    public interface IPushNotificationService
    {
        Task SendPushNotificationAsync(
            string fcmToken,
            string title,
            string body,
            Dictionary<string, string> data);
    }
}
```

### 4.2 Implementation

```csharp
// File: Cocorra.BLL/Services/NotificationService/PushNotificationService.cs

using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Logging;

namespace Cocorra.BLL.Services.NotificationService
{
    public class PushNotificationService : IPushNotificationService
    {
        private readonly ILogger<PushNotificationService> _logger;

        public PushNotificationService(ILogger<PushNotificationService> logger)
        {
            _logger = logger;
        }

        public async Task SendPushNotificationAsync(
            string fcmToken,
            string title,
            string body,
            Dictionary<string, string> data)
        {
            var type = data?.GetValueOrDefault("type", "unknown") ?? "unknown";

            // ── Guard 1: Empty token ────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(fcmToken))
            {
                _logger.LogWarning(
                    "FCM push skipped: token is null or empty. Type: {Type}", type);
                return;
            }

            // ── Guard 2: Firebase not initialized ───────────────────────────
            // FirebaseMessaging.DefaultInstance is null when FirebaseApp.Create()
            // was never called (firebase-config.json missing in Program.cs).
            if (FirebaseMessaging.DefaultInstance == null)
            {
                _logger.LogError(
                    "FCM push FAILED: FirebaseMessaging.DefaultInstance is null. " +
                    "Ensure firebase-config.json exists and FirebaseApp.Create() " +
                    "succeeded at startup. Type: {Type}", type);
                return;
            }

            // ── Build Message ───────────────────────────────────────────────
            // An alert payload (title/body present) and a data-only payload
            // require different APNS headers. Decide once.
            var hasAlert = !string.IsNullOrWhiteSpace(title)
                        || !string.IsNullOrWhiteSpace(body);

            var message = new Message()
            {
                Token = fcmToken,
                Data  = data,

                // HIGH priority wakes the device from Doze mode (Android)
                Android = new AndroidConfig()
                {
                    Priority = Priority.High
                },

                // iOS requires apns-push-type from iOS 13+.
                // Apple rejects background pushes at priority 10 (BadPriority).
                Apns = new ApnsConfig()
                {
                    Headers = new Dictionary<string, string>
                    {
                        { "apns-push-type", hasAlert ? "alert" : "background" },
                        { "apns-priority",  hasAlert ? "10"    : "5" }
                    },
                    Aps = new Aps()
                    {
                        // ContentAvailable = true wakes the app on iOS for
                        // data-only pushes. Do NOT set it on alert pushes.
                        ContentAvailable = !hasAlert
                    }
                }
            };

            // CRITICAL: Only attach Notification when title/body are non-empty.
            // Firebase treats ANY Notification object (even with empty strings)
            // as a "display" notification, which causes:
            //  - Blank pop-ups on Android 13+
            //  - Prevents silent background handling on iOS
            if (hasAlert)
            {
                message.Notification = new Notification()
                {
                    Title = title,
                    Body  = body
                };
            }

            // ── Send ────────────────────────────────────────────────────────
            try
            {
                var messageId = await FirebaseMessaging.DefaultInstance
                    .SendAsync(message);

                _logger.LogInformation(
                    "FCM push sent successfully. " +
                    "MessageId: {MessageId}, Type: {Type}",
                    messageId, type);
            }
            catch (FirebaseMessagingException ex)
            {
                // Log the FCM error code (e.g. UNREGISTERED, INVALID_ARGUMENT)
                // so token, quota, and payload problems are diagnosable.
                // Only the last 8 chars of the token are logged for privacy.
                _logger.LogError(ex,
                    "FCM push FAILED. " +
                    "MessagingErrorCode: {ErrorCode}, " +
                    "FcmToken (last 8): ...{TokenSuffix}, " +
                    "Type: {Type}",
                    ex.MessagingErrorCode,
                    fcmToken.Length > 8 ? fcmToken[^8..] : fcmToken,
                    type);
            }
            catch (Exception ex)
            {
                // Catch-all so callers can await this method safely.
                _logger.LogError(ex,
                    "FCM push FAILED with unexpected exception. Type: {Type}",
                    type);
            }
        }
    }
}
```

### Why This Design Matters

| Feature | Purpose |
|---|---|
| `AndroidConfig.Priority = High` | Wakes Android from Doze/App Standby. Without this, pushes are batched and delayed indefinitely. |
| `ApnsConfig.Headers["apns-push-type"]` | Required by Apple from iOS 13+. Mismatched type causes silent rejection. |
| `ApnsConfig.Aps.ContentAvailable` | Wakes the Flutter app on iOS for data-only pushes (e.g., `account_locked`). |
| Conditional `message.Notification` | Firebase treats **any** `Notification` object as a display push. For data-only payloads (ban/lock), we must **NOT** set it. |
| `ILogger` with `FirebaseMessagingException` | The **#1 root cause** of the original bug. Empty `catch {}` blocks silently swallowed Firebase errors. Now every failure is logged with error code and token suffix. |

---

## Phase 5: Dependency Injection Registration

In `Program.cs`, register the service:

```csharp
// ── Push Notification Service ────────────────────────────────────────────
builder.Services.AddScoped<IPushNotificationService, PushNotificationService>();
```

Then inject it into any service that needs to send pushes:

```csharp
public class AdminService : ResponseHandler, IAdminService
{
    private readonly IPushNotificationService _pushService;
    private readonly INotificationRepository  _notificationRepo;
    // ... other dependencies ...

    public AdminService(
        IPushNotificationService pushService,
        INotificationRepository notificationRepo,
        // ... other params ...
    )
    {
        _pushService      = pushService;
        _notificationRepo = notificationRepo;
        // ...
    }
}
```

---

## Phase 6: Usage Examples

### 6.1 WarnUser (SupportService — TakeActionOnReportAsync)

```csharp
case AdminReportAction.WarnUser:
    if (report.ReportedUserId == null)
        return BadRequest<string>("This report has no reported user to warn.");

    // 1. Create and persist the Notification to DB FIRST
    var warning = new Notification
    {
        UserId    = report.ReportedUserId.Value,
        Title     = "Admin Warning",
        Message   = dto.AdminNote ?? "You have received a warning for violating community guidelines.",
        Type      = NotificationType.AdminWarning,
        ReferenceId = report.Id,
        CreatedAt = DateTime.UtcNow,
        IsRead    = false
    };
    await _notificationRepo.AddAsync(warning);

    // 2. Fetch the user to get their FCM token
    var warnUser = await _userManager.FindByIdAsync(
        report.ReportedUserId.Value.ToString());

    // 3. Guard: only push if token exists
    if (!string.IsNullOrEmpty(warnUser?.FcmToken))
    {
        // 4. Build data dict matching Flutter's expected contract
        var data = new Dictionary<string, string>
        {
            { "type", "report" },
            { "reportId", report.Id.ToString() }
        };

        // 5. AWAIT the push (never fire-and-forget)
        await _pushService.SendPushNotificationAsync(
            warnUser.FcmToken,
            warning.Title,
            warning.Message,
            data);
    }

    report.Status = "Resolved";
    break;
```

### 6.2 BanUser (AdminService — ChangeUserStatusAsync)

```csharp
case UserStatus.Banned:
    await _userManager.SetLockoutEnabledAsync(user, true);
    var banLockoutEnd = DateTimeOffset.MaxValue;
    await _userManager.SetLockoutEndDateAsync(user, banLockoutEnd);

    // Invalidate refresh token
    user.RefreshToken = null;

    // Persist notification
    var banNotification = new Notification
    {
        UserId  = user.Id,
        Title   = "Account Suspended",
        Message = "Your account has been permanently suspended.",
        Type    = NotificationType.AdminWarning,
        IsRead  = false
    };
    await _notificationRepo.AddAsync(banNotification);

    // Push — data-only (empty title/body) so Flutter handles it silently
    if (!string.IsNullOrEmpty(user.FcmToken))
    {
        var data = new Dictionary<string, string>
        {
            { "type", "account_locked" },
            { "lockout_end", banLockoutEnd.ToString("o") }
        };

        // Empty title/body = data-only push.
        // PushNotificationService will NOT attach a Notification object,
        // which triggers Flutter's silent background handler.
        await _pushService.SendPushNotificationAsync(
            user.FcmToken, "", "", data);
    }
    break;
```

### 6.3 Mute24h (SupportService)

```csharp
case AdminReportAction.Mute24h:
    var muteUser = await _userManager.FindByIdAsync(
        report.ReportedUserId.Value.ToString());
    if (muteUser == null) return NotFound<string>("User not found.");

    await _userManager.SetLockoutEnabledAsync(muteUser, true);
    await _userManager.SetLockoutEndDateAsync(muteUser,
        DateTimeOffset.UtcNow.AddHours(24));

    // Persist notification
    var muteNotification = new Notification
    {
        UserId  = report.ReportedUserId.Value,
        Title   = "Account Locked",
        Message = "Your account has been temporarily suspended for 24 hours.",
        Type    = NotificationType.AdminWarning,
        ReferenceId = report.Id,
        CreatedAt   = DateTime.UtcNow,
        IsRead  = false
    };
    await _notificationRepo.AddAsync(muteNotification);

    // Push WITH title/body (shows a notification banner on the device)
    if (!string.IsNullOrEmpty(muteUser.FcmToken))
    {
        var data = new Dictionary<string, string>
        {
            { "type", "account_locked" }
            // NOTE: SupportService does NOT include lockout_end.
            // The Flutter app will probe the backend to get it.
        };
        await _pushService.SendPushNotificationAsync(
            muteUser.FcmToken,
            muteNotification.Title,
            muteNotification.Message,
            data);
    }

    report.Status = "Resolved";
    break;
```

### 6.4 Account Activated (AdminService)

```csharp
case UserStatus.Active:
    // ... clear lockout, delete voice, etc. ...

    var activeNotification = new Notification
    {
        UserId  = user.Id,
        Title   = "Account Verified ✅",
        Message = "Your voice verification has been approved. " +
                  "You now have full access to Cocorra.",
        Type    = NotificationType.System,
        IsRead  = false
    };
    await _notificationRepo.AddAsync(activeNotification);

    if (!string.IsNullOrEmpty(user.FcmToken))
    {
        await _pushService.SendPushNotificationAsync(
            user.FcmToken,
            "Account Verified ✅",
            "Your account is now fully active.",
            new Dictionary<string, string> { { "type", "account_activated" } });
    }
    break;
```

### 6.5 ReRecord Voice (AdminService)

```csharp
case UserStatus.ReRecord:
    // ... delete old voice ...

    var reRecordNotification = new Notification
    {
        UserId  = user.Id,
        Title   = "إعادة تسجيل صوتي 🎙️",
        Message = "نعتذر منك، نحتاج منك إعادة تسجيل المقطع الصوتي الخاص بك بوضوح أكبر.",
        Type    = NotificationType.System,
        IsRead  = false
    };
    await _notificationRepo.AddAsync(reRecordNotification);

    if (!string.IsNullOrEmpty(user.FcmToken))
    {
        await _pushService.SendPushNotificationAsync(
            user.FcmToken,
            reRecordNotification.Title,
            reRecordNotification.Message,
            new Dictionary<string, string> { { "type", "reRecord" } });
    }
    break;
```

---

## Appendix: Flutter FCM Payload Contract

The Flutter app reads `data["type"]` (lowercase) to decide how to handle each push. Here is the **exact contract** it expects:

| `type` Value | Extra `data` Keys | Display? | Flutter Behavior |
|---|---|---|---|
| `"chat"` | `senderId` (required) | Yes (title = sender name, body = message) | Shows SnackBar. Tap → opens private chat with sender. |
| `"room"` | `roomId` | Yes | Shows SnackBar. Tap → opens Voice Room screen. |
| `"report"` | `reportId` | Yes (title = "Admin Warning", body = reason) | Shows RED warning SnackBar. Tap → Notifications screen. |
| `"reRecord"` | `email` (optional) | Yes (title = Arabic re-record msg) | Shows ORANGE SnackBar with "سجّل الآن" button. Tap → VoiceRecordScreen. |
| `"general"` | (none) | Yes | Shows green SnackBar. Tap → Notifications screen. |
| `"account_locked"` | `lockout_end` (ISO8601, optional) | **No** (data-only, empty title/body) | **Silent**. Immediately sets `pending_ban` flag and navigates to BannedScreen. |
| `"account_rejected"` | (none) | **No** (data-only) | **Silent**. Same behavior as `account_locked`. |
| `"account_activated"` | (none) | Yes | Shows "Account Verified ✅" SnackBar. |

> [!IMPORTANT]
> **Key rule for `account_locked`**: Send it with **empty title and body** (`"", ""`). If you attach a title/body, Firebase wraps it in a `Notification` object, which triggers a visible pop-up instead of the silent background handler. The Flutter app handles the redirect internally.

> [!IMPORTANT]
> The `type` key must be **lowercase** (e.g., `"type"` not `"Type"`). The Flutter app normalizes using `.toLowerCase()`, but the key name itself must be lowercase `"type"` in the `data` dictionary.

---

## Quick Checklist Before Deploying

- [ ] `firebase-config.json` is in the server's content root directory
- [ ] `FirebaseApp.Create()` runs at startup (check logs for "✅ Firebase Admin SDK initialized")
- [ ] `IPushNotificationService` is registered in DI (`AddScoped`)
- [ ] `FcmToken` column exists in `AspNetUsers` table
- [ ] `UpdateFcmToken` endpoint is accessible at `PUT /Api/V1/Authentication/UpdateFcmToken`
- [ ] All `SendPushNotificationAsync` calls are **awaited** (never fire-and-forget)
- [ ] All push calls have a `!string.IsNullOrEmpty(user.FcmToken)` guard
- [ ] All push calls have a DB `Notification` persisted **before** the push
- [ ] `account_locked` pushes use empty title/body (data-only)
- [ ] Server logs are monitored for `"FCM push FAILED"` messages after deployment

---

*— Generated from Flutter codebase audit by the Mobile Team*
