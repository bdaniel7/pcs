namespace PlexCompatibleServer.Api.Hosted;

/// <summary>
/// Consumes metadata-sync requests - one fires after every completed media scan - and runs the
/// plex.tv backfill in the background so scans never wait on network lookups. Queued requests
/// that pile up during a run are harmless: the next run finds those items already present and
/// exits without network traffic.
/// </summary>
public sealed class MetadataSyncHostedService : BackgroundService
{
    private readonly MetadataSyncTrigger trigger;
    private readonly MetadataSyncService sync;
    private readonly ILogger<MetadataSyncHostedService> logger;

    public MetadataSyncHostedService(
        MetadataSyncTrigger trigger,
        MetadataSyncService sync,
        ILogger<MetadataSyncHostedService> logger)
    {
        this.trigger = trigger;
        this.sync = sync;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var reason in trigger.Requests.ReadAllAsync(stoppingToken))
        {
            try
            {
                await sync.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Metadata sync failed ({Reason}).", reason);
            }
        }
    }
}
