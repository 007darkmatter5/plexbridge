using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using PlexBridge.Components;
using PlexBridge.Components.Account;
using PlexBridge.Data;
using PlexBridge.Logging;
using PlexBridge.Plex;
using PlexBridge.Security;
using PlexBridge.Services;

var builder = WebApplication.CreateBuilder(args);

// ASPNETCORE_FORWARDEDHEADERS_ENABLED=true is handled by the framework (KnownProxies cleared).
// Authentication middleware is added automatically when the identity services are registered.

// Request logs include the query string. Plex tokens are never put on a query string, and these
// categories stay at Warning so a future mistake is less likely to land in the log.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

builder.Services.AddMudServices();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(options =>
{
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
    options.DisconnectedCircuitMaxRetained = 10;
});

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.SlidingExpiration = true;
});

var connectionString = ResolveConnectionString(builder.Configuration, builder.Environment.ContentRootPath);
var logDirectory = ResolveLogDirectory(builder.Configuration, builder.Environment.ContentRootPath, connectionString);
Directory.CreateDirectory(logDirectory);
builder.Logging.AddProvider(new RollingFileLoggerProvider(Path.Combine(logDirectory, "plexbridge.log")));
var databasePath = new SqliteConnectionStringBuilder(connectionString).DataSource;
if (!string.IsNullOrEmpty(databasePath) && databasePath != ":memory:" && Path.GetDirectoryName(databasePath) is { Length: > 0 } databaseFolder)
    Directory.CreateDirectory(databaseFolder);

builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString, sqlite => sqlite.CommandTimeout(30)));

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 8;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.Configure<SecurityStampValidatorOptions>(options =>
    options.ValidationInterval = TimeSpan.FromMinutes(1));

// Keys encrypt Plex tokens and sign the login cookie. Containers set DataProtection:KeysPath to /config/keys.
var keysPath = ResolveKeysPath(builder.Configuration, builder.Environment.ContentRootPath);
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection()
    .SetApplicationName("PlexBridge")
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddHttpClient(PlexHttp.ClientName, client => client.Timeout = PlexHttp.RequestTimeout)
    .ConfigurePrimaryHttpMessageHandler(PlexHttp.CreateHandler);
builder.Services.AddHttpClient(PlexHttp.WriteClientName, client => client.Timeout = PlexHttp.RequestTimeout)
    .ConfigurePrimaryHttpMessageHandler(PlexHttp.CreateHandler);
builder.Services.AddSingleton(ReadPreviewOptions(builder.Configuration));
builder.Services.AddHttpClient(PlexHttp.AuthClientName, client => client.Timeout = PlexHttp.RequestTimeout)
    .ConfigurePrimaryHttpMessageHandler(PlexHttp.CreateHandler);
