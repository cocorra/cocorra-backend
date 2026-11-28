using System;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.BLL.Services.ContentModeration
{
    internal static class DbConstraintChecks
    {
        public static bool IsUniqueViolation(DbUpdateException ex)
        {
            for (var current = ex.InnerException; current is not null; current = current.InnerException)
            {
                if (current is SqlException sqlEx)
                    return sqlEx.Number is 2601 or 2627;

                // SQLite (used by unit tests) has no compile-time assembly reference in BLL.
                var message = current.Message;
                if (message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
