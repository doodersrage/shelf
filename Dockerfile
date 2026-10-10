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
RUN apt-get update \
    && apt-get install -y --no-install-recommends tesseract-ocr tesseract-ocr-eng poppler-utils \
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
ENTRYPOINT ["dotnet", "Shelf.Api.dll"]
