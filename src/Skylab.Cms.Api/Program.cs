using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Skylab.Cms.Api.AccountAccess;
using Skylab.Cms.Api.AccountErasure;
using Skylab.Cms.Api.Endpoints;
using Skylab.Cms.Api.Middleware;
using Skylab.Cms.Application;
using Skylab.Cms.Application.Services.Helpers;
using Skylab.Cms.Infrastructure;
using Skylab.Cms.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

var keycloakSection = builder.Configuration.GetSection("Keycloak");
var requireHttpsMetadata = keycloakSection.GetValue("RequireHttpsMetadata", true);
var keycloakAuthority = keycloakSection["Authority"];
var keycloakMetadataAddress = keycloakSection["MetadataAddress"];

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
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
            OnTokenValidated = ctx =>
            {
                if (ctx.Principal?.Identity is not ClaimsIdentity identity)
                    return Task.CompletedTask;

                AzpClientRoleMapper.AddTo(identity, ctx.Principal);
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("CmsAccess", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("cms:access");
    })
    .AddAccountErasurePolicy();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var corsOrigins = (builder.Configuration["Cors:AllowedOrigins"] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod();
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
app.UseInternalRouteGuard();
app.UseCors();
app.UseAuthentication();
app.UseAccountAccessGate();
app.UseAuthorization();
app.MapAccountAccessHealthEndpoints();
app.MapCmsEndpoints();
app.MapCollectionEndpoints();
app.MapAccountErasureEndpoints();

app.Run();

public partial class Program;
