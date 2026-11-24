# MeetJoe API — multi-stage production image (.NET 9)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json MeetJoe.sln ./
COPY src/MeetJoe.Contracts/MeetJoe.Contracts.csproj src/MeetJoe.Contracts/
COPY src/MeetJoe.Domain/MeetJoe.Domain.csproj src/MeetJoe.Domain/
COPY src/MeetJoe.Application/MeetJoe.Application.csproj src/MeetJoe.Application/
COPY src/MeetJoe.Infrastructure/MeetJoe.Infrastructure.csproj src/MeetJoe.Infrastructure/
COPY src/MeetJoe.Api/MeetJoe.Api.csproj src/MeetJoe.Api/

RUN dotnet restore src/MeetJoe.Api/MeetJoe.Api.csproj

COPY src/MeetJoe.Contracts/ src/MeetJoe.Contracts/
COPY src/MeetJoe.Domain/ src/MeetJoe.Domain/
COPY src/MeetJoe.Application/ src/MeetJoe.Application/
COPY src/MeetJoe.Infrastructure/ src/MeetJoe.Infrastructure/
COPY src/MeetJoe.Api/ src/MeetJoe.Api/

RUN dotnet publish src/MeetJoe.Api/MeetJoe.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=40s --retries=5 \
  CMD curl -fsS http://127.0.0.1:8080/health || exit 1
ENTRYPOINT ["dotnet", "MeetJoe.Api.dll"]
