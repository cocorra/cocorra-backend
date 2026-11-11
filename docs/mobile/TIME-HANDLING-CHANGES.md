# Time Handling Changes: What the Flutter App Needs to Know

Audience: the Flutter developer. You do not need to have followed the backend work; everything you need is here.

Scope: backend commits `1c2cdeb`, `1b21304` and `de88d7b` on `main`, compared with what production runs today (`0cb90e1`). The Flutter source was not available while writing this, so every item says what the **server** does. Where we could not confirm whether the app uses an endpoint, the text says so.

---

## 1. Summary

The server stored every timestamp in UTC, but it sent them in two formats. Values read from the database had no time zone marker (`"2026-11-11T20:00:00"`), while values created in memory ended in `Z`. A client that parsed the first kind as local time showed times 2 hours off in winter and 3 hours off in summer.

The server now:
- sends every `DateTime` as UTC with a trailing `Z`;
- requires an explicit `Z` or offset on the one mobile-reachable write that takes a timestamp, `ScheduledStartDate`, and rejects anything else with `400`;
- computes day, week and hour buckets on the Africa/Cairo calendar. That calendar observes daylight saving time.

None of these changes add, remove or rename a field outside the admin-only analytics API.

---

## 2. Breaking changes (fix before the backend deploys)

### 2.1 `ScheduledStartDate` must carry `Z` or an offset

| | |
|---|---|
| Endpoint | `POST /Api/V1/Room/Create`, `multipart/form-data` |
| Who can call it | Roles `Admin` and `Coach` only. Regular users get `403`. **We could not confirm whether the app has a Create Room screen for coaches.** If it does not, this item does not affect you. |
| Field | `ScheduledStartDate` (optional) |
| Change | A value without `Z` or a `±HH:MM` offset now returns `400` and no room is created. Before, it was accepted and silently read as UTC, so a Cairo wall-clock time scheduled the room 2–3 hours late. |

**Before:** this request was accepted.
```
POST /Api/V1/Room/Create
Content-Type: multipart/form-data

RoomTitle=Evening talk
DurationHours=2
Category=...
ScheduledStartDate=2026-11-11T22:00:00
```
```json
HTTP/1.1 200 OK
{"statusCode":"Created","meta":null,"succeeded":true,"message":"Created Successfully","errors":null,"data":"3f2b…-room-id"}
```

**After:** the same request is rejected.
```json
HTTP/1.1 400 Bad Request
{"statusCode":"BadRequest","meta":null,"succeeded":false,"message":"ScheduledStartDate must include a time zone offset, for example 2026-11-11T20:00:00Z or 2026-11-11T22:00:00+02:00.","errors":[],"data":"00000000-0000-0000-0000-000000000000"}
```
Notes on this body:
- `statusCode` is the string `"BadRequest"`, not a number.
- `+` is the JSON escape for `+`, and `jsonDecode` turns it back into `+`.
- Branch on the HTTP status code or on `succeeded`, not on the message text.

The request is accepted with either of these values for 22:00 Cairo time on 11 November 2026:
```
ScheduledStartDate=2026-11-11T20:00:00.000Z
ScheduledStartDate=2026-11-11T22:00:00+02:00
```

**Dart change.** A local `DateTime`'s `toIso8601String()` has no offset (`"2026-11-11T22:00:00.000"`), and the server now rejects it. Convert to UTC first.

```dart
// Wrong: "2026-11-11T22:00:00.000", rejected with 400
request.fields['ScheduledStartDate'] = pickedLocal.toIso8601String();

// Correct: "2026-11-11T20:00:00.000Z" on a device in Cairo
request.fields['ScheduledStartDate'] = pickedLocal.toUtc().toIso8601String();
```

With `package:http`:
```dart
final request = http.MultipartRequest('POST', createRoomUri)
  ..fields['RoomTitle'] = title
  ..fields['DurationHours'] = '2'
  ..fields['Category'] = category
  ..fields['ScheduledStartDate'] = pickedLocal.toUtc().toIso8601String();
```

With `package:dio`:
```dart
final formData = FormData.fromMap({
  'RoomTitle': title,
  'DurationHours': 2,
  'Category': category,
  'ScheduledStartDate': pickedLocal.toUtc().toIso8601String(),
});
```

To create a room that starts now, leave `ScheduledStartDate` out.

### 2.2 Other parameters that now reject offset-less values

