FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY RemotePortable.Relay.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 47900
ENTRYPOINT ["dotnet","RemotePortable.Relay.dll"]