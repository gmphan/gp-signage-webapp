using System.Security.Claims;
using GpSignageWebapp.Auth;
using GpSignageWebapp.Data.Repositories;
using GpSignageWebapp.Dtos;
using GpSignageWebapp.Models;

namespace GpSignageWebapp.Endpoints;

/// <summary>
/// Admin user management (all endpoints require an authenticated admin):
///   GET    /api/users        — list admin users
///   POST   /api/users        — create an admin user
///   PATCH  /api/users/{id}   — change display name and/or reset password
///   DELETE /api/users/{id}   — delete an admin user (cannot delete yourself / last admin)
/// </summary>
public static class UserEndpoints
{
    private const int MinPasswordLength = 6;

    public static void MapUserEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/users").RequireAuthorization();

        group.MapGet("/", GetAllAsync);
        group.MapPost("/", CreateAsync);
        group.MapPatch("/{id:guid}", UpdateAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);
    }

    private static async Task<IResult> GetAllAsync(IAdminUserRepository users, CancellationToken ct)
    {
        var all = await users.GetAllAsync(ct);
        return Results.Ok(all.OrderBy(u => u.Username).Select(AdminUserDto.From));
    }

    private static async Task<IResult> CreateAsync(
        CreateUserRequest request,
        IAdminUserRepository users,
        CancellationToken ct)
    {
        var username = (request.Username ?? string.Empty).Trim().ToLowerInvariant();
        var password = request.Password ?? string.Empty;

        if (string.IsNullOrWhiteSpace(username))
        {
            return Results.BadRequest(new { error = "Username is required." });
        }
        if (!IsValidUsername(username))
        {
            return Results.BadRequest(new { error = "Username may only contain letters, digits, '.', '-', '_'." });
        }
        if (password.Length < MinPasswordLength)
        {
            return Results.BadRequest(new { error = $"Password must be at least {MinPasswordLength} characters." });
        }
        if (await users.GetByUsernameAsync(username, ct) is not null)
        {
            return Results.Conflict(new { error = $"Username '{username}' already exists." });
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? username
            : request.DisplayName.Trim();

        var user = new AdminUser
        {
            Username = username,
            DisplayName = displayName,
            PasswordHash = PasswordHasher.Hash(password),
            CreatedAtUtc = DateTime.UtcNow,
        };

        await users.AddAsync(user, ct);
        await users.SaveChangesAsync(ct);

        return Results.Created($"/api/users/{user.Id}", AdminUserDto.From(user));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateUserRequest request,
        IAdminUserRepository users,
        CancellationToken ct)
    {
        var user = await users.GetByIdAsync(id, ct);
        if (user is null)
        {
            return Results.NotFound(new { error = $"User '{id}' not found." });
        }

        if (request.DisplayName is not null)
        {
            var dn = request.DisplayName.Trim();
            if (string.IsNullOrWhiteSpace(dn))
            {
                return Results.BadRequest(new { error = "Display name cannot be empty." });
            }
            user.DisplayName = dn;
        }

        if (request.Password is not null)
        {
            if (request.Password.Length < MinPasswordLength)
            {
                return Results.BadRequest(new { error = $"Password must be at least {MinPasswordLength} characters." });
            }
            user.PasswordHash = PasswordHasher.Hash(request.Password);
        }

        users.Update(user);
        await users.SaveChangesAsync(ct);
        return Results.Ok(AdminUserDto.From(user));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        HttpContext http,
        IAdminUserRepository users,
        CancellationToken ct)
    {
        // Prevent an admin from deleting their own account (would lock them out mid-session).
        var currentId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(currentId, out var me) && me == id)
        {
            return Results.BadRequest(new { error = "You cannot delete your own account." });
        }

        var user = await users.GetByIdAsync(id, ct);
        if (user is null)
        {
            return Results.NotFound(new { error = $"User '{id}' not found." });
        }

        // Don't allow removing the last remaining admin.
        var all = await users.GetAllAsync(ct);
        if (all.Count <= 1)
        {
            return Results.BadRequest(new { error = "Cannot delete the last admin user." });
        }

        users.Remove(user);
        await users.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static bool IsValidUsername(string username) =>
        username.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_');
}
