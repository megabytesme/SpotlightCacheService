FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["SpotlightCacheService/SpotlightCacheService.csproj", "SpotlightCacheService/"]

RUN dotnet restore "SpotlightCacheService/SpotlightCacheService.csproj" --use-current-runtime --maxcpucount
COPY . .

WORKDIR "/src/SpotlightCacheService"
RUN dotnet publish "SpotlightCacheService.csproj" -c Release -o /app/publish --use-current-runtime --no-self-contained --no-restore --maxcpucount /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

RUN mkdir /app/cache
RUN mkdir /app/cache/data
RUN mkdir /app/cache/images

EXPOSE 8080

ENTRYPOINT ["dotnet", "SpotlightCacheService.dll"]
