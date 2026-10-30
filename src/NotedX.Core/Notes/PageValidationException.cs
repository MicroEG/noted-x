namespace NotedX.Core.Notes;

/// <summary>Signals that a page command was rejected because its input is not valid.</summary>
public sealed class PageValidationException : Exception
{
    /// <summary>Creates an exception with a message that is safe to show to the user.</summary>
    public PageValidationException(string message)
        : base(message)
    {
    }
}
