using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Skylab.Cms.Api.Authentication;
using Skylab.Cms.Api.Endpoints;
using Skylab.Cms.Api.Middleware;
using Skylab.Cms.Application;
using Skylab.Cms.Infrastructure;
using Skylab.Cms.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

var keycloakSection = builder.Configuration.GetSection("Keycloak");
var allowedClientIds = keycloakSection.GetSection("AllowedClientIds").Get<string[]>() ?? [];
var requireHttpsMetadata = keycloakSection.GetValue("RequireHttpsMetadata", true);
var keycloakAuthority = keycloakSection["Authority"];
var keycloakMetadataAddress = keycloakSection["MetadataAddress"];

builder.Services.AddAuthentication(AuthSchemes.Client)
    .AddJwtBearer(AuthSchemes.Client, options =>
    {
        options.Authority = keycloakAuthority;
        options.Audience = keycloakSection["Audience"];
        options.RequireHttpsMetadata = requireHttpsMetadata;
        if (keycloakMetadataAddress is not null)
            options.MetadataAddress = keycloakMetadataAddress;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true
        };
    })
    .AddJwtBearer(AuthSchemes.User, options =>
    {
        options.Authority = keycloakAuthority;
        options.Audience = keycloakSection["Audience"];
        options.RequireHttpsMetadata = requireHttpsMetadata;
        if (keycloakMetadataAddress is not null)
            options.MetadataAddress = keycloakMetadataAddress;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            NameClaimType = "preferred_username",
            RoleClaimType = "roles"
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var header = ctx.Request.Headers["X-User-Authorization"].ToString();
                if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    ctx.Token = header["Bearer ".Length..].Trim();
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthSchemes.ClientPolicy, policy =>
    {
        policy.AddAuthenticationSchemes(AuthSchemes.Client);
        policy.RequireAuthenticatedUser();
        if (allowedClientIds.Length > 0)
            policy.RequireClaim("azp", allowedClientIds);
    });
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:3001").AllowAnyHeader().AllowAnyMethod();
    });
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
    db.Database.Migrate();
}

app.UseExceptionHandler();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapCmsEndpoints();

app.Run();