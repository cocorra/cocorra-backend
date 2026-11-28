using Microsoft.Extensions.DependencyInjection;

namespace Cocorra.BLL.Services.TextRooms
{
    public static class TextRoomServiceCollectionExtensions
    {
        public static IServiceCollection AddTextRoomEngine(this IServiceCollection services)
        {
            services.AddSingleton<ITextRoomPresenceTracker, TextRoomPresenceTracker>();
            services.AddSingleton<TextRoomFamilyLockProvider>();
            services.AddScoped<ITextRoomSettingsProvider, TextRoomSettingsProvider>();
            services.AddScoped<ITextRoomService, TextRoomService>();
            services.AddScoped<ITextRoomAdminService, TextRoomAdminService>();
            services.AddScoped<ITextRoomModerationService, TextRoomModerationService>();
            services.AddScoped<ITextRoomMaintenance, TextRoomMaintenance>();
            return services;
        }

        public static IServiceCollection AddTextRoomBackgroundJobs(this IServiceCollection services)
        {
            services.AddHostedService<TextRoomMaintenanceHostedService>();
            return services;
        }
    }
}
