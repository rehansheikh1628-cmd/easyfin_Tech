namespace EasyFin_Tech.Server.Options;

public class FileUploadOptions
{
    public const string SectionName = "FileUpload";

    public long MaxPdfSizeBytes { get; set; } = 52428800; // Default 50MB

    public string StorageRoot { get; set; } = "Storage/Statements";

    public string[] AllowedExtensions { get; set; } = [".pdf"];

    public string[] AllowedContentTypes { get; set; } = ["application/pdf"];
}
