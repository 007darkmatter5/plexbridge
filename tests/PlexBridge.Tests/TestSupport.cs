using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlexBridge.Data;
using PlexBridge.Plex;
using PlexBridge.Security;
using PlexBridge.Services;

namespace PlexBridge.Tests;

public sealed class ScriptHandler : HttpMessageHandler
{
    public List<CapturedRequest> Requests { get; } = [];
    public Func<CapturedRequest, HttpResponseMessage>? Responder { get; set; }
    public Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>>? AsyncResponder { get; set; }
    public Exception? Throw { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);
        var token = request.Headers.TryGetValues("X-Plex-Token", out var values) ? string.Join(",", values) : null;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var captured = new CapturedRequest(request.Method, request.RequestUri ?? throw new InvalidOperationException("Missing URI."), token, body);
        Requests.Add(captured);
        if (Throw is not null)
            throw Throw;
        if (AsyncResponder is not null)
            return await AsyncResponder(captured, cancellationToken);
        return Responder?.Invoke(captured) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

public sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Token, string? Body = null);

public sealed class AuthTransport(HttpMessageHandler handler) : IPlexAuthTransport
{
    private readonly HttpClient _client = new(handler, disposeHandler: false) { Timeout = PlexHttp.RequestTimeout };
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => _client.SendAsync(request, cancellationToken);
}

public sealed class HandlerTransport(HttpMessageHandler handler) : IPlexTransport
{
    private readonly HttpClient _client = new(handler, disposeHandler: false) { Timeout = PlexHttp.RequestTimeout };

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => _client.SendAsync(request, cancellationToken);
}

public sealed class SpyLoggerProvider : ILoggerProvider
{
    public SpyLogger Logger { get; } = new();
    public ILogger CreateLogger(string categoryName) => Logger;
    public void Dispose() { }
}

public sealed class SpyLogger : ILogger
{
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var text = formatter(state, exception);
        if (exception is not null)
            text += " " + exception;
        Messages.Add(text);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

public sealed class ServiceFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "plexbridge-unit", Guid.NewGuid().ToString("n"));
    public string DatabasePath { get; }
    public ScriptHandler Handler { get; } = new();
    public SpyLoggerProvider Logs { get; } = new();
    public ServiceProvider Provider { get; }

    public ServiceFixture()
    {
        Directory.CreateDirectory(Root);
        var keys = Path.Combine(Root, "keys");
        Directory.CreateDirectory(keys);
        DatabasePath = Path.Combine(Root, "plexbridge.db");
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys)).SetApplicationName("PlexBridgeTests");
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite($"Data Source={DatabasePath}"));
        services.AddSingleton<IPlexTransport>(new HandlerTransport(Handler));
        services.AddSingleton<IPlexAddressGuard, AllowAllAddressGuard>();
        services.AddSingleton<PlexReadClient>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<PlexServerService>();
        Provider = services.BuildServiceProvider();
        using var scope = Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.Migrate();
    }

    public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

    public void AssertNoSecret(string secret)
    {
        Assert.DoesNotContain(Logs.Logger.Messages, message => message.Contains(secret, StringComparison.Ordinal));
        using var scope = Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE);");
        foreach (var file in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
        {
            var stored = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            Assert.DoesNotContain(secret, stored);
        }
    }

    public void Dispose()
    {
        Provider.Dispose();
        try { Directory.Delete(Root, true); } catch { /* the assertion already read the file */ }
    }
}

public static class HttpJson
{
    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
