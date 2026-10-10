# Shelf in one container: the app, plus Tesseract and Poppler for reading scanned PDFs.
# Everything it keeps (the database, e-books, audiobooks, and sign-in keys) lives under /data.

# The app is built once, on the builder's own processor: a framework-dependent publish runs on any, and carries
# SQLite's native library for each. Only the final stage is made per platform (amd64 and arm64).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Shelf.Api/Shelf.Api.csproj src/Shelf.Api/
COPY src/Shelf.ServiceDefaults/Shelf.ServiceDefaults.csproj src/Shelf.ServiceDefaults/
RUN dotnet restore src/Shelf.Api/Shelf.Api.csproj
COPY src/ src/
ARG VERSION=
# Publish restores again, now that the whole source is here: the restore above sees only the project files, and
# without the Razor pages in sight it leaves out the package that carries _framework/blazor.web.js. Published from
# that alone, no page could become interactive. The packages restored above are reused, so this costs little.
RUN dotnet publish src/Shelf.Api/Shelf.Api.csproj -c Release -o /app ${VERSION:+-p:Version=$VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Calibre turns Kindle files into EPUBs. It adds several hundred megabytes, so it is left out unless asked for:
#   docker build --build-arg CALIBRE=true .
ARG CALIBRE=false
RUN apt-get update \
    && apt-get install -y --no-install-recommends tesseract-ocr tesseract-ocr-eng poppler-utils curl \
        $(if [ "$CALIBRE" = "true" ]; then echo calibre; fi) \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data && chown app:app /data
COPY docker-entrypoint.sh /usr/local/bin/shelf-entrypoint
RUN chmod 755 /usr/local/bin/shelf-entrypoint
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__Shelf="Data Source=/data/shelf.db" \
    EbookStore__Root=/data/ebooks \
    AudioStore__Root=/data/audio \
    CoverStore__Root=/data/covers
# The entry point runs Shelf as PUID:PGID (the app user, 1654, by default) once /data is theirs; see docker-entrypoint.sh.
VOLUME /data
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -fsS http://localhost:8080/alive || exit 1
ENTRYPOINT ["shelf-entrypoint", "dotnet", "Shelf.Api.dll"]
