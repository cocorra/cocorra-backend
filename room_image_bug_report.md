# 🐛 Bug Report: Room Images Always Show Default Fallback

**Reporter**: Mobile Team
**Priority**: 🟡 Medium
**Affected Service**: `RoomService.CreateRoomAsync` — room image upload path
**Symptom**: Room is created successfully, image upload completes without error, but the room always displays the default fallback image.

---

## Root Cause

The room image upload is a **two-step process** in [`RoomService.CreateRoomAsync`](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/RoomService/RoomService.cs#L86-L106):

1. **Step 1** — Upload via `SaveImageAsync` (works correctly):
   ```csharp
   var savedPath = await _uploadImage.SaveImageAsync(roomImage);
   // savedPath = "https://storage.cocorraapp.com/cocorra-assets/Uploads/img/Profiles/abc123.jpg"
   ```
   [`UploadImage.SaveImageAsync`](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/UploadService/UploadImage.cs#L29-L73) uploads to **MinIO/S3** and returns a **full HTTPS URL** (line 65):
   ```csharp
   return $"{_settings.PublicUrl}/{_settings.BucketName}/{objectKey}";
   ```

2. **Step 2** — "Relocate" from Profiles to Rooms (BROKEN):
   ```csharp
   // Line 94: String.Replace on the URL — this part works
   imagePath = savedPath.Replace("Uploads/img/Profiles/", "Uploads/img/Rooms/");
   // imagePath = "https://storage.cocorraapp.com/cocorra-assets/Uploads/img/Rooms/abc123.jpg"

   // Lines 96-104: LOCAL filesystem move — THIS SILENTLY FAILS
   var profilesPath = Path.Combine(GetContentPath(), savedPath.Replace("/", ...));
   // profilesPath = "C:\app\wwwroot\https:\storage.cocorraapp.com\..." ← NONSENSICAL PATH
   if (File.Exists(profilesPath))  // ← Always false! File is on S3, not local disk
   {
       File.Move(profilesPath, roomsFilePath);  // ← NEVER EXECUTES
   }
   ```

**Result**:
- The file exists on S3 at `Uploads/img/Profiles/abc123.jpg` ✅
- The database stores `imagePath = "https://.../Uploads/img/Rooms/abc123.jpg"` ❌
- The Feed API returns this URL to Flutter
- Flutter tries to load it → **404 Not Found** → falls back to default asset

> [!IMPORTANT]
> The file was **successfully uploaded** to S3 under the `Profiles/` prefix. The bug is that the "relocation" code assumes local filesystem storage but `UploadImage` was refactored to use MinIO/S3. The S3 object was never copied/moved to the `Rooms/` prefix.

---

## Audit Summary

| Layer | Status | Notes |
|---|---|---|
| **Flutter: Image Picker** | ✅ OK | `add_room_screen.dart` picks image via `ImagePicker`, stores in `_selectedImage`, passes to model |
| **Flutter: FormData Upload** | ✅ OK | `toCreateFormData()` sends `roomImage` as `MultipartFile` — key matches Swagger spec exactly |
| **Flutter: Display Logic** | ✅ OK | `room_card.dart` `_buildRoomImage()` handles `File`, network URL, and asset fallback correctly |
| **Backend: Controller** | ✅ OK | `RoomsController.Create` receives `IFormFile? roomImage` as separate parameter |
| **Backend: S3 Upload** | ✅ OK | `UploadImage.SaveImageAsync` uploads to S3 successfully |
| **Backend: File Relocation** | ❌ **BUG** | Uses `File.Move` (local FS) but file is on S3 — relocation silently fails |
| **Backend: DB Storage** | ❌ **WRONG URL** | Stores Rooms/ path but file exists at Profiles/ path |

---

## Fix Option A — Upload Directly to `Rooms/` (Recommended — Simplest)

Skip the relocation entirely. Upload the file to the `Rooms/` prefix from the start.

**File**: [`RoomService.cs`](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/RoomService/RoomService.cs#L86-L106)

Replace lines 86–106 with:

```csharp
// Handle room image upload
string? imagePath = null;
if (roomImage != null && roomImage.Length > 0)
{
    // Upload directly to the Rooms subfolder on S3 — no relocation needed.
    // SaveImageAsync always uploads to Profiles/, so we use a new overload
    // or just accept the Profiles path and use it as-is (images are images
    // regardless of which S3 prefix they're under).
    var savedPath = await _uploadImage.SaveImageAsync(roomImage);
    if (!savedPath.StartsWith("Error"))
    {
        imagePath = savedPath; // Use the exact S3 URL returned by the upload service
    }
}
```

> [!TIP]
> This is the simplest fix: just use the URL that `SaveImageAsync` returns directly. The image will live under `Uploads/img/Profiles/` on S3 which is fine — the URL works regardless. If you want organizational separation, see Fix Option B.

---

## Fix Option B — S3 Copy + Delete (If You Need `Rooms/` Prefix)

If you need the image under `Uploads/img/Rooms/` on S3 for organizational purposes, replace the `File.Move` with an S3 `CopyObject` + `DeleteObject`:

**File**: [`RoomService.cs`](file:///d:/cocora/lib/cocorra-backend-main/cocorra-backend-main/Cocorra.BLL/Services/RoomService/RoomService.cs#L86-L106)

First, inject `IAmazonS3` and `MinioSettings` into `RoomService`:

```csharp
private readonly IAmazonS3 _s3Client;
private readonly MinioSettings _minioSettings;

public RoomService(
    // ... existing parameters ...
    IAmazonS3 s3Client,
    IOptions<MinioSettings> minioSettings)
{
    // ... existing assignments ...
    _s3Client = s3Client;
    _minioSettings = minioSettings.Value;
}
```

Then replace lines 86–106:

```csharp
// Handle room image upload
string? imagePath = null;
if (roomImage != null && roomImage.Length > 0)
{
    var savedPath = await _uploadImage.SaveImageAsync(roomImage);
    if (!savedPath.StartsWith("Error"))
    {
        // savedPath is a full S3 URL: https://storage.../bucket/Uploads/img/Profiles/abc.jpg
        // We need to move the S3 object from Profiles/ to Rooms/ prefix.
        var sourceKey = "Uploads/img/Profiles/" + Path.GetFileName(new Uri(savedPath).AbsolutePath);
        var destKey = "Uploads/img/Rooms/" + Path.GetFileName(new Uri(savedPath).AbsolutePath);

        try
        {
            // Copy to new key
            await _s3Client.CopyObjectAsync(new Amazon.S3.Model.CopyObjectRequest
            {
                SourceBucket = _minioSettings.BucketName,
                SourceKey = sourceKey,
                DestinationBucket = _minioSettings.BucketName,
                DestinationKey = destKey
            });

            // Delete old key
            await _s3Client.DeleteObjectAsync(new Amazon.S3.Model.DeleteObjectRequest
            {
                BucketName = _minioSettings.BucketName,
                Key = sourceKey
            });

            // Build the new full URL
            imagePath = $"{_minioSettings.PublicUrl}/{_minioSettings.BucketName}/{destKey}";
        }
        catch
        {
            // If S3 move fails, fall back to the original Profiles URL
            imagePath = savedPath;
        }
    }
}
```

---

## Verification

After applying either fix, verify:

- [ ] Create a room with an image from the Flutter app
- [ ] Check server logs — no error from `SaveImageAsync`
- [ ] Call `GET /Api/V1/Room/Feed` and inspect the `RoomImage` field in the response
- [ ] Verify the URL resolves (paste in browser) — should return the image, not 404
- [ ] Verify the Flutter room card displays the uploaded image instead of the default

---

*— Mobile Team*
