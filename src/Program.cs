using System.Text.Encodings.Web;
using System.Text.Json;
using Dapper;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Npgsql;
using Swashbuckle.AspNetCore.SwaggerGen;
using TestJob.Api;

var builder = WebApplication.CreateBuilder(args);
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = jsonOptions.PropertyNamingPolicy;
    options.JsonSerializerOptions.WriteIndented = true;
    options.JsonSerializerOptions.Encoder = jsonOptions.Encoder;
});
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.SuppressMapClientErrors = true;
    options.InvalidModelStateResponseFactory = context => new BadRequestObjectResult(
        ProcessingResponse.Error("INVALID_JSON", string.Join(" ", context.ModelState.Values
            .SelectMany(value => value.Errors)
            .Select(error => string.IsNullOrEmpty(error.ErrorMessage)
                ? "Request body must be a JSON object with string fields."
                : error.ErrorMessage))));
});
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings__Postgres must be configured.")));
builder.Services.AddScoped<IValidator<ProcessingRequest>, ProcessingRequestValidator>();
builder.Services.AddScoped<ProcessingService>();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "TestJob API",
        Version = "v1",
        Description = "HTML processing and AES-256/ECB decryption. POST /api/process."
    });
    options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, "TestJob.Api.xml"));
    options.SchemaFilter<ProcessingRequestSchemaFilter>();
});

var app = builder.Build();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        context.Abort();
    }
    catch (Exception exception) when (!context.Response.HasStarted)
    {
        var (status, code) = exception switch
        {
            RequestException error => (400, error.Code),
            BadHttpRequestException error => (error.StatusCode, "INVALID_REQUEST"),
            NpgsqlException => (500, "DATABASE_ERROR"),
            _ => (500, "INTERNAL_ERROR")
        };
        if (status >= 500)
            app.Logger.LogError(exception, "Request processing failed.");
        context.Response.Clear();
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(
            ProcessingResponse.Error(code, exception.Message), jsonOptions, context.RequestAborted);
    }
});
app.UseStatusCodePages(async statusContext =>
{
    var context = statusContext.HttpContext;
    await context.Response.WriteAsJsonAsync(
        ProcessingResponse.Error($"HTTP_{context.Response.StatusCode}",
            "The request could not be processed. Check the URL, HTTP method and Content-Type."),
        jsonOptions, context.RequestAborted);
});
app.UseSwagger(options => options.RouteTemplate = "api/swagger/{documentName}/swagger.json");
app.UseSwaggerUI(options =>
{
    options.RoutePrefix = "api/swagger";
    options.SwaggerEndpoint("/api/swagger/v1/swagger.json", "TestJob API v1");
});
app.MapGet("/", () => Results.Redirect("/api/swagger"));
app.MapGet("/health", async (NpgsqlDataSource dataSource, CancellationToken cancellationToken) =>
{
    await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
    await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: cancellationToken));
    return Results.Ok(new { status = "ok" });
}).ExcludeFromDescription();
app.MapControllers();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<ProcessingService>()
        .InitializeAsync(app.Lifetime.ApplicationStopping);
}
await app.RunAsync();

internal sealed class ProcessingRequestSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type != typeof(ProcessingRequest)
            || schema is not OpenApiSchema requestSchema
            || requestSchema.Properties is not { } properties)
            return;

        // All request fields are mandatory. Keep the DTO nullable so FluentValidation,
        // rather than model binding, returns our existing missing-parameter errors.
        requestSchema.Required = new HashSet<string>(properties.Keys);
        foreach (var property in properties.Values.OfType<OpenApiSchema>())
            property.Type &= ~JsonSchemaType.Null;
    }
}
