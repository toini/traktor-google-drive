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

## Running

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
