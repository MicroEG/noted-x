namespace NotedX.Server.Api;

internal static class ApiResults
{
    public static IResult NotFound(string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status404NotFound, title: "Not found");

    public static IResult BadRequest(string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");

    public static IResult Forbidden(string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status403Forbidden, title: "Forbidden");

    public static IResult Conflict(string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status409Conflict, title: "Conflict");

    public static IResult Unauthorized(string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
}
