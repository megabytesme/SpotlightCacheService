FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["SpotlightCache.sln", "."]
COPY ["SpotlightCacheService/SpotlightCacheService.csproj", "SpotlightCacheService/"]

RUN dotnet restore "SpotlightCache.sln"
COPY . .

WORKDIR "/src/SpotlightCacheService"
RUN dotnet publish "SpotlightCacheService.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

RUN mkdir /app/cache
RUN mkdir /app/cache/data
RUN mkdir /app/cache/images

EXPOSE 8080

ENTRYPOINT ["dotnet", "SpotlightCacheService.dll"]
