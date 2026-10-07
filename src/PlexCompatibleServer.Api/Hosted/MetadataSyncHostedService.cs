namespace PlexCompatibleServer.Api.Hosted;

/// <summary>
/// Consumes metadata-sync requests - one fires after every completed media scan - and runs the
/// plex.tv backfill in the background so scans never wait on network lookups. Queued requests
/// that pile up during a run are harmless: the next run finds those items already present and
/// exits without network traffic.
/// </summary>
public sealed class MetadataSyncHostedService : BackgroundService
{
    private readonly MetadataSyncTrigger _trigger;
    private readonly MetadataSyncService _sync;
    private readonly ILogger<MetadataSyncHostedService> _logger;

    public MetadataSyncHostedService(
        MetadataSyncTrigger trigger,
        MetadataSyncService sync,
        ILogger<MetadataSyncHostedService> logger)
    {
        _trigger = trigger;
        _sync = sync;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var reason in _trigger.Requests.ReadAllAsync(stoppingToken))
        {
            try
            {
                await _sync.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Metadata sync failed ({Reason}).", reason);
            }
        }
    }
}