builder.Services.AddSingleton(new PlexProbeLimits());
#if DEBUG
if (builder.Environment.IsDevelopment() && string.Equals(builder.Configuration["PlexBridge:FakePlex"], "true", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IPlexTransport, PreviewPlexMediaTransport>();
    builder.Services.AddSingleton<IPlexWriteTransport>(sp => new ForwardingPlexWriteTransport(sp.GetRequiredService<IPlexTransport>()));
    builder.Services.AddSingleton<IPlexAuthTransport, PreviewPlexAuthTransport>();
    builder.Services.AddSingleton<IPlexAddressGuard, AllowAllAddressGuard>();
    builder.Services.AddSingleton(new PlexProbeLimits { Limit = TimeSpan.FromMilliseconds(1200) });
}
else
#endif
{
    builder.Services.AddSingleton<IPlexTransport, HttpClientPlexTransport>();
    builder.Services.AddSingleton<IPlexWriteTransport, HttpClientPlexWriteTransport>();
    builder.Services.AddSingleton<IPlexAuthTransport, HttpClientPlexAuthTransport>();
    builder.Services.AddSingleton<IPlexAddressGuard, DnsPlexAddressGuard>();
}
builder.Services.AddSingleton<PlexReadClient>();
builder.Services.AddSingleton<PlexMigrationReader>();
builder.Services.AddSingleton<PlexWriteClient>();
builder.Services.AddSingleton<PlexAuthClient>();
builder.Services.AddSingleton<PlexPinCoordinator>();
builder.Services.AddScoped<PlexServerService>();
builder.Services.AddScoped<AdminAccountService>();
builder.Services.AddScoped<UserPreferences>();
builder.Services.AddScoped<IAdminGate, AuthenticationAdminGate>();
builder.Services.AddScoped<PlexAccountService>();
builder.Services.AddSingleton<MigrationUserTokens>();
builder.Services.AddScoped<MigrationPlexUsers>();
builder.Services.AddSingleton<MigrationPreviewJobs>();
builder.Services.AddScoped<MigrationService>();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database");

var app = builder.Build();

await DatabaseStartup.InitializeAsync(app.Services);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Re-execute only GET/HEAD 404s. Re-running a rejected POST (for example logout without
// an antiforgery token) through the pipeline makes the form reader throw.
app.Use(async (context, next) =>
{
    await next();
    if (context.Response.StatusCode != StatusCodes.Status404NotFound
        || context.Response.HasStarted
        || context.Items.ContainsKey("plexbridge-not-found"))
        return;
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        return;

    var originalPath = context.Request.Path;
    var originalQuery = context.Request.QueryString;
    context.Items["plexbridge-not-found"] = true;
    context.Request.Path = "/not-found";
    context.Request.QueryString = QueryString.Empty;
    context.SetEndpoint(null);
    context.Response.Clear();
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    try
    {
        await next();
    }
    finally
    {
        context.Request.Path = originalPath;
        context.Request.QueryString = originalQuery;
    }
});
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();
app.MapPost("/migrate/cancel", async (HttpContext context, MigrationPreviewJobs jobs) =>
{
    if (context.Features.Get<Microsoft.AspNetCore.Antiforgery.IAntiforgeryValidationFeature>() is not { IsValid: true })
        return Results.BadRequest();
    if (context.User.Identity?.IsAuthenticated != true || !context.User.IsInRole(Auth.AdminRole))
        return Results.Challenge();

    var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (context.Request.HasFormContentType && !string.IsNullOrEmpty(userId))
    {
        var form = await context.Request.ReadFormAsync();
        var jobId = form["jobId"].ToString();
        if (!string.IsNullOrEmpty(jobId))
            jobs.Cancel(userId, jobId);
    }

    return Results.LocalRedirect("~/migrate");
}).RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute());

// Docker, Unraid, and the release smoke test use this. The body is only Healthy or Unhealthy.
app.MapHealthChecks("/healthz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(report.Status == Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy
            ? "Healthy"
            : "Unhealthy");
    }
});

app.Run();

static string ResolveConnectionString(IConfiguration configuration, string contentRoot)
{
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=Data/plexbridge.db";
    var sqlite = new SqliteConnectionStringBuilder(connectionString);
    if (!string.IsNullOrEmpty(sqlite.DataSource) && sqlite.DataSource != ":memory:" && !Path.IsPathRooted(sqlite.DataSource))
    {
        sqlite.DataSource = Path.GetFullPath(Path.Combine(contentRoot, sqlite.DataSource));
        connectionString = sqlite.ToString();
    }

    return connectionString;
}

static MigrationPreviewOptions ReadPreviewOptions(IConfiguration configuration)
{
    var options = new MigrationPreviewOptions();
    if (int.TryParse(configuration["Migration:PreviewDeadlineMinutes"], out var minutes) && minutes is > 0 and <= 180)
        options.Deadline = TimeSpan.FromMinutes(minutes);
    if (int.TryParse(configuration["Migration:RequestTimeoutSeconds"], out var seconds) && seconds is >= 5 and <= 120)
        options.RequestTimeout = TimeSpan.FromSeconds(seconds);
    return options;
}

static string ResolveLogDirectory(IConfiguration configuration, string contentRoot, string connectionString)
{
    var configured = configuration["Logging:File:Path"];
    if (!string.IsNullOrWhiteSpace(configured))
        return Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured));

    var dataFile = new SqliteConnectionStringBuilder(connectionString).DataSource;
    var dataDir = string.IsNullOrWhiteSpace(dataFile)
        ? contentRoot
        : Path.GetDirectoryName(Path.GetFullPath(dataFile)) ?? contentRoot;
    var parent = Directory.GetParent(dataDir)?.FullName ?? dataDir;
    return Path.Combine(parent, "logs");
}

static string ResolveKeysPath(IConfiguration configuration, string contentRoot)
{
    var keysPath = configuration["DataProtection:KeysPath"];
    if (string.IsNullOrWhiteSpace(keysPath))
        keysPath = Path.Combine(contentRoot, "keys");
    else if (!Path.IsPathRooted(keysPath))
        keysPath = Path.Combine(contentRoot, keysPath);
    return Path.GetFullPath(keysPath);
}

public partial class Program;
