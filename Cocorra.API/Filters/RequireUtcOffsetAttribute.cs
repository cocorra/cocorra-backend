using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cocorra.API.Filters
{
    /// <summary>
    /// Rejects DateTime arguments that carry no offset. "2026-11-11T22:00:00" could mean UTC or
    /// Cairo time, two hours apart, and guessing either silently shifts a range filter. Callers
    /// send "Z" or an explicit offset; the model binder then converts the value to UTC.
    /// Actions whose parameters are calendar dates opt out with <see cref="CalendarDatesAttribute"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class RequireUtcOffsetAttribute : ActionFilterAttribute
    {
        public override void OnActionExecuting(ActionExecutingContext context)
        {
            if (context.ActionDescriptor.EndpointMetadata.OfType<CalendarDatesAttribute>().Any())
                return;

            var offsetless = context.ActionArguments
                .Where(argument => argument.Value is DateTime { Kind: DateTimeKind.Unspecified })
                .Select(argument => argument.Key)
                .ToList();

            if (offsetless.Count == 0)
                return;

            context.Result = new BadRequestObjectResult(new
            {
                succeeded = false,
                statusCode = 400,
                message = $"{string.Join(", ", offsetless)} must include a time zone offset, " +
                          "for example 2026-11-11T22:00:00Z or 2026-11-12T00:00:00+02:00."
            });
        }
    }

    /// <summary>Marks an action whose DateTime parameters are Cairo calendar dates, not instants.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class CalendarDatesAttribute : Attribute
    {
    }
}
