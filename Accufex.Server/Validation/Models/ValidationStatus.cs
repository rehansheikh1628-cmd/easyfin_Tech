namespace Accufex.Server.Validation.Models;

public static class ValidationStatus
{
    public const string Valid = "VALID";
    public const string Review = "REVIEW";
    public const string Invalid = "INVALID";
    public const string Corrected = "CORRECTED";
}

public static class ValidationSeverity
{
    public const string Info = "INFO";
    public const string Warning = "WARNING";
    public const string Error = "ERROR";
}

public static class BalanceStatus
{
    public const string Balanced = "BALANCED";
    public const string Mismatch = "MISMATCH";
    public const string NotCheckable = "NOT_CHECKABLE";
}
