using System.Security.Claims;
using GpSignageWebapp.Auth;
using GpSignageWebapp.Data.Repositories;
using GpSignageWebapp.Dtos;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace GpSignageWebapp.Endpoints;

/// <summary>
/// Authentication endpoints:
///   POST /api/auth/login    — verify credentials, set auth cookie
///   POST /api/auth/logout   — clear auth cookie
///   GET  /api/auth/me       — the current signed-in admin (401 if not signed in)
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapPost("/logout", LogoutAsync).AllowAnonymous();
        group.MapGet("/me", MeAsync).RequireAuthorization();
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext http,
        IAdminUserRepository users,
        CancellationToken ct)
    {
        var username = (request.Username ?? string.Empty).Trim().ToLowerInvariant();
        var password = request.Password ?? string.Empty;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return Results.BadRequest(new { error = "Username and password are required." });
        }

        var user = await users.GetByUsernameAsync(username, ct);

        // Verify even when the user is missing is not necessary here (no user enumeration
        // concern beyond a generic message), but always return the same generic error.
        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            return Results.Json(new { error = "Invalid username or password." }, statusCode: 401);
        }

        // Record last login (best-effort).
        user.LastLoginUtc = DateTime.UtcNow;
        users.Update(user);
        await users.SaveChangesAsync(ct);

        var identity = new ClaimsIdentity(AuthConstants.Scheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.Username));
        identity.AddClaim(new Claim("display_name", user.DisplayName));

        await http.SignInAsync(AuthConstants.Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12),
        });

        return Results.Ok(AdminUserDto.From(user));
    }

    private static async Task<IResult> LogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(AuthConstants.Scheme);
        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(
        HttpContext http,
        IAdminUserRepository users,
        CancellationToken ct)
    {
        var idValue = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idValue, out var id))
        {
            return Results.Unauthorized();
        }

        var user = await users.GetByIdAsync(id, ct);
        return user is null ? Results.Unauthorized() : Results.Ok(AdminUserDto.From(user));
    }
}
