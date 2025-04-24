# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

# Copy everything
COPY . .

# Inject GitHub token into NuGet config
RUN --mount=type=secret,id=github_token \
    mkdir -p ~/.nuget && \
    sed "s/%GITHUB_TOKEN%/$(cat /run/secrets/github_token)/" /app/nuget.template.config > /root/.nuget/NuGet.Config

# Restore and publish
RUN dotnet restore TraktorGoogleDrive.sln --configfile /root/.nuget/NuGet.Config
RUN dotnet publish TraktorGoogleDrive/TraktorGoogleDrive.csproj -c Release -o /app/dist

# Runtime stage: serve with nginx
FROM nginx:alpine AS runtime
COPY nginx.conf /etc/nginx/nginx.conf
COPY --from=build /app/dist/wwwroot /usr/share/nginx/html
