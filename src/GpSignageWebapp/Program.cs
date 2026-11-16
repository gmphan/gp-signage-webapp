using GpSignageWebapp.Injectors;

var builder = WebApplication.CreateBuilder(args);

// Discover and run all IInjector implementations (e.g. DbInjector).
builder.Services.InjectServicesFromAssemblies(builder.Configuration);
var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.Run();