The same rule (`400` for a timestamp without `Z` or an offset) now applies to the `from` and `to` query parameters of:
- every `/Api/V1/Analytics/*` endpoint;
- `GET /Api/V1/Admin/Invites/Stats`.

All of them require the `Admin` role, and authorization runs before the check, so a regular user or coach gets `403` first. **No other request body, form field, query parameter or SignalR hub method that the app can reach takes a timestamp.** If the app has no admin screens, nothing else here breaks.

---

## 3. Behaviour changes (no crash, different results)

### 3.1 Every `DateTime` in a response now ends in `Z`

Every `DateTime` read from the database used to go out with no offset, and now goes out with `Z`. These are the fields the app can reach:

| Endpoint | Fields |
|---|---|
| `GET /Api/V1/Room/Feed` | `data[].scheduledStartDate` |
| `GET /Api/V1/Room/{roomId}/State` | `data.participants[].joinedAt` |
| `GET /api/Chat/friends-list` | `data[].lastMessageDate` |
| `GET /api/Chat/history/{friendId}` | `data[].createdAt` |
| `GET /api/Notifications/my-notifications` | `data[].createdAt` |
| `GET /Api/V1/Room/{roomId}/Feedback/Me` | `data.feedback.createdAt`, `data.feedback.updatedAt` |
| `POST /Api/V1/Room/{roomId}/Feedback` (when updating an existing rating) | `data.createdAt` |
| `GET /Api/V1/Invites/{inviteCode}`; `DELETE /Api/V1/Invites/{inviteCode}`; the `410` response of `POST /Api/V1/Invites/{inviteCode}/Accept` | `data.expiresAt` |
| `GET /Api/V1/Support/chat/my-chat`, `GET /Api/V1/Support/chat/history` | `createdAt`, `closedAt`, `messages[].createdAt` |

```
Before: "scheduledStartDate": "2026-11-11T20:00:00"
After:  "scheduledStartDate": "2026-11-11T20:00:00Z"

Before: "createdAt": "2026-11-11T20:00:00.1234567"
After:  "createdAt": "2026-11-11T20:00:00.1234567Z"
```

Fractional seconds have 0 to 7 digits, with trailing zeros dropped.

**Already ended in `Z`, unchanged:**
- login and refresh `expiresOn` and `refreshTokenExpiration`;
- `POST /Api/V1/Room/{roomId}/Invites` `expiresAt`;
- the `message.createdAt` returned when sending a support message;
- every SignalR timestamp: `ReceiveMessage`, `MessageSent`, `ReceivePrivateMessage` and `PrivateMessageSent` `createdAt`; `ReceiveRoomMessage` `timestamp`; `HostDisconnected` `reconnectDeadline`; `ForceLogout` `timestamp`; and `ReceiveSupportMessage` `createdAt`.

**Exception, still `+00:00`:** the `403` response of login and refresh for a locked account carries `meta.lockoutEnd`, which is a `DateTimeOffset`. It still serializes as `"2026-11-12T20:00:00.1234567+00:00"`. `DateTime.parse` handles both forms, so treat it the same way.

**What this means for the app.** Before, `DateTime.parse("2026-11-11T20:00:00")` returned a *local* `DateTime` holding the UTC clock reading. Now `DateTime.parse("2026-11-11T20:00:00Z")` returns a UTC `DateTime`, with `isUtc == true`.
- If the app formats the parsed value without `.toLocal()`, it still shows the UTC clock reading, which is 2–3 hours behind Cairo, exactly as before. Add `.toLocal()` to fix it (§4.1).
- If the app compared these values with `DateTime.now()`, for countdowns or "starts in", those comparisons were off by 2–3 hours before and are now correct.
- If the app added a fixed `+2`/`+3` hours anywhere to compensate, remove it: the times would now be shifted twice.

### 3.2 A past `ScheduledStartDate` reports the actual go-live time

A `ScheduledStartDate` at or before the server's current time already made the room go live immediately; that has not changed. What changed is what the feed reports. `scheduledStartDate` in `GET /Api/V1/Room/Feed` is now the moment the room actually went live, not the past time that was sent.

```
Sent:   ScheduledStartDate=2026-11-11T18:00:00Z   (server time is 20:00:05Z)
Before: "status":"Live", "scheduledStartDate":"2026-11-11T18:00:00"
After:  "status":"Live", "scheduledStartDate":"2026-11-11T20:00:05.1234567Z"
```

### 3.3 FCM `lockout_end` uses `Z` instead of `+00:00`

