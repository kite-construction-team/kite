FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ ./src/
COPY benchmarks/techempower/ ./benchmarks/techempower/
RUN dotnet publish src/Kite.Server/Kite.Server.csproj -c Release -o /out \
    && dotnet publish benchmarks/techempower/Kite.TechEmpower.App/Kite.TechEmpower.App.csproj -c Release -o /app-out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ENV DOTNET_GCDynamicAdaptationMode=0
ENV DOTNET_ReadyToRun=0
ENV DOTNET_HillClimbing_Disable=1
ENV Logging__LogLevel__Default=Warning
ENV Logging__LogLevel__Microsoft.Hosting.Lifetime=Warning

WORKDIR /app
COPY --from=build /out ./
COPY --from=build /app-out ./app/
COPY benchmarks/techempower/kite-data-proxy ./kite-data
RUN mkdir -p /app/kite-data/plugins /app/kite-data/state /app/kite-data/logs

EXPOSE 8080
ENTRYPOINT ["dotnet", "Kite.Server.dll", "--home", "/app/kite-data"]
