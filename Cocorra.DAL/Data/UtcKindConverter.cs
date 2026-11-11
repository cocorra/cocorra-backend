using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Cocorra.DAL.Data
{
    /// <summary>
    /// datetime2 stores no offset, so EF materializes every value as Unspecified. All stored
    /// instants are UTC; marking them so on read keeps ToUniversalTime, ToLocalTime and
    /// serialization from treating them as server-local time. Writes pass through unchanged.
    /// </summary>
    public sealed class UtcKindConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcKindConverter()
            : base(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc))
        {
        }
    }
}
