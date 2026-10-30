FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS build
ARG TARGETARCH
WORKDIR /src

COPY . .
RUN dotnet restore SpotlightCacheService/SpotlightCacheService.csproj -a "$TARGETARCH" \
    && dotnet publish SpotlightCacheService/SpotlightCacheService.csproj -c Release \
       -a "$TARGETARCH" --no-restore --no-self-contained -o /out /p:UseAppHost=false \
    && mkdir -p /out/cache/data /out/cache/images

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:9b755ea799ae649141a073e30d8b62d8095a5465af2b7694d17866e8c05131d8
WORKDIR /app

COPY --from=build --chown=1654:1654 /out/ .
USER 1654:1654
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_EnableDiagnostics=0
EXPOSE 8080

ENTRYPOINT ["dotnet", "SpotlightCacheService.dll"]
