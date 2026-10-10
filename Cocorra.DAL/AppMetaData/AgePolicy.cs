namespace Cocorra.DAL.AppMetaData
{
    /// <summary>
    /// Cocorra is an adults-only (18+) platform. Every DTO that accepts an age validates
    /// against these bounds so sign-up, profile edits and admin-created accounts agree.
    /// </summary>
    public static class AgePolicy
    {
        public const int MinimumAge = 18;
        public const int MaximumAge = 120;
        public const string RangeErrorMessage = "You must be at least 18 years old to use Cocorra.";
    }
}
