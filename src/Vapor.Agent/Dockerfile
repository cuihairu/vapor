# Build context must be the repository root:
#   docker build -f src/Vapor.Agent/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY .editorconfig ./
COPY global.json ./
COPY Vapor.sln ./
COPY src/Vapor.Protocol/Vapor.Protocol.csproj src/Vapor.Protocol/
COPY src/Vapor.Steam.Core/Vapor.Steam.Core.csproj src/Vapor.Steam.Core/
COPY src/Vapor.Plugins.Core/Vapor.Plugins.Core.csproj src/Vapor.Plugins.Core/
COPY src/Vapor.Plugins.Monitoring/Vapor.Plugins.Monitoring.csproj src/Vapor.Plugins.Monitoring/
COPY src/Vapor.Agent/Vapor.Agent.csproj src/Vapor.Agent/
RUN dotnet restore src/Vapor.Agent/Vapor.Agent.csproj \
	&& dotnet restore src/Vapor.Plugins.Monitoring/Vapor.Plugins.Monitoring.csproj

COPY src/ src/
RUN dotnet publish src/Vapor.Agent/Vapor.Agent.csproj \
	-c Release \
	-o /app/publish \
	/p:UseAppHost=false \
	&& dotnet publish src/Vapor.Plugins.Monitoring/Vapor.Plugins.Monitoring.csproj \
	-c Release \
	-o /app/publish/plugins/vapor.monitoring \
	/p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Writable homes: /app/.vapor holds the encrypted credential store; /app/plugins the plugins.
RUN mkdir -p /app/.vapor /app/plugins && chown -R $APP_UID /app/.vapor /app/plugins

COPY --from=build /app/publish .

USER $APP_UID
ENV VAPOR_PLUGINS_DIR=/app/plugins
EXPOSE 9700
ENTRYPOINT ["dotnet", "Vapor.Agent.dll"]
