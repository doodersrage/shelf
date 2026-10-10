# Keeping Shelf up to date

## What updates itself

Dependabot opens a pull request each week for anything out of date: the NuGet packages, the GitHub Actions in `.github/workflows`, the .NET base images in the `Dockerfile`, and the front-end files pinned in `src/Shelf.Api/vendor`. CI runs on every one; merge it when CI is green.

## The copied front-end files

The app serves PDF.js and the Inter and Lora fonts from `wwwroot`, so no page calls out to a CDN. Their versions are pinned in `src/Shelf.Api/vendor/package.json`. When Dependabot bumps one, or to bump one yourself:

```bash
cd src/Shelf.Api/vendor
npm ci
npm run vendor
git add ../wwwroot package.json package-lock.json
```

`npm run vendor` copies exactly the files the app uses, with their licenses. CI checks that `wwwroot` matches the pinned versions and fails with this instruction if it does not. After a PDF.js update, open a PDF in the reader, turn a page, and select some text, since its API has changed between major versions before.

## Tesseract, Poppler, and the base image

The Docker image installs Tesseract and Poppler from the base image's distribution. Rebuilding the image, which the release workflow does, picks up their security updates. On a server run from source, update them with the system's package manager.

## Releasing

See *Versions and releases* in the README: bump `VersionPrefix` in `Directory.Build.props`, add the release to `CHANGELOG.md`, and push a `vX.Y.Z` tag.
