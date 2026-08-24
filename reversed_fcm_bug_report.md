# 🐛 Bug Report: Reversed FCM Notification Recipients

**Priority**: 🔴 CRITICAL
**Date**: 2026-08-24
**Audited By**: Mobile Team (read-only backend audit)

---

## Executive Summary

Notifications are being delivered to the **wrong user** because the backend **never clears `user.FcmToken`** when a user logs out (revokes their token). Since FCM device tokens are **device-bound** (not user-bound), a stale token left on User A's database row will route pushes to whoever currently owns that physical device — which is now User B.

---

## Root Cause Diagram

```
User A logs in on Device X  →  FcmToken = "abc123" saved to User A's row  ✅
User A logs out              →  RefreshToken = null, but FcmToken STAYS "abc123"  ❌
User B logs in on Device X  →  FcmToken = "abc123" saved to User B's row  ✅
                                User A's row STILL has FcmToken = "abc123"  💀

Admin bans User A:
  → Backend reads User A's FcmToken = "abc123"
  → Sends push to "abc123"
  → Device X receives it
  → But User B is now on Device X!  💥 WRONG RECIPIENT
```

---

## Bug #1 (CRITICAL): `RevokeTokenAsync` Does Not Clear `FcmToken`

### File
[`AuthServices.cs`](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/AuthService/AuthServices.cs) — Line 616

### Buggy Code
```csharp
public async Task<Response<string>> RevokeTokenAsync(Guid userId)
{
    var user = await _userManager.FindByIdAsync(userId.ToString());
    if (user == null) return BadRequest<string>("Invalid user");

    if (string.IsNullOrEmpty(user.RefreshToken))
        return Success<string>("Token already revoked.");

    user.RefreshToken = null;
    // ❌ FcmToken is NOT cleared — stale token stays in DB
    await _userManager.UpdateAsync(user);

    return Success<string>("Token revoked successfully.");
}
```

### Fix
```csharp
public async Task<Response<string>> RevokeTokenAsync(Guid userId)
{
    var user = await _userManager.FindByIdAsync(userId.ToString());
    if (user == null) return BadRequest<string>("Invalid user");

    if (string.IsNullOrEmpty(user.RefreshToken))
        return Success<string>("Token already revoked.");

    user.RefreshToken = null;
    user.FcmToken = null;  // ✅ Clear stale FCM token to prevent cross-user delivery
    await _userManager.UpdateAsync(user);

    return Success<string>("Token revoked successfully.");
}
```

---

## Bug #2 (CRITICAL): `ChangeUserStatusAsync` — Ban Flow Doesn't Clear `FcmToken`

### File
[`AdminService.cs`](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/AdminService/AdminService.cs) — Line 105

### Buggy Code
```csharp
case UserStatus.Banned:
    await _userManager.SetLockoutEnabledAsync(user, true);
    banLockoutEnd = DateTimeOffset.MaxValue;
    await _userManager.SetLockoutEndDateAsync(user, banLockoutEnd);
    _uploadVoice.DeleteVoice(user.VoiceVerificationPath);
    user.VoiceVerificationPath = null;
    user.RefreshToken = null;
    // ❌ FcmToken is NOT cleared after the ban push is sent
    break;
```

### Why This Matters
The ban push itself is sent correctly (L221) using `user.FcmToken` **before** the token is cleared. But the `FcmToken` is never nullified afterward. If the banned user's device is later used by another user, the stale token remains and can cause phantom pushes.

### Fix
```csharp
case UserStatus.Banned:
    await _userManager.SetLockoutEnabledAsync(user, true);
    banLockoutEnd = DateTimeOffset.MaxValue;
    await _userManager.SetLockoutEndDateAsync(user, banLockoutEnd);
    _uploadVoice.DeleteVoice(user.VoiceVerificationPath);
    user.VoiceVerificationPath = null;
    user.RefreshToken = null;
    // ✅ FcmToken is cleared AFTER the push block (L200-L236) sends the ban push.
    // The push uses the token before it's wiped.
    break;
```

Then, **after the push block** (after L236), add:

```csharp
// Clear the FCM token AFTER the push is sent, so the ban notification
// is delivered but future pushes can't reach a new device owner.
if (newStatus == UserStatus.Banned || newStatus == UserStatus.Rejected)
{
    user.FcmToken = null;
    await _userManager.UpdateAsync(user);
}
```

> [!IMPORTANT]
> The push to `user.FcmToken` must happen **before** clearing it. The current code structure already does this (push is at L200-L236, DB update is at L133). Just add the `FcmToken = null` after the push block.

---

## Bug #3 (MODERATE): `ChatService.SaveMessageAsync` — Silent `catch {}`

### File
[`ChatService.cs`](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/ChatService/ChatService.cs) — Line 105

### Buggy Code
```csharp
catch { }  // ❌ Swallows ALL exceptions — impossible to debug
```

### Fix
```csharp
catch (Exception ex)
{
    // Log but don't fail the message save
    _logger.LogError(ex, 
        "FCM push failed for chat message. SenderId: {SenderId}, ReceiverId: {ReceiverId}", 
        senderId, receiverId);
}
```

> [!NOTE]
> This requires injecting `ILogger<ChatService>` into the constructor.

---

## Verification After Fix

After applying these fixes, verify with this scenario:

1. **User A** logs into the app on **Device X** → FCM token `"abc123"` is saved to User A's row ✅
2. **User A** logs out → `RevokeTokenAsync` clears both `RefreshToken` AND `FcmToken` from User A's row ✅
3. **User B** logs into the app on **Device X** → FCM token `"abc123"` is saved to User B's row ✅
4. **Admin warns User A** → Backend reads User A's `FcmToken` → it's `null` → push is **skipped** (no delivery to wrong user) ✅
5. **User C sends a message to User B** → Backend reads User B's `FcmToken` = `"abc123"` → push arrives on Device X → **correct** ✅

---

## Summary Table

| Bug | File | Severity | Fix |
|-----|------|----------|-----|
| #1: Stale FcmToken on logout | `AuthServices.cs` L616 | 🔴 CRITICAL | Add `user.FcmToken = null` in `RevokeTokenAsync` |
| #2: Stale FcmToken after ban | `AdminService.cs` L105 | 🔴 CRITICAL | Clear `FcmToken` after sending the ban push |
| #3: Silent catch in chat push | `ChatService.cs` L105 | 🟡 MODERATE | Replace `catch {}` with `ILogger` |

---

*— Generated via read-only backend audit by the Mobile Team*
