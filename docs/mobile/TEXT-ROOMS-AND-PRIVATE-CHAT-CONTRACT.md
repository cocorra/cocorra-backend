# Text Rooms, Message Reports and Private Chat: Mobile Contract

For the Flutter team. It covers the new text rooms, reporting a text-room message, and the fix for
private chats that disappeared after a room ended.

All REST responses use the usual envelope `{ statusCode, succeeded, message, data, errors }`.
Every timestamp is UTC ISO-8601 with an offset (see `TIME-HANDLING-CHANGES.md`).

**Enums are sent differently on the two transports:**

- **REST** sends enum *names*, for example `"High"` or `"WholeWord"`.
- **SignalR** sends enum *numbers*. Every SignalR result that carries a status also carries a
  `statusName` string. Switch on `statusName`.

---

## 1. Private chat started in a room (bug fix: action needed)

### What was wrong

Room private messages **were** saved to the normal 1-on-1 `Message` table the whole time.
`RoomHub.SendRoomPrivateMessage` goes through the same `ChatService.SaveMessageAsync` as
`ChatHub.SendMessage`. What broke was **live delivery**, which depended on the hub the sender used:

| Sent from | Old delivery | Effect after leaving the room |
|---|---|---|
| Room (`RoomHub.SendRoomPrivateMessage`) | `ReceivePrivateMessage` on **RoomHub only** | The RoomHub connection closes when the user leaves, so later room-sent messages never reached the chat screen, which only listens on ChatHub. |
| Chat screen (`ChatHub.SendMessage`) | `ReceiveMessage` on **ChatHub only** | A partner still in the room, connected only to RoomHub, never saw replies. |

Messages also carried nothing marking where they came from, and the chat list had no friend flag.
So a client that filtered the list down to friends dropped every thread started in a room with a
non-friend.

### What the backend does now

- Every saved private message is delivered on **both hubs, to all connections of both users**,
  whichever surface sent it:
  - ChatHub: `ReceiveMessage` to the receiver, `MessageSent` to the sender.
  - RoomHub: `ReceivePrivateMessage` to the receiver, `PrivateMessageSent` to the sender.
- Messages store where they were sent from. `MessageDto` gains `originRoomId` and `originTextRoomId`.
- `GET /api/Chat/friends-list` returns **every conversation partner** (as before), with new fields:
  - `isFriend` (bool)
  - `startedInRoomId` (Guid or null)
  - `startedInTextRoomId` (Guid or null)
- Messages longer than 1000 characters are rejected with a clear `SendMessageError`. They used to
  fail with a generic error.

### What Flutter must do

1. **Do not filter the chat list by friendship.** Show every entry from `friends-list`. Use
   `isFriend` and `startedInRoomId` for a label such as "Met in a room".
2. **Open a room private chat in the normal chat screen**, using `GET /api/Chat/history/{userId}`.
   You can keep sending through `RoomHub.SendRoomPrivateMessage` while inside the room. After
   leaving, send through `ChatHub.SendMessage`. It is the same thread.
3. **De-duplicate on `MessageDto.id`.** A client connected to both hubs now receives each message
   twice, once per hub.
4. No endpoint or hub method was renamed or removed.

---

## 2. Text rooms: REST (`[Authorize]`, active users)

| Method | Route | Returns |
|---|---|---|
| GET | `/Api/V1/TextRooms` | `TextRoomListItemDto[]`, active rooms only |
| GET | `/Api/V1/TextRooms/{roomId}` | `TextRoomClientConfigDto` |
| GET | `/Api/V1/TextRooms/{roomId}/Messages?beforeId=&limit=` | `TextRoomMessageDto[]`, ascending. Members only; `limit` ≤ 100. |
| POST | `/Api/V1/TextRooms/Messages/{messageId}/Report` | `TextRoomReportResultDto` |

### `TextRoomClientConfigDto`

The server-driven UI config. Render the whole room from it, and replace it whenever
`TextRoomConfigUpdated` arrives.

```json
{
  "roomId": "guid", "title": "string", "subject": "string|null", "description": "string|null",
  "discussionPrompts": ["pinned question 1", "pinned question 2"],
  "status": "Scheduled|Active|Closed",
  "isAlwaysOn": false,
  "opensAt": "…", "closesAt": "…|null", "serverTimeUtc": "…", "nextRotationAt": "…|null",
  "maxParticipants": 50, "overflowIndex": 1, "parentRoomId": "guid|null",
  "showOnlineCount": false, "onlineCount": null, "totalParticipants": null,
  "allowPrivateChat": true, "slowModeSeconds": 0, "maxMessageLength": 500,
  "configVersion": 3
}
```

