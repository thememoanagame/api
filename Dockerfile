# Build from the repository root after initializing the themes submodule:
#   git submodule update --init --recursive
#   docker build -t memoana-api .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the complete repository, including the checked-out modules/themes submodule.
COPY . .

RUN test -f modules/themes/src/themes/wwwroot/data/01a0bbbe-e0f4-7251-86c8-cc9bc84703d0/manifest.json \
    || (echo "Theme assets are missing. Run 'git submodule update --init --recursive' before docker build." >&2 && exit 1)

RUN dotnet restore src/memoana/memoana.csproj
RUN dotnet publish src/memoana/memoana.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    Themes__RootPath=/app/modules/themes/src/themes/wwwroot \
    Persistence__DatabasePath=/data/memoana.db

COPY --from=build /app/publish .
# Copy runtime theme data and image assets from the submodule into the image.
COPY --from=build /src/modules/themes/src/themes/wwwroot ./modules/themes/src/themes/wwwroot

EXPOSE 8080
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "memoana.dll"]
