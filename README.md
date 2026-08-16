# traktor-google-drive

### 🎧 https://traktor-google-drive-194132977379.europe-north1.run.app

Blazor WebAssembly app that reads a Traktor `collection.nml` from Google Drive
and streams the referenced audio files in the browser. Sign in with the Google
account that owns the collection; access is read-only.

## Live endpoint

| | |
|---|---|
| **URL** | <https://traktor-google-drive-194132977379.europe-north1.run.app> |
| Alias (legacy URL form) | <https://traktor-google-drive-mulgdizrhq-lz.a.run.app> |
| Platform | Google Cloud Run (managed) |
| GCP project | `traktor-toni-2025` (number `194132977379`) |
| Region | `europe-north1` |
| Service | `traktor-google-drive` |
| Container port | 8080 |
| Access | `--allow-unauthenticated` (public; the app gates itself with Google OAuth) |
| Live revision | `traktor-google-drive-00013-kp7`, image `gcr.io/traktor-toni-2025/traktor-google-drive:1197cf1` |

No custom domain is mapped — the `*.run.app` URL above is the only address.

> The service scales to zero, so the first request after an idle period is a
> cold start. If it returns `429 Rate exceeded` / `no available instance` for
> more than a few minutes, force a new revision — that re-registers it with the
> serving layer:
>
> ```bash
> gcloud run services update traktor-google-drive --region europe-north1 \
>   --update-env-vars=REDEPLOY_TS=$(date +%Y%m%d%H%M%S)
> ```
>
> This was needed on 2026-08-16 after the GCP billing account was replaced: the
> revision reported `Ready: True` and all quotas were at default, but Cloud Run
> would not schedule an instance until a new revision was created.

Re-read it at any time with:

```bash
gcloud run services list --platform managed --project traktor-toni-2025
```

### Server-side surface

The container is an ASP.NET host whose only dynamic endpoint is the Drive
streaming proxy — everything else is static Blazor WASM:

```
GET /api/proxy/drive/{fileId}?token={googleAccessToken}
```

It re-issues `GET https://www.googleapis.com/drive/v3/files/{fileId}?alt=media`
with `Authorization: Bearer {token}` and forwards `Range` / `Content-Range`.
It exists only because `<audio src>` cannot attach an `Authorization` header.

## Deploy

Build image and push

```bash
dotnet restore

DOCKER_BUILDKIT=1 docker build --secret id=github_token,src=./.github_token -t traktor-google-drive .
docker tag traktor-google-drive tonijuvani/traktor-google-drive:$(date +%Y%m%d)
docker tag traktor-google-drive tonijuvani/traktor-google-drive:latest
docker push tonijuvani/traktor-google-drive
```

## Restoring NuGet packages locally with private GitHub feed

If your `nuget.config` uses environment variable placeholders for credentials, run this in your project root:

```sh
export GITHUB_USERNAME=toini
export GITHUB_TOKEN=$(cat .github_token)
dotnet restore
```

- Your token must be in a file called `.github_token` in the project root.
- You may see warnings like:
  - `Error occurred while getting package vulnerability data: Unable to load the service index for source ...`
- These warnings are usually harmless and relate to NuGet's attempt to fetch vulnerability data from the package sources. They do not affect restoring or building your project.

## Running (Local Docker Compose)

```yaml
services:
  traktor-google-drive:
    image: tonijuvani/traktor-google-drive:latest
    pull_policy: always
    container_name: traktor-google-drive
    ports:
      - "5000:443"
    volumes:
      - /home/toni/homeassistant/ssl/fullchain.pem:/etc/letsencrypt/fullchain.pem:ro
      - /home/toni/homeassistant/ssl/privkey.pem:/etc/letsencrypt/privkey.pem:ro
```

## Deploying to Google Cloud Run

See [README-cloudrun.md](README-cloudrun.md) for build, push and deploy steps,
including the billing/cold-start failure modes this service has actually hit.

## Tests

End-to-end tests drive the real app in Chromium with the Google Drive API
mocked, so they need no Google account and no network:

```bash
cd tests/e2e
npm install
npx playwright install chromium
npx playwright test              # the suite
npx playwright test screenshots  # writes tests/e2e/screenshots/
```

The harness seeds `sessionStorage.access_token` directly and intercepts every
`googleapis.com` call plus the audio proxy — see `tests/e2e/drive-mock.ts`. The
fixture in `tests/e2e/fixtures/collection.nml` is shaped to the parser's exact
path-normalisation rules; the comment at the top of that file explains why.

`node` is not on `PATH` by default on this machine (nvm is loaded with
`--no-use`), so prefix commands with:

```bash
export PATH="$HOME/.nvm/versions/node/$(cat ~/.nvm/alias/default)/bin:$PATH"
```
