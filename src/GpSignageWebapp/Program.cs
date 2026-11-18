using GpSignageWebapp.Injectors;
using GpSignageWebapp.Hubs;
using GpSignageWebapp.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// Discover and run all IInjector implementations (e.g. DbInjector, SignalRInjector).
builder.Services.InjectServicesFromAssemblies(builder.Configuration);
var app = builder.Build();

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

app.MapGet("/", () => "Hello World!");

// Minimal API endpoints (≈ controllers), organized per resource.
app.MapDisplayEndpoints();
app.MapSlideEndpoints();

// SignalR hub the player(s) connect to: /hubs/signage
app.MapHub<SignageHub>("/hubs/signage");

app.Run();