The data-only push with `type: "account_locked"` carries `lockout_end`:

| Trigger | Before | After |
|---|---|---|
| 24-hour mute from a report | `"2026-11-12T20:00:00.1234567+00:00"` | `"2026-11-12T20:00:00.1234567Z"` |
| Ban from a report (100 years) | `"2126-11-11T20:00:00.1234567+00:00"` | `"2126-11-11T20:00:00.1234567Z"` |
| Ban by admin status change | `"9999-12-31T23:59:59.9999999+00:00"` | `"9999-12-31T23:59:59.9999999Z"` |

It is the same instant in a different spelling. `DateTime.parse` accepts both forms, including the 7 fractional digits, which were already there before. The change only matters if the app handles the string itself, for example with a regex or `substring`. No other FCM payload carries a timestamp.

### 3.4 Analytics changes do not apply to the app

The date-only fields (`weekStart`, `cohortWeekStart`, `lastSnapshotDate`), the renamed fields (`byHour`, `byDayOfWeek`, `scheduleByHour`) and the removed `suggestedDisplayOffsetMinutes` all exist only on `/Api/V1/Analytics/*`. Every endpoint there requires the `Admin` role, at controller level. **If the app has no admin screens, it does not call these endpoints and you can ignore them.** If it does, see `COCORRA-ANALYTICS-MOBILE-INTEGRATION-GUIDE.md` §6.

---

## 4. Rules for handling time in the app

### 4.1 Parse with `DateTime.parse`, call `.toLocal()` before display
```dart
final startUtc = DateTime.parse(json['scheduledStartDate'] as String); // isUtc == true
final label = DateFormat('EEE d MMM, h:mm a').format(startUtc.toLocal());
```
Formatting `startUtc` directly prints the UTC clock reading.

### 4.2 Send `.toUtc().toIso8601String()`
```dart
final value = picked.toUtc().toIso8601String(); // "...Z"
```
Never send `picked.toIso8601String()` for a local `DateTime`, because it has no offset.

### 4.3 URL-encode date query parameters
Build URLs with `Uri(queryParameters: ...)`, or with dio's `queryParameters`, instead of concatenating strings. An unencoded `+` in a query string is decoded as a space, so `...T22:00:00+02:00` arrives as `...T22:00:00 02:00` and fails. `Uri` encodes `+` as `%2B`. Sending UTC values (`Z`) avoids the `+` entirely.
```dart
final uri = Uri.https(apiHost, '/some/path', {
  'from': fromLocal.toUtc().toIso8601String(),
});
```

### 4.4 Do countdowns and comparisons in UTC
```dart
final remaining = startUtc.difference(DateTime.now().toUtc());
final hasStarted = remaining <= Duration.zero;
```
`isBefore`, `isAfter` and `difference` compare instants. Mixing UTC and local values still causes bugs:
- `==` is `false` for the same instant when `isUtc` differs; use `isAtSameMomentAs`.
- `.day` and `.hour` read different clocks.
- Adding `Duration(days: 1)` to a local value can shift the hour across a DST change.

Keep the values you compare in UTC, and convert only for display.

### 4.5 Never add a fixed `+2` or `+3`
Egypt observes daylight saving time: UTC+2 in winter, UTC+3 in summer. In 2026, the clocks went forward on the night of 23/24 April (00:00 became 01:00) and back on the night of 29/30 October (24:00 became 23:00). A fixed offset is wrong for half the year, and wrong for every user outside Egypt.
```dart
// Wrong in summer: 17:10Z + 2h = 19:10, but Cairo shows 20:10
final wrong = utc.add(const Duration(hours: 2));
// Correct for the device's zone
final right = utc.toLocal();
```

### 4.6 When to use the `timezone` package instead of `.toLocal()`
`.toLocal()` uses the **device's** zone, which is right for most screens. Use `package:timezone` with `Africa/Cairo` when the screen must show or interpret **Cairo** time whatever zone the device is in:
- showing an Egypt schedule as Cairo time to a user who is travelling;
- treating a time the user picks as Cairo wall-clock time on a device set to another zone.

```dart
import 'package:timezone/data/latest.dart' as tz;
import 'package:timezone/timezone.dart' as tz;

tz.initializeTimeZones(); // once, in main()
final cairo = tz.getLocation('Africa/Cairo');

final cairoDisplay = tz.TZDateTime.from(startUtc, cairo);
final pickedCairo = tz.TZDateTime(cairo, y, m, d, hour, minute);
final scheduledStart = pickedCairo.toUtc().toIso8601String();
```

