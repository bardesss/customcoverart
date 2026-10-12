namespace CustomCoverArt.Services;

/// <summary>
/// A request the renderer refuses for a reason the user can fix (canvas too large or too
/// small, …). Its message is safe to show and is returned to the client as-is, unlike
/// other exceptions, which are logged and replaced with a generic error.
/// </summary>
public sealed class CoverValidationException : Exception
{
    public CoverValidationException(string message)
        : base(message)
    {
    }
}
