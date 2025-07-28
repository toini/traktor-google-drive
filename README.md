# traktor-google-drive

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

See `README-cloudrun.md` for full instructions on deploying to Google Cloud Run, including required changes to `nginx.conf` and deployment steps.
