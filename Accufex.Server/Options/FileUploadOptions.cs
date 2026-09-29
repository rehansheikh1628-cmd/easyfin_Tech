namespace Accufex.Server.Options;

public class FileUploadOptions
{
    public const string SectionName = "FileUpload";

    public long MaxPdfSizeBytes { get; set; } = 262144000; // Default 250MB (supports 100+ MB statements)

    public string StorageRoot { get; set; } = "Storage/Statements";

    public string StorageProvider { get; set; } = "Local";

    public AzureBlobStorageOptions AzureBlob { get; set; } = new();

    public string[] AllowedExtensions { get; set; } = [".pdf"];

    public string[] AllowedContentTypes { get; set; } = ["application/pdf"];
}

public class AzureBlobStorageOptions
{
    public string ConnectionString { get; set; } = string.Empty;

    public string ContainerName { get; set; } = "statements";
}
