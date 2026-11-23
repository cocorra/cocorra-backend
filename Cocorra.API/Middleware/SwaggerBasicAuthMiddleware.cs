using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Cocorra.API.Middleware
{
    /// <summary>
    /// Guards /swagger with HTTP Basic auth so the API surface (admin endpoints included) is not
    /// public when Swagger is enabled outside Development. API calls made from the Swagger UI go to
    /// /api/... and still use the Bearer token entered in "Authorize"; this only covers the docs.
    /// </summary>
    public class SwaggerBasicAuthMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly byte[] _username;
        private readonly byte[] _password;

        public SwaggerBasicAuthMiddleware(RequestDelegate next, string username, string password)
        {
            _next = next;
            _username = Encoding.UTF8.GetBytes(username);
            _password = Encoding.UTF8.GetBytes(password);
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!context.Request.Path.StartsWithSegments("/swagger") || IsAuthorized(context.Request))
            {
                await _next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"Cocorra API Docs\", charset=\"UTF-8\"";
            context.Response.Headers.CacheControl = "no-store";
        }

        private bool IsAuthorized(HttpRequest request)
        {
            if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header)
                || !"Basic".Equals(header.Scheme, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(header.Parameter))
            {
                return false;
            }

            string decoded;
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
            }
            catch (FormatException)
            {
                return false;
            }

            var separator = decoded.IndexOf(':');
            if (separator < 0)
            {
                return false;
            }

            // Evaluate both comparisons so a wrong username takes as long as a wrong password.
            var userOk = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(decoded[..separator]), _username);
            var passOk = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(decoded[(separator + 1)..]), _password);
            return userOk & passOk;
        }
    }
}
