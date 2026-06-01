# Runtime base: ASP.NET (not the bare runtime) so Kestrel/web hosting is available.
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
# Render injects PORT at runtime; Program.cs binds 0.0.0.0:$PORT (8080 fallback for local).
EXPOSE 8080

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Restore as its own layer for better caching.
COPY ["EStoreMCP.csproj", "./"]
RUN dotnet restore "./EStoreMCP.csproj"

# Publish.
COPY . .
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./EStoreMCP.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Final runtime image
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "EStoreMCP.dll"]
