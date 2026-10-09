using Microsoft.Extensions.Options;
using PlexCompatibleServer.Api.Options;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Api.Hosted;

public sealed class MediaScanHostedService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly MediaOptions options;
    private readonly MediaScanTrigger trigger;
    private readonly MetadataSyncTrigger syncTrigger;
    private readonly ILogger<MediaScanHostedService> logger;

    public MediaScanHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<MediaOptions> options,
        MediaScanTrigger trigger,
        MetadataSyncTrigger syncTrigger,
        ILogger<MediaScanHostedService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.options = options.Value;
        this.trigger = trigger;
        this.syncTrigger = syncTrigger;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

        await scanAsync(MediaScanReason.Startup, stoppingToken);

        await foreach (var reason in trigger.Requests.ReadAllAsync(stoppingToken))
            await scanAsync(reason, stoppingToken);
    }

    private async Task scanAsync(MediaScanReason reason, CancellationToken stoppingToken)
    {
        try
        {
            var libraries = options.Roots.Select(x => new MediaLibrary
            {
                Name = x.Name,
                RootPath = Path.GetFullPath(x.Path),
                Type = x.Type.Equals("show", StringComparison.OrdinalIgnoreCase)
                    ? LibraryType.Show
                    : LibraryType.Movie
            }).ToList();

            using var scope = scopeFactory.CreateScope();
            var scanner = scope.ServiceProvider.GetRequiredService<IMediaScanner>();
            await scanner.SynchronizeAsync(libraries, stoppingToken);
            logger.LogInformation("Media scan completed ({Reason}).", reason);
            // New or changed files may lack plex.tv records - hand off to the metadata agent.
            syncTrigger.Request(MetadataSyncReason.ScanCompleted);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Media scan failed ({Reason}).", reason);
        }
    }
}
