using Cocorra.DAL.DTOS.RoomInviteDto;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.GenericRepository;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Cocorra.DAL.Repository.RoomInviteRepository
{
    public interface IRoomInviteRepository : IGenericRepositoryAsync<RoomInvite>
    {
        /// <summary>Untracked. Null when no invite has this code.</summary>
        Task<RoomInvite?> GetByCodeAsync(string inviteCode);

        /// <summary>Invites by this inviter for this room that are Active and not yet past ExpiresAt.</summary>
        Task<int> CountUsableAsync(Guid inviterUserId, Guid roomId, DateTime nowUtc);

        /// <summary>
        /// Inserts the invite with a code from <paramref name="generateCode"/>, drawing a fresh
        /// code and retrying (up to 3 attempts in total) if the unique code index is tripped.
        /// </summary>
        Task<RoomInvite> AddWithUniqueCodeAsync(RoomInvite invite, Func<string> generateCode);

        /// <summary>
        /// Runs <paramref name="work"/> inside a database transaction under the context's
        /// execution strategy, committing only when it returns Commit = true. On rollback the
        /// change tracker is cleared so nothing the work loaded outlives the undone writes.
        /// </summary>
        Task<T> ExecuteInTransactionAsync<T>(Func<Task<(bool Commit, T Result)>> work);

        /// <summary>
        /// Atomically flips the invite Active→Used for <paramref name="userId"/>, provided it is
        /// still Active and unexpired. False means someone else got there first (or it expired).
        /// Call inside <see cref="ExecuteInTransactionAsync{T}"/> so the row lock is held
        /// until the join that justifies the consumption has been saved.
        /// </summary>
        Task<bool> TryConsumeAsync(Guid inviteId, Guid userId, DateTime nowUtc);

        /// <summary>Atomically flips the invite Active→Revoked. False if it was no longer Active.</summary>
        Task<bool> TryRevokeAsync(Guid inviteId, Guid revokedByUserId, DateTime nowUtc);

        /// <summary>
        /// Lifecycle counts (TotalCreated … UniqueAcceptedUsers) over invites created in
        /// [from, to], applying the computed-expiry rule. Event count fields are left unset.
        /// </summary>
        Task<RoomInviteStatsDto> GetLifecycleStatsAsync(DateTime? fromUtc, DateTime? toUtc, Guid? roomId, Guid? inviterUserId, DateTime nowUtc);

        /// <summary>Event type → number of analytics events in [from, to], optionally for one room.</summary>
        Task<Dictionary<string, long>> CountEventsAsync(IReadOnlyCollection<string> eventTypes, DateTime? fromUtc, DateTime? toUtc, Guid? roomId);
    }
}
