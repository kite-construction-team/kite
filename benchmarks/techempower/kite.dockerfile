FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ ./src/
COPY benchmarks/techempower/ ./benchmarks/techempower/
RUN dotnet publish src/Kite.Server/Kite.Server.csproj -c Release -o /out \
    && dotnet publish benchmarks/techempower/Kite.TechEmpower.Plugin/Kite.TechEmpower.Plugin.csproj -c Release -o /plugin-out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ENV DOTNET_GCDynamicAdaptationMode=0
ENV DOTNET_ReadyToRun=0
ENV DOTNET_HillClimbing_Disable=1
ENV Logging__LogLevel__Default=Warning
ENV Logging__LogLevel__Microsoft.Hosting.Lifetime=Warning

WORKDIR /app
COPY --from=build /out ./
COPY benchmarks/techempower/kite-data ./kite-data
RUN mkdir -p /app/kite-data/plugins /app/kite-data/state /app/kite-data/logs
COPY --from=build /plugin-out/ /app/kite-data/plugins/

EXPOSE 8080
ENTRYPOINT ["dotnet", "Kite.Server.dll", "--home", "/app/kite-data"]
