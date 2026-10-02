using Microsoft.Extensions.Options;
using PlexCompatibleServer.Api.Options;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Api.Hosted;

public sealed class MediaScanHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MediaOptions _options;
    private readonly MediaScanTrigger _trigger;
    private readonly ILogger<MediaScanHostedService> _logger;

    public MediaScanHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<MediaOptions> options,
        MediaScanTrigger trigger,
        ILogger<MediaScanHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _trigger = trigger;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

        await ScanAsync(MediaScanReason.Startup, stoppingToken);

        await foreach (var reason in _trigger.Requests.ReadAllAsync(stoppingToken))
            await ScanAsync(reason, stoppingToken);
    }

    private async Task ScanAsync(MediaScanReason reason, CancellationToken stoppingToken)
    {
        try
        {
            var libraries = _options.Roots.Select(x => new MediaLibrary
            {
                Name = x.Name,
                RootPath = Path.GetFullPath(x.Path),
                Type = x.Type.Equals("show", StringComparison.OrdinalIgnoreCase)
                    ? LibraryType.Show
                    : LibraryType.Movie
            }).ToList();

            using var scope = _scopeFactory.CreateScope();
            var scanner = scope.ServiceProvider.GetRequiredService<IMediaScanner>();
            await scanner.SynchronizeAsync(libraries, stoppingToken);
            _logger.LogInformation("Media scan completed ({Reason}).", reason);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Media scan failed ({Reason}).", reason);
        }
    }
}