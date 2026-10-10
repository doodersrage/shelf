# Shelf in one container: the app, plus Tesseract and Poppler for reading scanned PDFs.
# Everything it keeps (the database, e-books, audiobooks, and sign-in keys) lives under /data.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Shelf.Api/Shelf.Api.csproj src/Shelf.Api/
COPY src/Shelf.ServiceDefaults/Shelf.ServiceDefaults.csproj src/Shelf.ServiceDefaults/
RUN dotnet restore src/Shelf.Api/Shelf.Api.csproj
COPY src/ src/
ARG VERSION=
RUN dotnet publish src/Shelf.Api/Shelf.Api.csproj -c Release -o /app --no-restore ${VERSION:+-p:Version=$VERSION}

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
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__Shelf="Data Source=/data/shelf.db" \
    EbookStore__Root=/data/ebooks \
    AudioStore__Root=/data/audio
USER app
VOLUME /data
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -fsS http://localhost:8080/alive || exit 1
ENTRYPOINT ["dotnet", "Shelf.Api.dll"]
