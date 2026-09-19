# Build context must be the repository root:
#   docker build -f src/Vapor.ControlPlane/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY .editorconfig ./
COPY global.json ./
COPY Vapor.sln ./
COPY src/Vapor.Protocol/Vapor.Protocol.csproj src/Vapor.Protocol/
COPY src/Vapor.Steam.Core/Vapor.Steam.Core.csproj src/Vapor.Steam.Core/
COPY src/Vapor.ControlPlane/Vapor.ControlPlane.csproj src/Vapor.ControlPlane/
RUN dotnet restore src/Vapor.ControlPlane/Vapor.ControlPlane.csproj

COPY src/ src/
RUN dotnet publish src/Vapor.ControlPlane/Vapor.ControlPlane.csproj \
	-c Release \
	-o /app/publish \
	/p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Persisted SQLite stores (controlplane.db, audit.db) live under /app/data.
RUN mkdir -p /app/data && chown -R $APP_UID /app/data

COPY --from=build /app/publish .

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Vapor.ControlPlane.dll"]