- **Countdown:** compute `closesAt - serverTimeUtc` once, then count down locally. Do not use the
  device clock.
- **Counts:** `onlineCount` and `totalParticipants` are `null` unless `showOnlineCount` is true.
  Hide the counter when they are null. An Admin can switch counts on live, and the change arrives
  as `TextRoomConfigUpdated`.
- **Overflow:** `overflowIndex > 1` means an overflow instance. Show it as "Room #2" and so on.
- `maxParticipants: null` means unlimited.
- `configVersion` increases on every change. Ignore an update whose version is lower than the one
  you hold.

### Report a message

`POST /Api/V1/TextRooms/Messages/{messageId}/Report` with body
`{ "category": "Harassment", "description": "optional, ≤ 1000" }`.

`category` is one of `InappropriateContent`, `Harassment`, `Spam`, `FakeIdentity` or `Other`.

| Status | Meaning |
|---|---|
| 201 | `{ reportId, severity, severityName }` |
| 400 | You reported your own message. |
| 403 | You are not a member of the message's room. |
| 404 | The message is missing or was deleted. |
| 409 | You already reported this message. |

The server stores a copy of the messages around the reported one, by default 25 before and
25 after, for the moderators. The app sends nothing extra.

---

## 3. Text rooms: SignalR hub `/hubs/text-rooms`

Authenticate with `?access_token=<jwt>`, as on the other hubs. Methods return their result directly
(an invocation result), so use `invoke`, not `send`.

### Client → server

| Method | Args | Returns |
|---|---|---|
| `JoinTextRoom` | `roomId` | `TextRoomJoinResultDto` |
| `LeaveTextRoom` | `roomId` | — |
| `SendTextRoomMessage` | `roomId, content` | `SendTextRoomMessageResultDto` |
| `SendTextRoomPrivateMessage` | `roomId, targetUserId, content` | `Response<MessageDto>`, a normal persisted DM (see section 1) |

**`TextRoomJoinResultDto`:** `{ status, statusName, roomId, config, recentMessages[], mutedUntil }`.

`statusName` is one of:

| `statusName` | Meaning |
|---|---|
| `Joined` | You are in the room. |
| `Redirected` | The room was full. **You were placed in `roomId`, an overflow instance.** Use the returned `roomId` and `config` from then on. |
| `Full` | The room is full and no overflow is available. |
| `Closed` | The room has closed. |
| `Kicked` | You were removed from this room and cannot rejoin it. |
| `NotFound` | No room has this id. |
| `NotOpenYet` | The room is scheduled and has not opened. |

**`SendTextRoomMessageResultDto`:** `{ status, statusName, message, retryAfterSeconds, mutedUntil, error }`.

`statusName` is one of:

| `statusName` | Meaning |
|---|---|
| `Sent` | Delivered as written. |
| `Masked` | Delivered, with some words replaced by `*`. Render `message.content`, not the local draft. |
| `Blocked` | Rejected by the content policy and not delivered. Show `error`. |
| `SlowMode` | Wait `retryAfterSeconds`. |
| `TooLong` | Longer than `maxMessageLength`. |
| `Empty` | The message was blank. |
| `Muted` | Muted until `mutedUntil`. |
| `NotMember` | Join the room first. |
| `Closed` | The room has closed. |
| `NotFound` | No room has this id. |

### Server → client

| Event | Payload |
|---|---|
| `TextRoomMessageReceived` | `TextRoomMessageDto { id, roomId, senderId, senderName, senderProfilePicturePath, content, isMasked, createdAt }` |
| `TextRoomMessageDeleted` | `{ roomId, messageId }`: remove it from the list |
| `TextRoomConfigUpdated` | `TextRoomClientConfigDto` |
| `TextRoomPresenceUpdated` | `{ roomId, onlineCount, totalParticipants }`: sent to rooms only while `showOnlineCount` is true |
| `TextRoomHistoryCleared` | `{ roomId }`: the topic rotated, so clear the visible history |
| `TextRoomClosed` | `{ roomId, reason }` |
| `TextRoomUserMuted` | `{ roomId, mutedUntil, reason }`: sent only to the muted user |
| `TextRoomKicked` | `{ roomId, reason }`: sent only to the kicked user. Leave the room screen. |
| `TextRoomListChanged` | `{}`: refetch `GET /Api/V1/TextRooms` |

Message ids are numbers (`long`), in chronological order within a room. Use
`GET …/Messages?beforeId=<oldest id>` for older pages.
