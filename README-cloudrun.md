# Google Cloud Run Deployment

> **Status: live** as of 2026-08-16, revision `traktor-google-drive-00013-kp7`.
>
> It was down from ~2026-07-26 to 2026-08-16 with `500 The request failed
> because billing is disabled for this project`. Fixed by attaching a new
> billing account (`01759C-104BC6-B5FBF4`) — the two older accounts were closed
> and could not be reopened. Enabling billing alone was not enough: the service
> kept returning `429 Rate exceeded` with `no available instance` in the logs
> despite a healthy revision and default quotas, and only recovered once a new
> revision was forced. See the note in [README.md](README.md).
>
> Hosting is still under review — audio egress is billed per GiB here, which is
> the one cost that scales with actual listening.

There is no nginx in this image. It was removed in `90e2e0e`; the container is
the ASP.NET host (`TraktorGoogleDrive.Server`) serving the Blazor WASM client
plus the `/api/proxy/drive/{fileId}` streaming endpoint, listening on 8080.

## 1. Prerequisites

- Google Cloud project with **billing enabled**
- [gcloud CLI](https://cloud.google.com/sdk/docs/install) installed and authenticated
- Docker with buildx (the Cloud Run target is `linux/amd64`)

## 2. Build

The server project has a `ProjectReference` to the client, so publishing the
server pulls the Blazor output in automatically.

```bash
dotnet restore TraktorGoogleDrive.sln

# Release (what gets deployed)
./publish.sh

# Debug build with symbols
./publish.sh --dev
```

Output lands in `./out`, which is what the Dockerfile copies.

## 3. Build and push the image

```bash
# One-time: create an amd64 builder (Cloud Run will not run arm64 images)
docker buildx create --name amd64-builder --use
docker buildx inspect --bootstrap

gcloud auth configure-docker

export TAG=$(git rev-parse --short HEAD)
docker buildx build \
  --platform linux/amd64 \
  -t gcr.io/traktor-toni-2025/traktor-google-drive:$TAG \
  -t gcr.io/traktor-toni-2025/traktor-google-drive:latest \
  --push .
```

## 4. Deploy

```bash
gcloud run deploy traktor-google-drive \
  --image gcr.io/traktor-toni-2025/traktor-google-drive:$TAG \
  --platform managed \
  --region europe-north1 \
  --allow-unauthenticated \
  --port 8080
```

- `--allow-unauthenticated` makes the service public; the app gates itself with
  Google OAuth.
- Cloud Run terminates TLS, so the container serves plain HTTP. The server
  deliberately does **not** call `UseHttpsRedirection()` — behind a TLS-
  terminating proxy that only risks a redirect loop.

## 5. Running locally

```bash
# Client only, with the dev server (the audio proxy is absent — e2e tests mock it)
dotnet run --project TraktorGoogleDrive/TraktorGoogleDrive.csproj --launch-profile http
# -> http://localhost:5048

# Full stack, exactly as deployed
dotnet run --project TraktorGoogleDrive.Server/TraktorGoogleDrive.Server.csproj
```

Via Docker:

```bash
./publish.sh
docker build -t traktor-google-drive:local .
docker run --rm -p 8080:8080 traktor-google-drive:local
```

## 6. Troubleshooting

| Symptom | Cause |
|---|---|
| `500` + "billing is disabled" | The GCP project has no active billing account. |
| `503` "not available yet" | Same root cause — Cloud Run cannot start the revision. |
| Container starts then exits | Check the port: Cloud Run sends traffic to `$PORT` (8080). |
| Blank page, 404 on `_framework/*` | Stale `out/` — re-run `./publish.sh`. |

Logs:

```bash
gcloud logging read \
  'resource.type="cloud_run_revision" AND resource.labels.service_name="traktor-google-drive"' \
  --project traktor-toni-2025 --limit 30 --freshness=7d
```
