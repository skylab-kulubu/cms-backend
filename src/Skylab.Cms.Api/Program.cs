using System.Text.Json.Serialization;
using Skylab.Cms.Api.Endpoints;
using Skylab.Cms.Api.Middleware;
using Skylab.Cms.Application;
using Skylab.Cms.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

app.UseExceptionHandler();
app.MapCmsEndpoints();

app.Run();