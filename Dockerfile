FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

COPY Skylab.Cms.sln ./
COPY src/Skylab.Cms.Domain/Skylab.Cms.Domain.csproj              src/Skylab.Cms.Domain/
COPY src/Skylab.Cms.Application/Skylab.Cms.Application.csproj    src/Skylab.Cms.Application/
COPY src/Skylab.Cms.Infrastructure/Skylab.Cms.Infrastructure.csproj src/Skylab.Cms.Infrastructure/
COPY src/Skylab.Cms.Api/Skylab.Cms.Api.csproj                    src/Skylab.Cms.Api/

RUN dotnet restore

COPY src/ src/

RUN dotnet publish src/Skylab.Cms.Api/Skylab.Cms.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser
USER appuser

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

ENTRYPOINT ["dotnet", "Skylab.Cms.Api.dll"]