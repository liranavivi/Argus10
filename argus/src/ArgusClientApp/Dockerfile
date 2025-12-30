# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files, nuget config and local packages for restore
COPY ["ArgusClientApp/ArgusClientApp.csproj", "ArgusClientApp/"]
COPY ["ArgusClientApp/nuget.config", "ArgusClientApp/"]
COPY ["nuget-packages/", "nuget-packages/"]

# Restore dependencies (uses local nuget-packages)
WORKDIR /src/ArgusClientApp
RUN dotnet restore "ArgusClientApp.csproj"

# Copy source and build
COPY ["ArgusClientApp/", "/src/ArgusClientApp/"]
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Create non-root user for security
RUN adduser --disabled-password --gecos "" --uid 1000 argusclient
USER argusclient

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "ArgusClientApp.dll"]

