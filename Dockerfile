# Production releases should replace version tags with publisher-verified sha256 digests.
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.302-noble
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.10-noble

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src

COPY . .
# Bound build parallelism on small shared hosts; this does not change runtime CPU limits.
RUN dotnet restore src/LicensePlatform.Web/LicensePlatform.Web.csproj --disable-parallel -m:1 \
    && dotnet publish src/LicensePlatform.Web/LicensePlatform.Web.csproj \
       --configuration Release \
       --no-restore \
       --disable-build-servers \
       -m:1 \
       --output /out \
       /p:UseSharedCompilation=false \
       /p:UseAppHost=false

FROM ${DOTNET_ASPNET_IMAGE} AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

COPY --from=build --chown=app:app /out/ ./
RUN mkdir -p /app/data && chown app:app /app/data

# The supplied Compose deployment keeps this unprivileged user and grants a
# dedicated supplemental group read-only access to file-backed secrets.
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "LicensePlatform.Web.dll"]
