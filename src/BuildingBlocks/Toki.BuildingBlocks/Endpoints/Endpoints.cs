using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Toki.BuildingBlocks.Security;
using Toki.SharedKernel;

namespace Toki.BuildingBlocks.Endpoints;

/// <summary>One per slice. Discovered by assembly scanning.</summary>
public interface IEndpoint
{
    void Map(IEndpointRouteBuilder app);
}

public static class EndpointExtensions
{
    public static IEndpointRouteBuilder MapEndpoints(this IEndpointRouteBuilder app, IEnumerable<Assembly> assemblies)
    {
        var endpoints = assemblies.SelectMany(a => a.DefinedTypes)
            .Where(t => t is { IsAbstract: false, IsInterface: false } && t.IsAssignableTo(typeof(IEndpoint)));
        foreach (var t in endpoints) ((IEndpoint)Activator.CreateInstance(t, nonPublic: true)!).Map(app);
        return app;
    }

    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(p => p.RequireClaim(TokiClaims.Permission, permission));

    public static IResult ToHttp<T>(this Result<T> result, Func<T, IResult> ok) =>
        result.IsSuccess ? ok(result.Value) : Problem(result.Error!);

    public static IResult ToHttp<T>(this Result<T> result) => result.ToHttp(v => Results.Ok(v));

    public static IResult Problem(Error error)
    {
        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status422UnprocessableEntity,
        };
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };
        if (error.Details is not null) extensions["errors"] = error.Details;
        return Results.Problem(detail: error.Description, statusCode: status, title: error.Code, extensions: extensions);
    }
}