### 4.7 Date-only strings are calendar dates
`DateTime.parse("2026-11-14")` returns local midnight. Format it as it is. Do not call `.toUtc()` on it: in Cairo, that turns it into 13 November. This only matters if the app reads admin analytics (§3.4).

### 4.8 `lockout_end`
```dart
final raw = message.data['lockout_end'] as String?;
final lockoutEnd = raw == null ? null : DateTime.tryParse(raw);
final nowUtc = DateTime.now().toUtc();
final isLocked = lockoutEnd != null && lockoutEnd.isAfter(nowUtc);
```
Treat a far-future value (year 2126 or 9999) as a permanent ban, and do not render it as a countdown.

---

## 5. Test checklist

Run these on a build that includes the changes from §2 and §4.

1. **Schedule from Cairo.** Set the device zone to `Africa/Cairo` and create a room 1 hour ahead.
   - In a proxy, check that `ScheduledStartDate` ends in `Z` and is 2 hours (winter) or 3 hours (summer) behind the time you picked.
   - Check that the feed shows the room at the time you picked.
2. **Other zones.** View the same room with the device set to `Europe/London`, then to `Asia/Tokyo`. Each must show the same instant in its own local time; for example, `20:00Z` shows as 22:00 in Cairo, 20:00 in London and 05:00 the next day in Tokyo. Then create a room from `Asia/Tokyo` and confirm a Cairo device shows it at the matching Cairo time.
3. **Past start.** Create a room with a `ScheduledStartDate` 5 minutes in the past, using a debug build or `curl`. Expect `200`, and the room goes live immediately. Its `scheduledStartDate` in the feed is the creation time, not the past time you sent.
4. **Offset-less value.** Send `ScheduledStartDate=2026-12-01T20:00:00` with `curl` or a debug build. Expect `400` with the body shown in §2.1, and no room created. The app must show an error rather than crash.
5. **DST edge cases.** Write a unit test of the code that builds `ScheduledStartDate`; dates in the past cannot be scheduled for real. For each value below, assert that the output ends in `Z` and that converting it back gives the minute you picked.
   - **Repeated hour:** 23:30 Cairo on 29 October 2026. This hour happens twice. It is `20:30Z` at UTC+3 or `21:30Z` at UTC+2.
   - **Non-existent time:** 00:30 Cairo on 24 April 2026. It does not exist; the clock jumps from 00:00 to 01:00, which is 22:00Z.

   Run both values through `DateTime(...).toUtc()` on a device in Cairo and through `tz.TZDateTime(cairo, ...)`, and record which instant each one picks. **We have not verified which occurrence each library picks.** If the result matters for the product, add an explicit rule to the picker, such as rejecting or snapping times in the gap.
6. **Zone change while open.** Open a room scheduled 45 minutes ahead and watch its countdown. Change the device zone from Cairo to Tokyo to London and return to the app. The countdown must continue without jumping, while the clock labels update.
7. **Lists and chat.** With the device in Cairo, check against the status-bar clock:
   - chat history and the friends list;
   - notifications;
   - support chat;
   - live room messages;
   - room start times.

   None may be 2 or 3 hours off.
8. **Account lock push.** Trigger a 24-hour mute and a ban from the admin panel, with the app in the foreground and in the background.
   - `lockout_end` parses without an exception.
   - The mute shows about 24 hours remaining.
   - The ban shows as permanent.
   - A `lockout_end` in the past counts as unlocked.
9. **UTC+0 and UTC−5.** Repeat steps 1, 2, 6, 7 and 8 with the device set to `UTC` and then to `America/Bogota` (UTC−5).
   - At UTC−5, an instant such as `2026-11-11T02:00:00Z` must show on the previous local evening, 10 November at 21:00.
   - Countdowns must match a Cairo device to the second.

---

## 6. Deployment order

1. Ship the app changes from §2.1 and §4, or confirm that the current production build already sends `ScheduledStartDate` with `Z` or an offset, or never sends it at all.
2. Wait until the updated build is approved and **live** in the stores, and enough users have updated. Older builds that send offset-less values will get `400` on room creation once the backend is deployed.
3. Only then deploy the backend. The backend and the admin dashboard are deployed together.
4. The §3 changes do not need a separate app release to avoid crashes. Without §4.1, however, times keep showing 2–3 hours off, as they do today.
