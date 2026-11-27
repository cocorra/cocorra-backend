namespace Cocorra.BLL.Services.AdminService
{
    internal static class AuditReasonValidator
    {
        public const int MinLength = 3;
        public const int MaxLength = 1000;

        public static bool TryValidateRequired(string? reason, out string normalized, out string errorMessage)
        {
            var trimmed = reason?.Trim() ?? string.Empty;
            if (trimmed.Length < MinLength || trimmed.Length > MaxLength)
            {
                normalized = string.Empty;
                errorMessage = $"Reason is required and must be between {MinLength} and {MaxLength} characters.";
                return false;
            }

            normalized = trimmed;
            errorMessage = string.Empty;
            return true;
        }

        public static bool TryValidateOptional(string? reason, out string? normalized, out string errorMessage)
        {
            var trimmed = reason?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                normalized = null;
                errorMessage = string.Empty;
                return true;
            }

            if (trimmed.Length > MaxLength)
            {
                normalized = null;
                errorMessage = $"Reason must be at most {MaxLength} characters.";
                return false;
            }

            normalized = trimmed;
            errorMessage = string.Empty;
            return true;
        }
    }
}
