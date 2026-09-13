using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using EasyFin_Tech.Server.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EasyFin_Tech.Server.Services;

public class LocalFileStorageService : IFileStorageService
{
    private readonly string _absoluteStorageRoot;
    private readonly ILogger<LocalFileStorageService> _logger;

    public LocalFileStorageService(
        IOptions<FileUploadOptions> options,
        IHostEnvironment environment,
        ILogger<LocalFileStorageService> logger)
    {
        _logger = logger;
        var configRoot = options.Value.StorageRoot;

        // Resolve absolute storage root relative to content root if not rooted
        _absoluteStorageRoot = Path.IsPathRooted(configRoot)
            ? Path.GetFullPath(configRoot)
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configRoot));

        if (!Directory.Exists(_absoluteStorageRoot))
        {
            Directory.CreateDirectory(_absoluteStorageRoot);
        }
    }

    public async Task<string> SaveFileAsync(Stream stream, string subDirectory, string safeFileName, CancellationToken cancellationToken = default)
    {
        var targetDirectory = ResolveSafeDirectory(subDirectory);
        if (!Directory.Exists(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        var destinationPath = Path.Combine(targetDirectory, safeFileName);
        ValidateWithinRoot(destinationPath);

        const int maxRetries = 3;
        const int retryDelayMs = 100;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // Save using async file stream without buffering entire file in memory
                await using (var fileStream = new FileStream(
                    destinationPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true))
                {
                    if (stream.CanSeek)
                    {
                        stream.Position = 0;
                    }
                    await stream.CopyToAsync(fileStream, cancellationToken);
                    await fileStream.FlushAsync(cancellationToken);
                }

                _logger.LogInformation("Stored file {SafeFileName} successfully in controlled storage.", safeFileName);
                return destinationPath;
            }
            catch (IOException ex) when (attempt < maxRetries)
            {
                _logger.LogWarning("File {SafeFileName} is locked (attempt {Attempt}/{MaxRetries}). Retrying in {Delay}ms: {Message}",
                    safeFileName, attempt, maxRetries, retryDelayMs, ex.Message);
                await Task.Delay(retryDelayMs, cancellationToken);
            }
            catch (Exception ex)
            {
                // Clean up incomplete/corrupted file on failure or cancellation
                if (File.Exists(destinationPath))
                {
                    try
                    {
                        File.Delete(destinationPath);
                        _logger.LogWarning("Cleaned up partial file {SafeFileName} after failed/cancelled stream copy: {Message}", safeFileName, ex.Message);
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.LogError(cleanupEx, "Failed to clean up partial file {SafeFileName} after error.", safeFileName);
                    }
                }
                throw;
            }
        }

        // Should never reach here, but just in case
        throw new IOException($"Failed to save file {safeFileName} after {maxRetries} attempts.");
    }

    public async Task<Stream?> GetFileStreamAsync(string subDirectory, string safeFileName, CancellationToken cancellationToken = default)
    {
        var targetDirectory = ResolveSafeDirectory(subDirectory);
        var targetPath = Path.Combine(targetDirectory, safeFileName);
        ValidateWithinRoot(targetPath);

        if (!File.Exists(targetPath))
        {
            _logger.LogWarning("File {SafeFileName} was not found in storage.", safeFileName);
            return null;
        }

        const int maxRetries = 5;
        const int retryDelayMs = 50;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var fileStream = new FileStream(
                    targetPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 81920,
                    useAsync: true);

                return fileStream;
            }
            catch (IOException ex) when (attempt < maxRetries)
            {
                _logger.LogWarning("File {SafeFileName} is momentarily locked for read (attempt {Attempt}/{MaxRetries}). Retrying in {Delay}ms: {Message}",
                    safeFileName, attempt, maxRetries, retryDelayMs, ex.Message);
                await Task.Delay(retryDelayMs, cancellationToken);
            }
        }

        // Final attempt without catching IOException
        return new FileStream(
            targetPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            useAsync: true);
    }

    public async Task<bool> DeleteFileAsync(string subDirectory, string safeFileName, CancellationToken cancellationToken = default)
    {
        var targetDirectory = ResolveSafeDirectory(subDirectory);
        var targetPath = Path.Combine(targetDirectory, safeFileName);
        ValidateWithinRoot(targetPath);

        const int maxRetries = 5;
        const int retryDelayMs = 150;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                    _logger.LogInformation("Deleted file {SafeFileName} from storage during cleanup/rollback.", safeFileName);
                    return true;
                }
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (IOException ex) when (attempt < maxRetries)
            {
                _logger.LogWarning("File {SafeFileName} is locked for deletion (attempt {Attempt}/{MaxRetries}). Retrying in {Delay}ms: {Message}",
                    safeFileName, attempt, maxRetries, retryDelayMs, ex.Message);
                await Task.Delay(retryDelayMs, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete file {SafeFileName} during cleanup.", safeFileName);
                return false;
            }
        }

        return false;
    }

    public Task<bool> FileExistsAsync(string subDirectory, string safeFileName, CancellationToken cancellationToken = default)
    {
        var targetDirectory = ResolveSafeDirectory(subDirectory);
        var targetPath = Path.Combine(targetDirectory, safeFileName);
        ValidateWithinRoot(targetPath);

        return Task.FromResult(File.Exists(targetPath));
    }

    public async Task<string> ComputeSha256HashAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        using var sha256 = SHA256.Create();
        var hashBytes = await sha256.ComputeHashAsync(stream, cancellationToken);

        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private string ResolveSafeDirectory(string subDirectory)
    {
        // Sanitize subDirectory to prevent relative path escapes
        var sanitizedSubDir = subDirectory.Replace('/', Path.DirectorySeparatorChar)
                                          .Replace('\\', Path.DirectorySeparatorChar)
                                          .TrimStart(Path.DirectorySeparatorChar);

        var fullPath = Path.GetFullPath(Path.Combine(_absoluteStorageRoot, sanitizedSubDir));
        ValidateWithinRoot(fullPath);
        return fullPath;
    }

    private void ValidateWithinRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(_absoluteStorageRoot, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogSecurityAlert("Path traversal attempt detected. Path: outside authorized storage boundary.");
            throw new UnauthorizedAccessException("Access to the requested storage path is forbidden.");
        }
    }
}

internal static class LoggerSecurityExtensions
{
    public static void LogSecurityAlert(this ILogger logger, string message)
    {
        logger.LogWarning("SECURITY ALERT: {Message}", message);
    }
}
