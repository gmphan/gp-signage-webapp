using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace GpSignageWebapp.Auth;

/// <summary>
/// Cookie-based authentication for the admin UI + management API.
///
/// - Browser pages under /admin return a 302 redirect to /login.html when not signed in.
/// - API requests (/api/*) return 401 instead of redirecting, so the JS can detect it.
/// - Only the admin/management surface is protected; the player, uploads and read-only
///   display/playlist endpoints stay public so kiosk browsers keep working.
/// </summary>
public static class AuthConstants
{
    public const string Scheme = "SignageAdmin";
    public const string LoginPath = "/login.html";
    public const string CookieName = "gp_signage_auth";
}

public static class AuthSetup
{
    public static IServiceCollection AddSignageAuth(this IServiceCollection services)
    {
        services
            .AddAuthentication(AuthConstants.Scheme)
            .AddCookie(AuthConstants.Scheme, options =>
            {
                options.Cookie.Name = AuthConstants.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                // Secure = true: the site is served over HTTPS (Cloudflare edge). Cookies are
                // only attached to the same origin, which is HTTPS in production.
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(12);
                options.SlidingExpiration = true;
                options.LoginPath = AuthConstants.LoginPath;

                options.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = ctx =>
                    {
                        // For API calls, return 401 so the front-end can react; otherwise redirect.
                        if (ctx.Request.Path.StartsWithSegments("/api"))
                        {
                            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return Task.CompletedTask;
                        }
                        ctx.Response.Redirect(ctx.RedirectUri);
                        return Task.CompletedTask;
                    },
                    OnRedirectToAccessDenied = ctx =>
                    {
                        if (ctx.Request.Path.StartsWithSegments("/api"))
                        {
                            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return Task.CompletedTask;
                        }
                        ctx.Response.Redirect(ctx.RedirectUri);
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization();
        return services;
    }
}
