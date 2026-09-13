using System;
using System.Threading;
using System.Threading.Tasks;

namespace EasyFin_Tech.Server.Services;

public record StatementProcessingJob(
    Guid JobId,
    Guid FileRecordId,
    Guid UserId,
    string? Password = null);

public class JobRuntimeInfo
{
    public Guid JobId { get; set; }

    public Guid UserId { get; set; }

    public string Stage { get; set; } = "Queued";

    public int Progress { get; set; } = 10;

    public CancellationTokenSource? Cts { get; set; }
}

public interface IStatementProcessingQueue
{
    ValueTask<bool> QueueJobAsync(Guid fileRecordId, Guid currentUserId, string? password = null, CancellationToken cancellationToken = default);

    ValueTask<StatementProcessingJob> DequeueJobAsync(CancellationToken cancellationToken);

    bool RequestCancellation(Guid jobId, Guid currentUserId);

    JobRuntimeInfo? GetRuntimeInfo(Guid jobId);

    void UpdateStage(Guid jobId, string stage, int progress);

    void RegisterActiveCts(Guid jobId, CancellationTokenSource cts);

    void CompleteJob(Guid jobId);
}
