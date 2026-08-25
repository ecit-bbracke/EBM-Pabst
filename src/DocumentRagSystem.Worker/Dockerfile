# Stage 1: Build, Test, & Publish
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

# Copy Solution and Project Files
COPY DocumentRagSystem.slnx ./
COPY src/DocumentRagSystem.Core/DocumentRagSystem.Core.csproj src/DocumentRagSystem.Core/
COPY src/DocumentRagSystem.Infrastructure/DocumentRagSystem.Infrastructure.csproj src/DocumentRagSystem.Infrastructure/
COPY src/DocumentRagSystem.WebApi/DocumentRagSystem.WebApi.csproj src/DocumentRagSystem.WebApi/
COPY src/DocumentRagSystem.Worker/DocumentRagSystem.Worker.csproj src/DocumentRagSystem.Worker/
COPY tests/UnitTests/DocumentRagSystem.UnitTests.csproj tests/UnitTests/
COPY tests/IntegrationTests/DocumentRagSystem.IntegrationTests.csproj tests/IntegrationTests/
COPY tests/E2ETests/DocumentRagSystem.E2ETests.csproj tests/E2ETests/

# Restore NuGet Packages
RUN dotnet restore

# Copy all source code
COPY . .

# Publish Worker
WORKDIR /app/src/DocumentRagSystem.Worker
RUN dotnet publish -c Release -r linux-x64 --self-contained true -o /app/publish

# Stage 2: Runtime Image
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build /app/data ./data

ENTRYPOINT ["./DocumentRagSystem.Worker"]
