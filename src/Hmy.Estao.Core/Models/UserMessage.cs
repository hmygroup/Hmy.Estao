namespace Hmy.Estao.Core.Models;

/// <summary>A presentation-independent message. Arguments are data, never translated text.</summary>
public sealed record UserMessage(string Code, params object?[] Arguments)
{
    public static UserMessage FromException(Exception exception) => exception.Data[nameof(UserMessage)] as UserMessage
        ?? exception switch
        {
            HttpRequestException { StatusCode: { } status } => new("error.http_status", (int)status),
            HttpRequestException => new("error.network"),
            TimeoutException => new("error.timeout"),
            UnauthorizedAccessException => new("error.permission"),
            FileNotFoundException => new("error.file_missing"),
            System.Text.Json.JsonException => new("error.invalid_data"),
            InvalidDataException => new("error.invalid_data"),
            IOException => new("error.io"),
            OperationCanceledException => new("operation.cancelled"),
            _ => new("error.operation")
        };
}

public static class UserMessageExtensions
{
    // Keep existing exception types and English diagnostics for CLI/legacy consumers.
    public static T WithUserMessage<T>(this T exception, string code, params object?[] arguments) where T : Exception
    {
        exception.Data[nameof(UserMessage)] = new UserMessage(code, arguments);
        return exception;
    }
}

public interface IUserMessageProgress : IProgress<string>
{
    void Report(UserMessage message);
}

public static class UserMessageProgressExtensions
{
    public static void ReportMessage(this IProgress<string>? progress, UserMessage message, string diagnostic)
    {
        if (progress is IUserMessageProgress localized) localized.Report(message);
        else progress?.Report(diagnostic);
    }
}
