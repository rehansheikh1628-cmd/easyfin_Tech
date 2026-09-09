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

        // Save using async file stream
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

    public Task<Stream?> GetFileStreamAsync(string subDirectory, string safeFileName, CancellationToken cancellationToken = default)
    {
        var targetDirectory = ResolveSafeDirectory(subDirectory);
        var targetPath = Path.Combine(targetDirectory, safeFileName);
        ValidateWithinRoot(targetPath);

        if (!File.Exists(targetPath))
        {
            _logger.LogWarning("File {SafeFileName} was not found in storage.", safeFileName);
            return Task.FromResult<Stream?>(null);
        }

        var fileStream = new FileStream(
            targetPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        return Task.FromResult<Stream?>(fileStream);
    }

    public Task<bool> DeleteFileAsync(string subDirectory, string safeFileName, CancellationToken cancellationToken = default)
    {
        try
        {
            var targetDirectory = ResolveSafeDirectory(subDirectory);
            var targetPath = Path.Combine(targetDirectory, safeFileName);
            ValidateWithinRoot(targetPath);

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
                _logger.LogInformation("Deleted file {SafeFileName} from storage during cleanup/rollback.", safeFileName);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete file {SafeFileName} during cleanup.", safeFileName);
            return Task.FromResult(false);
        }
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
