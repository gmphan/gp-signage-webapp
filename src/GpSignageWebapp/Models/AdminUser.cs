namespace GpSignageWebapp.Models;

/// <summary>
/// An admin user who can sign in to the admin UI and edit displays / slides.
/// Passwords are stored as a salted PBKDF2 hash (see PasswordHasher), never plaintext.
/// </summary>
public class AdminUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Login handle (unique, case-insensitive).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Friendly name shown in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>PBKDF2 hash encoded as "iterations.saltB64.hashB64". See PasswordHasher.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginUtc { get; set; }
}
