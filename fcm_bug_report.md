# 🐛 Bug Report: Silent FCM Push Notification Failures on Admin & Support Actions

**Reporter**: Mobile Team
**Priority**: 🔴 Critical
**Affected Services**: `PushNotificationService`, `AdminService`, `SupportService`
**Affected Endpoint(s)**: `PUT Api/V1/Admin/User/ChangeStatus/{id}`, `POST Api/V1/Support/admin/reports/{id}/action`

---

## Summary

Push notifications for admin and support actions (Ban, Reject, ReRecord, Mute24h, WarnUser, etc.) are **silently failing** and producing **zero server-side logs**. Mobile users never receive these critical notifications. After investigation, we've identified three distinct but interrelated root causes in the backend notification pipeline.

---

## Root Cause Analysis

### Root Cause 1 — Silent Exception Swallowing in `PushNotificationService`

**File**: [`PushNotificationService.cs`](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/NotificationService/PushNotificationService.cs) — Lines 34-42

```csharp
// CURRENT (broken)
try
{
    await FirebaseMessaging.DefaultInstance.SendAsync(message);
}
catch (FirebaseMessagingException)
{
    // Optionally log inactive token or mapping issues.
    // Swallow so caller doesn't crash on invalid/expired tokens.
}
```

**Problem**: The `catch` block swallows `FirebaseMessagingException` with no logging. Additionally, if the Firebase Admin SDK was never initialized (e.g., `firebase-config.json` is missing — see [`Program.cs:50-61`](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.API/Program.cs#L50-L61)), then `FirebaseMessaging.DefaultInstance` is `null`, which throws a `NullReferenceException` — a type that is **not caught** by this handler. That exception then propagates to every call site, where it is caught and discarded by the outer `catch { }` wrappers.

**Result**: If Firebase fails for any reason (missing config, expired token, invalid payload, network error), we have **zero visibility**. No logs, no alerts, no way to diagnose.

---

### Root Cause 2 — Missing `AndroidConfig` and `ApnsConfig` on the FCM Message Payload

**File**: [`PushNotificationService.cs`](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/NotificationService/PushNotificationService.cs) — Lines 15-32

```csharp
// CURRENT (missing platform config)
var message = new Message()
{
    Token = fcmToken,
    Data = data
};
```

**Problem**: The `Message` object has no `Android` or `Apns` configuration.

| Platform | Impact |
|---|---|
| **Android** | Without `AndroidConfig.Priority = Priority.High`, FCM messages can be batched, delayed, or dropped entirely when the device is in Doze mode. This is the primary reason notifications fail to appear on Android when the app is in the background. |
| **iOS** | Without `ApnsConfig` setting `content-available = 1` and `priority = "10"`, data-only messages (like `account_locked` with empty title/body) will **not** wake the app from background/terminated state. The push is delivered to the device OS but never forwarded to the app. |

---

### Root Cause 3 — Admin/Support Actions Send FCM but Don't Persist a `Notification` Entity

This is a **data consistency** problem. Some actions save to the DB, some don't.

#### In `AdminService.ChangeUserStatusAsync` — [`AdminService.cs`](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/AdminService/AdminService.cs)

| Status Change | Saves `Notification` to DB? | Dispatches FCM Push? | Line Reference |
|---|---|---|---|
| → `Banned` | ❌ No | ✅ Yes (data-only, empty title/body) | [L103-111](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/AdminService/AdminService.cs#L103-L111) |
| → `Rejected` | ❌ No | ✅ Yes (data-only, empty title/body) | [L121-128](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/AdminService/AdminService.cs#L121-L128) |
| → `ReRecord` | ❌ No | ✅ Yes (with title/body) | [L161-164](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/AdminService/AdminService.cs#L161-L164) |
| → `Active` | ❌ No | ❌ No | [L84-91](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/AdminService/AdminService.cs#L84-L91) |

#### In `SupportService.TakeActionOnReportAsync` — [`SupportService.cs`](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/SupportService/SupportService.cs)

| Action | Saves `Notification` to DB? | Dispatches FCM Push? | Line Reference |
|---|---|---|---|
| `WarnUser` | ✅ Yes | ✅ Yes | [L158-175](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/SupportService/SupportService.cs#L158-L175) |
| `Mute24h` | ❌ No | ✅ Yes | [L195-199](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/SupportService/SupportService.cs#L195-L199) |
| `BanUser` | ❌ No | ✅ Yes | [L219-223](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/SupportService/SupportService.cs#L219-L223) |

**Impact**: If the FCM push doesn't reach the device (Root Causes 1 & 2), the notification is **permanently lost**. The user can never retrieve it via `GET api/Notifications/my-notifications` because it was never written to the `Notifications` table.

> [!IMPORTANT]
> The `WarnUser` action is the **only** admin/support action that follows the correct pattern: save to DB **and** dispatch FCM. All other actions should follow this same pattern.

---

## Actionable Code Fixes

### Fix 1 — Add `ILogger` and Proper Error Logging to `PushNotificationService`

> **File to modify**: [`Cocorra.BLL/Services/NotificationService/PushNotificationService.cs`](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/NotificationService/PushNotificationService.cs)

Replace the entire file content with:

```csharp
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
            string fcmToken, string title, string body, Dictionary<string, string> data)
        {
            if (string.IsNullOrWhiteSpace(fcmToken))
            {
                _logger.LogWarning("FCM push skipped: token is null or empty. Data type: {Type}",
                    data?.GetValueOrDefault("type", "unknown"));
                return;
            }

            // Guard: if Firebase SDK was never initialized, fail loudly once
            // rather than throwing NullReferenceException on every call.
            if (FirebaseMessaging.DefaultInstance == null)
            {
                _logger.LogError(
                    "FCM push FAILED: FirebaseMessaging.DefaultInstance is null. " +
                    "Ensure firebase-config.json exists and FirebaseApp.Create() succeeded at startup.");
                return;
            }

            var message = new Message()
            {
                Token = fcmToken,
                Data = data,

                // ── Android: High priority ensures immediate delivery even in Doze mode ──
                Android = new AndroidConfig()
                {
                    Priority = Priority.High
                },

                // ── iOS/APNs: wake the app for data-only pushes; use max priority ──
                Apns = new ApnsConfig()
                {
                    Headers = new Dictionary<string, string>
                    {
                        { "apns-priority", "10" }
                    },
                    Aps = new Aps()
                    {
                        ContentAvailable = true
                    }
                }
            };

            // CRITICAL: Only attach Notification when title/body are non-empty.
            // Firebase treats ANY Notification object (even with empty strings) as a
            // "display" notification, which can cause blank pop-ups on Android 13+
            // and prevents silent background handling on iOS.
            if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(body))
            {
                message.Notification = new Notification()
                {
                    Title = title,
                    Body = body
                };
            }

            try
            {
                var messageId = await FirebaseMessaging.DefaultInstance.SendAsync(message);
                _logger.LogInformation(
                    "FCM push sent successfully. MessageId: {MessageId}, Type: {Type}",
                    messageId, data?.GetValueOrDefault("type", "unknown"));
            }
            catch (FirebaseMessagingException ex)
            {
                // Log the specific FCM error code so we can diagnose token issues,
                // quota limits, or payload problems from server logs.
                _logger.LogError(ex,
                    "FCM push FAILED. MessagingErrorCode: {ErrorCode}, FcmToken (last 8): ...{TokenSuffix}, Type: {Type}",
                    ex.MessagingErrorCode,
                    fcmToken.Length > 8 ? fcmToken[^8..] : fcmToken,
                    data?.GetValueOrDefault("type", "unknown"));
            }
            catch (Exception ex)
            {
                // Catch-all for unexpected errors (network, serialization, etc.)
                _logger.LogError(ex,
                    "FCM push FAILED with unexpected exception. Type: {Type}",
                    data?.GetValueOrDefault("type", "unknown"));
            }
        }
    }
}
```

**Key changes**:
- Injects `ILogger<PushNotificationService>` via constructor (auto-resolved by DI — no registration needed)
- Null-check on `FirebaseMessaging.DefaultInstance` with explicit error log
- `AndroidConfig` with `Priority.High` for reliable Android delivery
- `ApnsConfig` with `apns-priority: 10` and `ContentAvailable = true` for iOS background wake
- Catches both `FirebaseMessagingException` (with `MessagingErrorCode`) and generic `Exception`
- Logs token suffix (last 8 chars) for debugging without exposing full tokens in logs

---

### Fix 2 — Remove Silent `catch { }` at Every Call Site

After Fix 1, the `PushNotificationService` itself handles all error logging and never throws. The outer `try/catch` wrappers at each call site are now **redundant** and should be simplified. You can safely remove the `try/catch` wrapping across all call sites and just `await` directly:

**Example — before** ([`AdminService.cs:110`](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/AdminService/AdminService.cs#L110)):
```csharp
try { await _pushService.SendPushNotificationAsync(user.FcmToken, "", "", banData); } catch { }
```

**After**:
```csharp
await _pushService.SendPushNotificationAsync(user.FcmToken, "", "", banData);
```

> [!TIP]
> This applies to **all 11 call sites** in `AdminService`, `FriendService`, `RoomService`, `SupportService`, and `ChatService`. The `PushNotificationService` now guarantees it will never throw — errors are logged internally and execution continues safely.

---

### Fix 3 — Persist `Notification` Entity for Admin Status Changes

> **File to modify**: [`Cocorra.BLL/Services/AdminService/AdminService.cs`](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/AdminService/AdminService.cs)

**Prerequisite**: `AdminService` needs `INotificationRepository` injected. Add it to the constructor:

```csharp
// Add to the field declarations:
private readonly INotificationRepository _notificationRepo;

// Add to the constructor parameters and body:
public AdminService(
    UserManager<ApplicationUser> userManager,
    IUploadVoice uploadVoice,
    IConfiguration configuration,
    IEmailService emailService,
    IUserRepository userRepository,
    IPushNotificationService pushService,
    IBlockedDevicesRepository blockedDevicesRepository,
    IEventTracker eventTracker,
    AppDbContext context,
    INotificationRepository notificationRepo)   // ← NEW
{
    // ... existing assignments ...
    _notificationRepo = notificationRepo;       // ← NEW
}
```

Then, inside `ChangeUserStatusAsync`, after `if (result.Succeeded)` at [line 142](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/AdminService/AdminService.cs#L142), add the `Notification` entity creation for each status change. Here is the suggested code to insert **inside** the `if (result.Succeeded)` block, **before** the email and ReRecord push logic:

```csharp
if (result.Succeeded)
{
    _eventTracker.Track(EventTypes.VoiceVerificationResult, user.Id, new { status = newStatus.ToString() });

    if (newStatus == UserStatus.Active)
    {
        var alreadyActivated = await _context.UserEvents.AnyAsync(
            e => e.UserId == user.Id && e.EventType == EventTypes.ActivationCompleted);
        if (!alreadyActivated)
        {
            _eventTracker.Track(EventTypes.ActivationCompleted, user.Id);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // NEW: Persist a Notification record for admin status changes
    // so the user can retrieve it later via GET my-notifications,
    // even if the FCM push didn't reach the device.
    // ═══════════════════════════════════════════════════════════════
    Notification? adminNotification = newStatus switch
    {
        UserStatus.Active => new Notification
        {
            UserId = user.Id,
            Title = "Account Verified ✅",
            Message = "Congratulations! Your voice verification has been approved. You now have full access to Cocorra.",
            Type = NotificationType.System,
            CreatedAt = DateTime.UtcNow,
            IsRead = false
        },
        UserStatus.Banned => new Notification
        {
            UserId = user.Id,
            Title = "Account Suspended",
            Message = "Your account has been permanently suspended for violating community guidelines.",
            Type = NotificationType.AdminWarning,
            CreatedAt = DateTime.UtcNow,
            IsRead = false
        },
        UserStatus.Rejected => new Notification
        {
            UserId = user.Id,
            Title = "Verification Rejected",
            Message = "Your voice verification has been rejected. Please contact support for more information.",
            Type = NotificationType.System,
            CreatedAt = DateTime.UtcNow,
            IsRead = false
        },
        UserStatus.ReRecord => new Notification
        {
            UserId = user.Id,
            Title = "إعادة تسجيل صوتي 🎙️",
            Message = "نعتذر منك، نحتاج منك إعادة تسجيل المقطع الصوتي الخاص بك بوضوح أكبر.",
            Type = NotificationType.System,
            CreatedAt = DateTime.UtcNow,
            IsRead = false
        },
        _ => null
    };

    if (adminNotification != null)
    {
        await _notificationRepo.AddAsync(adminNotification);
    }

    // NEW: Send FCM push for Active status (currently missing)
    if (newStatus == UserStatus.Active && !string.IsNullOrEmpty(user.FcmToken))
    {
        var data = new Dictionary<string, string> { { "type", "account_activated" } };
        await _pushService.SendPushNotificationAsync(
            user.FcmToken, "Account Verified ✅",
            "Congratulations! Your account is now fully active.", data);
    }

    try
    {
        await SendVerificationEmailAsync(user, newStatus);
    }
    catch { }

    if (newStatus == UserStatus.ReRecord && !string.IsNullOrEmpty(user.FcmToken))
    {
        var data = new Dictionary<string, string> { { "type", "reRecord" } };
        await _pushService.SendPushNotificationAsync(user.FcmToken,
            "إعادة تسجيل صوتي 🎙️",
            "نعتذر منك، نحتاج منك إعادة تسجيل المقطع الصوتي الخاص بك بوضوح أكبر.", data);
    }

    return Success($"User status changed from {oldStatus} to {newStatus}");
}
```

> [!NOTE]
> **Don't forget the `using` import**: `using Cocorra.DAL.Repository.NotificationRepository;` — this should already be available via the project reference, since `FriendService` and `SupportService` use it the same way.

---

### Fix 4 — Persist `Notification` Entity for `SupportService` Mute/Ban Actions

> **File to modify**: [`Cocorra.BLL/Services/SupportService/SupportService.cs`](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/SupportService/SupportService.cs)

`SupportService` already has `INotificationRepository _notificationRepo` injected ([line 27](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/SupportService/SupportService.cs#L27)), so only the action cases need updating.

#### `Mute24h` — Add Notification record (after [line 188](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/SupportService/SupportService.cs#L188)):

```csharp
case AdminReportAction.Mute24h:
    if (report.ReportedUserId == null)
        return BadRequest<string>("This report has no reported user to mute.");

    var muteUser = await _userManager.FindByIdAsync(report.ReportedUserId.Value.ToString());
    if (muteUser == null) return NotFound<string>("Reported user not found.");

    await _userManager.SetLockoutEnabledAsync(muteUser, true);
    await _userManager.SetLockoutEndDateAsync(muteUser, DateTimeOffset.UtcNow.AddHours(24));

    await _realTimeNotifier.ForceLogoutAsync(
        report.ReportedUserId.Value,
        "Your account has been temporarily suspended for 24 hours.");

    // ── NEW: Persist notification to DB ──
    var muteNotification = new Notification
    {
        UserId = report.ReportedUserId.Value,
        Title = "Account Locked",
        Message = "Your account has been temporarily suspended for 24 hours.",
        Type = NotificationType.AdminWarning,
        ReferenceId = report.Id,
        CreatedAt = DateTime.UtcNow,
        IsRead = false
    };
    await _notificationRepo.AddAsync(muteNotification);

    if (!string.IsNullOrEmpty(muteUser?.FcmToken))
    {
        var data = new Dictionary<string, string> { { "type", "account_locked" } };
        await _pushService.SendPushNotificationAsync(
            muteUser.FcmToken, muteNotification.Title, muteNotification.Message, data);
    }

    report.Status = "Resolved";
    break;
```

#### `BanUser` — Add Notification record (after [line 212](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/SupportService/SupportService.cs#L212)):

```csharp
case AdminReportAction.BanUser:
    if (report.ReportedUserId == null)
        return BadRequest<string>("This report has no reported user to ban.");

    var banUser = await _userManager.FindByIdAsync(report.ReportedUserId.Value.ToString());
    if (banUser == null) return NotFound<string>("Reported user not found.");

    await _userManager.SetLockoutEnabledAsync(banUser, true);
    await _userManager.SetLockoutEndDateAsync(banUser, DateTimeOffset.UtcNow.AddYears(100));

    await _realTimeNotifier.ForceLogoutAsync(
        report.ReportedUserId.Value,
        "Your account has been permanently banned.");

    // ── NEW: Persist notification to DB ──
    var banNotification = new Notification
    {
        UserId = report.ReportedUserId.Value,
        Title = "Account Locked",
        Message = "Your account has been permanently banned.",
        Type = NotificationType.AdminWarning,
        ReferenceId = report.Id,
        CreatedAt = DateTime.UtcNow,
        IsRead = false
    };
    await _notificationRepo.AddAsync(banNotification);

    if (!string.IsNullOrEmpty(banUser?.FcmToken))
    {
        var data = new Dictionary<string, string> { { "type", "account_locked" } };
        await _pushService.SendPushNotificationAsync(
            banUser.FcmToken, banNotification.Title, banNotification.Message, data);
    }

    report.Status = "Resolved";
    break;
```

---

## Verification Checklist

After implementing all fixes, please verify:

- [ ] `firebase-config.json` exists in the API project's `ContentRootPath` at deploy time
- [ ] Run the app and check server logs for `FCM push sent successfully` messages on any notification trigger
- [ ] Trigger a `WarnUser` action from the admin dashboard and verify:
  - Server log shows `FCM push sent successfully`
  - Mobile device receives and displays the notification
  - `GET api/Notifications/my-notifications` includes the warning
- [ ] Trigger a `Mute24h` action and verify the same three checks above
- [ ] Test with an **expired FCM token** — verify the server logs show `FCM push FAILED. MessagingErrorCode: ...` instead of silent failure
- [ ] On iOS, verify that data-only pushes (Ban/Reject) wake the app from background state
- [ ] On Android, verify that pushes arrive promptly even when the device is in Doze mode

---

## Files Changed Summary

| File | Change Type | Description |
|---|---|---|
| `Cocorra.BLL/Services/NotificationService/PushNotificationService.cs` | **Modified** | Add `ILogger`, `AndroidConfig`, `ApnsConfig`, error logging |
| `Cocorra.BLL/Services/AdminService/AdminService.cs` | **Modified** | Inject `INotificationRepository`, persist `Notification` entities for all status changes, add FCM push for `Active` status |
| `Cocorra.BLL/Services/SupportService/SupportService.cs` | **Modified** | Add `Notification` DB persistence for `Mute24h` and `BanUser` actions |
| All call sites (11 locations) | **Simplified** | Remove redundant outer `try { ... } catch { }` wrappers |

---

*Let me know if you need any clarification or would like to discuss alternative approaches. Happy to hop on a call to walk through the changes. — Mobile Team*
