namespace Jumbo.AmazonEdi.Core.Invoicing;

/// <summary>The outcome of validating one invoice. Failure reasons are written verbatim into
/// AmazonInvoice.LastError, so they must read as something Erica can act on without a developer.</summary>
public sealed class ValidationResult
{
    private ValidationResult(IReadOnlyList<string> failures) => Failures = failures;

    public IReadOnlyList<string> Failures { get; }

    public bool IsValid => Failures.Count == 0;

    /// <summary>All failures joined into one line, for the LastError column.</summary>
    public string Message => string.Join("; ", Failures);

    public static ValidationResult Success() => new(Array.Empty<string>());

    public static ValidationResult Failed(IReadOnlyList<string> failures) => new(failures);
}
