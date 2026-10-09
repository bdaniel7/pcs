using System.Net;
using Microsoft.EntityFrameworkCore;
using PlexCompatibleServer.Api;
using PlexCompatibleServer.Api.Controllers;
using PlexCompatibleServer.Api.Hosted;
using PlexCompatibleServer.Api.Options;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Infrastructure.Data;
using PlexCompatibleServer.Infrastructure.Media;
using PlexCompatibleServer.Infrastructure.Scanning;

var builder = WebApplication.CreateBuilder(args);

// EF emits every SELECT at Information under the Database.Command category, which buries the
// REQ/RES lines that actually matter when tracing what a TV client requests. DbContextOptions.LogTo
// does not suppress these; they come from the standard logger, so the category has to be filtered here.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Infrastructure", LogLevel.Warning);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Any, 32400);
});

builder.Services.Configure<ServerOptions>(
    builder.Configuration.GetSection("Server"));
builder.Services.Configure<MediaOptions>(
    builder.Configuration.GetSection("Media"));

var mediaArt = builder.Configuration.GetSection("Media:Art").Get<MediaArtOptions>()
    ?? new MediaArtOptions();

// One generator for the whole process: it resolves ffmpeg once and rate-limits extraction,
// so a rescan of a large library cannot spawn a ffmpeg process per file at once.
builder.Services.AddSingleton(mediaArt);
builder.Services.AddSingleton<PosterGenerator>();
builder.Services.AddSingleton<RemoteArtworkCache>();
builder.Services.AddSingleton<ImageTranscoder>();
// Sidecar and lookup-cache files live in the web root of the content root (project dir in dev,
// app dir when published) - never under the output directory's wwwroot.
builder.Services.AddSingleton<IMetadataService>(
    new ExternalMetadata(contentRoot: builder.Environment.ContentRootPath));
// RemoteArtworkCache fetches through IHttpClientFactory instead of owning an HttpClient.
builder.Services.AddHttpClient();

var connectionString = builder.Configuration.GetConnectionString("Media")
    ?? "Data Source=media.db";

builder.Services.AddDbContextFactory<MediaDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddScoped<IMediaRepository, MediaRepository>();
builder.Services.AddScoped<IMediaScanner, FileSystemMediaScanner>();
builder.Services.AddScoped<IPlaybackService, PlaybackService>();

var serverOptions = builder.Configuration.GetSection("Server").Get<ServerOptions>()
    ?? new ServerOptions();

builder.Services.AddSingleton(serverOptions);
builder.Services.AddSingleton<MediaScanTrigger>();
builder.Services.AddSingleton<MetadataSyncTrigger>();
builder.Services.AddSingleton<MetadataSyncService>();
builder.Services.AddSingleton<PlaybackState>();
// The viewer's audio/subtitle pick: written by PUT /library/parts/{id} and read back by every
// response that publishes a stream list, so it has to outlive a single request.
builder.Services.AddSingleton<StreamSelectionStore>();
builder.Services.AddHostedService<MediaScanHostedService>();
builder.Services.AddHostedService<MetadataSyncHostedService>();
builder.Services.AddControllers();

// CORS only matters to browser callers (native clients ignore it and the built-in /web UI is
// same-origin). An unauthenticated POC gains nothing from locking it down, so the default allows
// any origin; restrict via Cors:AllowedOrigins when the deployment warrants it.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "*" };

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length == 0 || allowedOrigins.Contains("*"))
            policy.AllowAnyOrigin();
        else
            policy.WithOrigins(allowedOrigins);

        policy
            .AllowAnyMethod()
            .AllowAnyHeader()
            .WithExposedHeaders("X-Plex-Protocol", "X-Plex-Platform", "X-Plex-Product",
                "X-Plex-Version", "X-Plex-Device", "X-Plex-Client-Identifier",
                "X-Plex-Machine-Identifier", "X-Plex-Server-Identifier");
    });
});

var app = builder.Build();

app.UseCors();

// Browsable UI at /web/. The root path stays the Plex server-info response because
// Plex clients read it during discovery.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MediaDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    await db.EnsureColumnsAsync();
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Plex-Protocol"] = "1.0";
    context.Response.Headers["X-Plex-Platform"] = "Windows";
    context.Response.Headers["X-Plex-Product"] = "PlexCompatibleServer";
    context.Response.Headers["X-Plex-Version"] = serverOptions.Version;
    context.Response.Headers["X-Plex-Device"] = "Server";
    // Official Plex sends these three on every response and advertises exactly them in
    // Access-Control-Expose-Headers. Advertising a header the server never sets is worse than not
    // advertising it, so they have to be present for the CORS declaration to be truthful.
    // The client identifier is echoed back from the request when the client sends one.
    var clientIdentifier = context.Request.Headers["X-Plex-Client-Identifier"].FirstOrDefault();
    // The client only sends its token on /identity; remember it for plex.tv metadata lookups.
    var requestToken = context.Request.Query["X-Plex-Token"].FirstOrDefault()
        ?? context.Request.Headers["X-Plex-Token"].FirstOrDefault();
    context.RequestServices.GetRequiredService<IMetadataService>().CaptureToken(requestToken);
    context.Response.Headers["X-Plex-Client-Identifier"] =
        string.IsNullOrEmpty(clientIdentifier) ? serverOptions.MachineIdentifier : clientIdentifier;
    context.Response.Headers["X-Plex-Machine-Identifier"] = serverOptions.MachineIdentifier;
    context.Response.Headers["X-Plex-Server-Identifier"] = serverOptions.MachineIdentifier;
    await next();
});

if (app.Configuration.GetValue<bool>("Diagnostics:LogRequests"))
{
    app.Use(async (context, next) =>
    {
        var remote = context.Connection.RemoteIpAddress?.ToString() ?? "?";
        var accept = context.Request.Headers.Accept.ToString();

        // The token never reaches the log: RequestLog strips it from both the request line and the
        // identity dump, whether it travelled as a header or a query parameter.
        var target = RequestLog.DescribeTarget(context);

        app.Logger.LogInformation(
            "REQ {Method} {Target} from={Remote} accept={Accept}",
            context.Request.Method, target, remote, accept);

        // Plex clients identify themselves with X-Plex-* headers, and the auth token may be a query
        // parameter rather than a header. Log both so an unexpected client is recognisable.
        var described = RequestLog.DescribeIdentity(context);

        if (described.Length > 0) app.Logger.LogInformation("  IDENT {Ident}", described);

        await next();

        app.Logger.LogInformation("RES {Status} {Target} from={Remote}", context.Response.StatusCode, target, remote);
    });
}

// Plex TV clients preflight with OPTIONS; answer before routing so no action returns 405.
app.Use(async (context, next) =>
{
    if (HttpMethods.IsOptions(context.Request.Method))
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        return;
    }
    await next();
});

app.MapControllers();

app.Run();
