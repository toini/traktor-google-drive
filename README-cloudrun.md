# Google Cloud Run Deployment

This guide explains how to deploy `traktor-google-drive` to Google Cloud Run, replacing the previous Linux/nginx/SSL setup. Cloud Run manages HTTPS automatically, so no SSL config or certificates are needed in the container.

## 1. Prerequisites

- Google Cloud project with billing enabled
- [gcloud CLI](https://cloud.google.com/sdk/docs/install) installed and authenticated
- Docker installed

## 2. Build and Push Docker Image

```bash
# Authenticate Docker with Google
gcloud auth configure-docker

# Build the Docker image
DOCKER_BUILDKIT=1 docker build --secret id=github_token,src=./.github_token -t gcr.io/YOUR_PROJECT_ID/traktor-google-drive:latest .

# Push the image to Google Container Registry
docker push gcr.io/YOUR_PROJECT_ID/traktor-google-drive:latest
```

Replace `YOUR_PROJECT_ID` with your actual Google Cloud project ID.

## 3. Deploy to Cloud Run

```bash
gcloud run deploy traktor-google-drive \
  --image gcr.io/YOUR_PROJECT_ID/traktor-google-drive:latest \
  --platform managed \
  --region europe-north1 \
  --allow-unauthenticated \
  --port 80
```

- `--allow-unauthenticated` makes the service public. Remove if you want authentication.
- `--port 80` matches the default nginx port (see below).

## 4. Container Changes for Cloud Run

- **nginx.conf**: Change `listen 443 ssl;` to `listen 80;` and remove all `ssl_` lines. Cloud Run terminates SSL for you.
- **Dockerfile**: No changes needed unless you want to remove certificate volume mounts.

## 5. Tools & Useful Commands

- [Google Cloud Console](https://console.cloud.google.com/run)
- [gcloud CLI reference](https://cloud.google.com/sdk/gcloud/reference/run/deploy)

## 6. Example nginx.conf for Cloud Run

```
events {}

http {
    include       mime.types;
    default_type  application/octet-stream;
    sendfile        on;
    keepalive_timeout  65;
    server {
        listen 80;
        server_name _;
        root /usr/share/nginx/html;
        index index.html;
        location / {
            try_files $uri $uri/ /index.html;
        }
    }
}
```

---

## Running Locally (with Debugger)

To run the app locally with the .NET development server and debugger:

```sh
dotnet run --project TraktorGoogleDrive/TraktorGoogleDrive.csproj
```

- The app will be available at the URL shown in the output (typically http://localhost:5048 or similar).

---

## Running Locally (via Docker)

To run the built Docker image locally:

```sh
docker run --rm -p 8080:8080 gcr.io/traktor-toni-2025/traktor-google-drive:latest
```

- Visit http://localhost:8080 in your browser.
- This uses nginx and matches the production environment.

---

## Deploying to Google Cloud Run

1. Build and push the image:

```sh
DOCKER_BUILDKIT=1 docker build --secret id=github_token,src=.github_token -t gcr.io/traktor-toni-2025/traktor-google-drive:latest .

gcloud auth configure-docker
docker push gcr.io/traktor-toni-2025/traktor-google-drive:latest
```

2. Deploy to Cloud Run:

```sh
gcloud run deploy traktor-google-drive \
  --image gcr.io/traktor-toni-2025/traktor-google-drive:latest \
  --platform managed \
  --region europe-north1 \
  --allow-unauthenticated \
  --port 80
```

- Cloud Run will provide a public HTTPS URL after deployment.
- GCP handles SSL termination; your container only needs to serve HTTP on port 80.

- If prompted, enable required APIs.
- If the container fails to start, check logs in the Google Cloud Console (Cloud Run > Service > Logs).

## Notes

- The service will be available at the URL shown in the output after deployment.
- If you change the region, update the `--region` flag accordingly.
