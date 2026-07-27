namespace CustomCoverArt.Exceptions;

/// <summary>
/// Base exception for Custom Cover Art plugin errors
/// </summary>
public class CustomCoverArtException : Exception
{
    public string ErrorCode { get; }
    public string UserMessage { get; }

    public CustomCoverArtException(string errorCode, string message, string userMessage, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        UserMessage = userMessage;
    }
}

/// <summary>
/// Exception thrown when image processing fails
/// </summary>
public class ImageProcessingException : CustomCoverArtException
{
    public ImageProcessingException(string message, string userMessage = "Image processing failed", Exception? innerException = null)
        : base("IMAGE_PROCESSING_ERROR", message, userMessage, innerException)
    {
    }
}

/// <summary>
/// Exception thrown when font operations fail
/// </summary>
public class FontException : CustomCoverArtException
{
    public FontException(string message, string userMessage = "Font operation failed", Exception? innerException = null)
        : base("FONT_ERROR", message, userMessage, innerException)
    {
    }
}

/// <summary>
/// Exception thrown when file operations fail
/// </summary>
public class FileOperationException : CustomCoverArtException
{
    public FileOperationException(string message, string userMessage = "File operation failed", Exception? innerException = null)
        : base("FILE_OPERATION_ERROR", message, userMessage, innerException)
    {
    }
}

/// <summary>
/// Exception thrown when library operations fail
/// </summary>
public class LibraryException : CustomCoverArtException
{
    public LibraryException(string message, string userMessage = "Library operation failed", Exception? innerException = null)
        : base("LIBRARY_ERROR", message, userMessage, innerException)
    {
    }
}

/// <summary>
/// Exception thrown when validation fails
/// </summary>
public class ValidationException : CustomCoverArtException
{
    public ValidationException(string message, string userMessage = "Validation failed", Exception? innerException = null)
        : base("VALIDATION_ERROR", message, userMessage, innerException)
    {
    }
}

/// <summary>
/// Exception thrown when rate limiting is exceeded
/// </summary>
public class RateLimitException : CustomCoverArtException
{
    public RateLimitException(string message, string userMessage = "Too many requests. Please try again later.", Exception? innerException = null)
        : base("RATE_LIMIT_EXCEEDED", message, userMessage, innerException)
    {
    }
}

/// <summary>
/// Exception thrown when security validation fails
/// </summary>
public class SecurityException : CustomCoverArtException
{
    public SecurityException(string message, string userMessage = "Security validation failed", Exception? innerException = null)
        : base("SECURITY_ERROR", message, userMessage, innerException)
    {
    }
}
