using Microsoft.Extensions.DependencyInjection;

namespace Cocorra.BLL.Services.ContentModeration
{
    public static class ContentModerationServiceCollectionExtensions
    {
        public static IServiceCollection AddContentModeration(this IServiceCollection services)
        {
            services.AddScoped<IWordFilter, WordFilter>();
            services.AddScoped<IWordDictionaryService, WordDictionaryService>();
            services.AddScoped<ITextRoomReportService, TextRoomReportService>();
            return services;
        }

        public static IServiceCollection AddContentModerationBackgroundJobs(this IServiceCollection services)
        {
            services.AddHostedService<ReportContextCompletionHostedService>();
            return services;
        }
    }
}
