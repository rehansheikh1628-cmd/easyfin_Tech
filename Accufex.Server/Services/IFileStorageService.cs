using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Accufex.Server.Services;

public interface IFileStorageService
{
    Task<string> SaveFileAsync(Stream stream, string subDirectory, string safeFileName, CancellationToken cancellationToken = default);

    Task<Stream?> GetFileStreamAsync(string subDirectory, string safeFileName, CancellationToken cancellationToken = default);

    Task<bool> DeleteFileAsync(string subDirectory, string safeFileName, CancellationToken cancellationToken = default);

    Task<bool> FileExistsAsync(string subDirectory, string safeFileName, CancellationToken cancellationToken = default);

    Task<string> ComputeSha256HashAsync(Stream stream, CancellationToken cancellationToken = default);
}
