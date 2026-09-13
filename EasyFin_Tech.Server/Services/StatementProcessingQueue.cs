using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EasyFin_Tech.Server.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EasyFin_Tech.Server.Services;

public class StatementProcessingQueue : IStatementProcessingQueue
{
    private readonly Channel<StatementProcessingJob> _channel;
    private readonly ConcurrentDictionary<Guid, JobRuntimeInfo> _runtimeInfos = new();
    private readonly ILogger<StatementProcessingQueue> _logger;

    public StatementProcessingQueue(
        IOptions<BackgroundProcessingOptions> options,
        ILogger<StatementProcessingQueue> logger)
    {
        _logger = logger;
        var capacity = options.Value.QueueCapacity > 0 ? options.Value.QueueCapacity : 100;
        var channelOptions = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };
        _channel = Channel.CreateBounded<StatementProcessingJob>(channelOptions);
    }

    public async ValueTask<bool> QueueJobAsync(
        Guid fileRecordId,
        Guid currentUserId,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        var job = new StatementProcessingJob(fileRecordId, fileRecordId, currentUserId, password);

        var runtimeInfo = _runtimeInfos.GetOrAdd(fileRecordId, id => new JobRuntimeInfo
        {
            JobId = id,
            UserId = currentUserId,
            Stage = "Queued",
            Progress = 10
        });

        runtimeInfo.Stage = "Queued";
        runtimeInfo.Progress = 10;
        runtimeInfo.UserId = currentUserId;

        try
        {
            await _channel.Writer.WriteAsync(job, cancellationToken);
            _logger.LogInformation("Enqueued statement processing job {JobId} for user {UserId}", fileRecordId, currentUserId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue processing job {JobId} for user {UserId}", fileRecordId, currentUserId);
            return false;
        }
    }

    public ValueTask<StatementProcessingJob> DequeueJobAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAsync(cancellationToken);
    }

    public bool RequestCancellation(Guid jobId, Guid currentUserId)
    {
        if (_runtimeInfos.TryGetValue(jobId, out var info))
        {
            if (info.UserId != currentUserId)
            {
                _logger.LogWarning("Security violation: User {UserId} attempted to cancel unauthorized job {JobId}", currentUserId, jobId);
                return false;
            }

            if (info.Cts != null && !info.Cts.IsCancellationRequested)
            {
                info.Cts.Cancel();
                info.Stage = "Cancelled";
                info.Progress = 0;
                _logger.LogInformation("Cancelled active processing job {JobId} for user {UserId}", jobId, currentUserId);
                return true;
            }
        }

        return false;
    }

    public JobRuntimeInfo? GetRuntimeInfo(Guid jobId)
    {
        _runtimeInfos.TryGetValue(jobId, out var info);
        return info;
    }

    public void UpdateStage(Guid jobId, string stage, int progress)
    {
        if (_runtimeInfos.TryGetValue(jobId, out var info))
        {
            info.Stage = stage;
            info.Progress = progress;
        }
    }

    public void RegisterActiveCts(Guid jobId, CancellationTokenSource cts)
    {
        if (_runtimeInfos.TryGetValue(jobId, out var info))
        {
            info.Cts = cts;
        }
    }

    public void CompleteJob(Guid jobId)
    {
        if (_runtimeInfos.TryGetValue(jobId, out var info))
        {
            info.Cts?.Dispose();
            info.Cts = null;
        }
    }
}
