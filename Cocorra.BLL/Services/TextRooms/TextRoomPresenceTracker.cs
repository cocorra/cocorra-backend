using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Cocorra.BLL.Services.TextRooms
{
    public sealed class TextRoomPresenceTracker : ITextRoomPresenceTracker
    {
        // Structural mutations span three indices; synchronizing writes preserves cross-index
        // invariants while GetOnlineCount/IsOnline/GetGlobalOnlineUserCount stay lock-free O(1).
        private readonly Lock _sync = new();
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, HashSet<string>>> _rooms = new();
        private readonly ConcurrentDictionary<string, HashSet<(Guid RoomId, Guid UserId)>> _connections = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<Guid, int> _userRoomCounts = new();

        public bool AddConnection(Guid roomId, Guid userId, string connectionId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

            lock (_sync)
            {
                var roomUsers = _rooms.GetOrAdd(roomId, static _ => new ConcurrentDictionary<Guid, HashSet<string>>());
                var wentOnline = false;

                if (!roomUsers.TryGetValue(userId, out var userConnections))
                {
                    userConnections = new HashSet<string>(StringComparer.Ordinal);
                    roomUsers[userId] = userConnections;
                    IncrementUserRoomCount(userId);
                    wentOnline = true;
                }

                userConnections.Add(connectionId);

                var connectionEntries = _connections.GetOrAdd(connectionId, static _ => new HashSet<(Guid, Guid)>());
                connectionEntries.Add((roomId, userId));

                return wentOnline;
            }
        }

        public IReadOnlyList<TextRoomPresenceChange> RemoveConnection(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
                return Array.Empty<TextRoomPresenceChange>();

            lock (_sync)
            {
                if (!_connections.TryRemove(connectionId, out var entries) || entries.Count == 0)
                    return Array.Empty<TextRoomPresenceChange>();

                var changes = new List<TextRoomPresenceChange>(entries.Count);
                foreach (var (roomId, userId) in entries)
                {
                    var wentOffline = RemoveConnectionFromRoomCore(roomId, userId, connectionId);
                    changes.Add(new TextRoomPresenceChange(roomId, userId, wentOffline));
                }

                return changes;
            }
        }

        public TextRoomPresenceChange? RemoveConnectionFromRoom(Guid roomId, string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
                return null;

            lock (_sync)
            {
                if (!_connections.TryGetValue(connectionId, out var entries))
                    return null;

                var matching = entries.FirstOrDefault(e => e.RoomId == roomId);
                if (matching.RoomId == Guid.Empty)
                    return null;

                entries.Remove(matching);
                if (entries.Count == 0)
                    _connections.TryRemove(connectionId, out _);

                var wentOffline = RemoveConnectionFromRoomCore(roomId, matching.UserId, connectionId);
                return new TextRoomPresenceChange(roomId, matching.UserId, wentOffline);
            }
        }

        public IReadOnlyList<string> RemoveUser(Guid roomId, Guid userId)
        {
            lock (_sync)
            {
                if (!_rooms.TryGetValue(roomId, out var roomUsers) || !roomUsers.TryRemove(userId, out var connectionIds))
                    return Array.Empty<string>();

                if (roomUsers.IsEmpty)
                    _rooms.TryRemove(roomId, out _);

                DecrementUserRoomCount(userId);

                foreach (var connectionId in connectionIds)
                {
                    if (_connections.TryGetValue(connectionId, out var entries))
                    {
                        entries.Remove((roomId, userId));
                        if (entries.Count == 0)
                            _connections.TryRemove(connectionId, out _);
                    }
                }

                return connectionIds.ToList();
            }
        }

        public int GetOnlineCount(Guid roomId) =>
            _rooms.TryGetValue(roomId, out var roomUsers) ? roomUsers.Count : 0;

        public bool IsOnline(Guid roomId, Guid userId) =>
            _rooms.TryGetValue(roomId, out var roomUsers) && roomUsers.ContainsKey(userId);

        public int GetGlobalOnlineUserCount() => _userRoomCounts.Count;

        private bool RemoveConnectionFromRoomCore(Guid roomId, Guid userId, string connectionId)
        {
            if (!_rooms.TryGetValue(roomId, out var roomUsers)
                || !roomUsers.TryGetValue(userId, out var userConnections))
            {
                return false;
            }

            userConnections.Remove(connectionId);
            if (userConnections.Count > 0)
                return false;

            roomUsers.TryRemove(userId, out _);
            if (roomUsers.IsEmpty)
                _rooms.TryRemove(roomId, out _);

            DecrementUserRoomCount(userId);
            return true;
        }

        private void IncrementUserRoomCount(Guid userId)
        {
            _userRoomCounts[userId] = _userRoomCounts.TryGetValue(userId, out var current) ? current + 1 : 1;
        }

        private void DecrementUserRoomCount(Guid userId)
        {
            if (!_userRoomCounts.TryGetValue(userId, out var current))
                return;

            if (current <= 1)
                _userRoomCounts.TryRemove(userId, out _);
            else
                _userRoomCounts[userId] = current - 1;
        }
    }
}
