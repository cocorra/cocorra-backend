using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.DAL.DTOS.TextRoomDto;

namespace Cocorra.BLL.Services.TextRooms
{
    /// <summary>
    /// Dashboard management. Mutations require <c>actor.IsAdmin</c>, or a Moderator while
    /// <c>TextRoomGlobalSettings.AllowModeratorsToManageRooms</c> is true (Forbidden otherwise).
    /// Global settings are Admin-only regardless. Every successful mutation records one audit row
    /// (AuditCategories.TextRoom) and, for room changes, broadcasts TextRoomConfigUpdated to the
    /// room and its overflow instances. Validation failures return BadRequest naming the field.
    /// </summary>
    public interface ITextRoomAdminService
    {
        Task<Response<TextRoomOverviewDto>> GetOverviewAsync(StaffActor actor);

        Task<Response<TextRoomStaffViewDto>> GetRoomAsync(Guid roomId);

        /// <summary>Audit: TextRoomCreated.</summary>
        Task<Response<TextRoomStaffViewDto>> CreateRoomAsync(TextRoomUpsertDto dto, StaffActor actor);

        /// <summary>
        /// Applies to the whole overflow family; bumps ConfigVersion; recomputes ClosesAt from
        /// OpensAt when DurationMinutes or IsAlwaysOn changes. Conflict on a Closed room.
        /// Audit: TextRoomUpdated.
        /// </summary>
        Task<Response<TextRoomStaffViewDto>> UpdateRoomAsync(Guid roomId, TextRoomUpsertDto dto, StaffActor actor);

        /// <summary>Closes the room and its overflow family; broadcasts TextRoomClosed. Audit: TextRoomClosed.</summary>
        Task<Response<string>> CloseRoomAsync(Guid roomId, string? reason, StaffActor actor);

        Task<Response<List<TextRoomScheduleDto>>> GetSchedulesAsync();

        /// <summary>TopicPool must hold at least one topic. Audit: TextRoomScheduleChanged (ToState "Created").</summary>
        Task<Response<TextRoomScheduleDto>> CreateScheduleAsync(TextRoomScheduleUpsertDto dto, StaffActor actor);

        Task<Response<TextRoomScheduleDto>> UpdateScheduleAsync(Guid scheduleId, TextRoomScheduleUpsertDto dto, StaffActor actor);

        /// <summary>Rooms already spawned stay open.</summary>
        Task<Response<string>> DeleteScheduleAsync(Guid scheduleId, StaffActor actor);

        Task<Response<TextRoomScheduleDto>> SetScheduleEnabledAsync(Guid scheduleId, bool isEnabled, StaffActor actor);

        Task<Response<TextRoomGlobalSettingsDto>> GetGlobalSettingsAsync();

        /// <summary>Admin only (Forbidden for anyone else). Invalidates the settings cache. Audit: TextRoomSettingsChanged.</summary>
        Task<Response<TextRoomGlobalSettingsDto>> UpdateGlobalSettingsAsync(TextRoomGlobalSettingsDto dto, StaffActor actor);

        /// <summary>
        /// Keys: "always-on-open", "flash-1h", "lounge-2h", "daily-12h". Fixed server-side so the
        /// dashboard and any future client share one definition.
        /// </summary>
        IReadOnlyList<TextRoomPresetDto> GetPresets();
    }
}
