using System.Text.Json;
using ResuClean.Models;

namespace ResuClean;

/// <summary>
/// Consistent JSON, one error shape for every failure, query helpers and file streaming.
/// Everything the UI sees goes through here so error handling cannot drift between endpoints.
/// </summary>
public static class Api
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Wraps a handler so thrown exceptions become the standard error body.</summary>
    public static async Task<IResult> Guard(Func<Task<IResult>> handler)
    {
        try
        {
            return await handler().ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            return Error(ex.StatusCode, ex.Code, ex.Message, ex.Details);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(StatusCodes.Status404NotFound, "not_found", ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Error(StatusCodes.Status409Conflict, "conflict", ex.Message);
        }
        catch (BadHttpRequestException ex)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, ex.Message.Contains("too large", StringComparison.OrdinalIgnoreCase) ? "payload_too_large" : "bad_request", ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Error(StatusCodes.Status408RequestTimeout, "cancelled", "The request was cancelled.");
        }
        catch (Exception ex)
        {
            // Logged without resume content or keys: only the type and message.
            Console.Error.WriteLine($"[api] {ex.GetType().Name}: {ex.Message}");
            return Error(StatusCodes.Status500InternalServerError, "internal_error",
                "Something went wrong on the server. The details are in the server log.");
        }
    }

    public static IResult Error(int status, string code, string message, object? details = null) =>
        Results.Json(new ErrorResponse(new ErrorBody(code, message, details)), Json, statusCode: status);

    public static IResult Ok<T>(T value) => Results.Json(value, Json);

    public static IResult Created<T>(T value, string location) =>
        Results.Json(value, Json, statusCode: StatusCodes.Status201Created);

    public static int Page(HttpRequest request) =>
        int.TryParse(request.Query["page"], out var page) && page > 0 ? page : 1;

    public static int Size(HttpRequest request, int max = 100)
    {
        if (!int.TryParse(request.Query["size"], out var size) || size <= 0) return 25;
        return Math.Min(size, max);
    }

    public static string? Param(HttpRequest request, string name) => request.Query[name].FirstOrDefault();

    /// <summary>Streams a file without buffering it in memory. Used for .docx/.pdf/.eml/.zip downloads.</summary>
    public static IResult Download(string path, string downloadName, string contentType)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            return Error(StatusCodes.Status404NotFound, "not_found",
                "That file has not been generated yet. Open the kit again to generate it.");

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        return Results.File(stream, contentType, downloadName, enableRangeProcessing: true);
    }
}

/// <summary>An error with a deliberate status code. Thrown by services; rendered by Api.Guard.</summary>
public sealed class ApiException : Exception
{
    public int StatusCode { get; }
    public string Code { get; }
    public object? Details { get; }

    public ApiException(int statusCode, string code, string message, object? details = null) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Details = details;
    }

    public static ApiException Unprocessable(string code, string message, object? details = null) =>
        new(StatusCodes.Status422UnprocessableEntity, code, message, details);
}