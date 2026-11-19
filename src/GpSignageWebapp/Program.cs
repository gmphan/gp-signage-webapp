using GpSignageWebapp.Auth;
using GpSignageWebapp.Data.Repositories;
using GpSignageWebapp.Injectors;
using GpSignageWebapp.Hubs;
using GpSignageWebapp.Endpoints;
using GpSignageWebapp.Models;

var builder = WebApplication.CreateBuilder(args);

// Discover and run all IInjector implementations (e.g. DbInjector, SignalRInjector).
builder.Services.InjectServicesFromAssemblies(builder.Configuration);
var app = builder.Build();

// Authentication/authorization middleware (cookie auth via AuthInjector).
app.UseAuthentication();
app.UseAuthorization();

// Gate the admin SPA: the admin pages are served as static files, so without this a
// static-file handler would return them regardless of auth. Redirect to login when the
// visitor is not signed in. (The API protects its own endpoints via RequireAuthorization.)
app.Use(async (context, next) =>
{
    // Gate only the admin PAGE (and its index), not its static assets (admin.js),
    // which the browser loads after the page is already served to a signed-in user.
    var path = context.Request.Path.Value ?? string.Empty;
    var isAdminPage = (path == "/admin" || path == "/admin/" || path == "/admin/index.html");

    if (isAdminPage && context.User?.Identity?.IsAuthenticated != true)
    {
        context.Response.Redirect(AuthConstants.LoginPath);
        return;
    }
    await next();
});

// Serve static files from wwwroot (player page, vendored SignalR JS, uploads, etc.)
app.UseDefaultFiles();
app.UseStaticFiles();

// If uploads are configured to a path outside wwwroot (e.g. a mounted volume),
// also expose that directory at /uploads.
var uploadsPath = app.Configuration["Storage:UploadsPath"];
if (!string.IsNullOrWhiteSpace(uploadsPath))
{
    var physicalPath = Path.IsPathRooted(uploadsPath)
        ? uploadsPath
        : Path.Combine(app.Environment.ContentRootPath, uploadsPath);
    Directory.CreateDirectory(physicalPath);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(physicalPath),
        RequestPath = "/uploads",
    });
}

app.MapGet("/", () => "GP Signage is running.");

// Minimal API endpoints (≈ controllers), organized per resource.
app.MapDisplayEndpoints();
app.MapSlideEndpoints();
app.MapAuthEndpoints();
app.MapUserEndpoints();

// SignalR hub the player(s) connect to: /hubs/signage
app.MapHub<SignageHub>("/hubs/signage");

// Ensure at least one admin account exists so the site is usable on first run.
await BootstrapAdminAsync(app);

app.Run();

/// <summary>
/// On startup, if there are no admin users, create one from configuration:
///   Auth:InitialAdmin:Username (default "admin")
///   Auth:InitialAdmin:Password (env: Auth__InitialAdmin__Password)
///   Auth:InitialAdmin:DisplayName (optional)
/// If no password is configured, a random one is generated and logged ONCE so you can sign in.
/// </summary>
static async Task BootstrapAdminAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var users = scope.ServiceProvider.GetRequiredService<IAdminUserRepository>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("AdminBootstrap");

    try
    {
        if (await users.AnyAsync())
        {
            return;
        }

        var config = app.Configuration;
        var username = (config["Auth:InitialAdmin:Username"] ?? "admin").Trim().ToLowerInvariant();
        var displayName = config["Auth:InitialAdmin:DisplayName"];
        var password = config["Auth:InitialAdmin:Password"];

        var generated = false;
        if (string.IsNullOrWhiteSpace(password))
        {
            // Generate a readable random password and log it once.
            password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
            generated = true;
        }

        var admin = new AdminUser
        {
            Username = string.IsNullOrWhiteSpace(username) ? "admin" : username,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Administrator" : displayName.Trim(),
            PasswordHash = PasswordHasher.Hash(password),
            CreatedAtUtc = DateTime.UtcNow,
        };

        await users.AddAsync(admin);
        await users.SaveChangesAsync();

        if (generated)
        {
            logger.LogWarning(
                "Created initial admin '{User}' with a GENERATED password: {Password}  " +
                "Sign in and change it. Set Auth__InitialAdmin__Password to control this on first run.",
                admin.Username, password);
        }
        else
        {
            logger.LogInformation("Created initial admin '{User}' from configuration.", admin.Username);
        }
    }
    catch (Exception ex)
    {
        // Don't crash the app if the DB isn't reachable yet; admin bootstrap can run next start.
        logger.LogWarning(ex, "Admin bootstrap skipped (database not ready?).");
    }
}
