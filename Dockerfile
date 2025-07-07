# Build stage
#FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
#WORKDIR /app

# Copy everything
#COPY . .

# Inject GitHub token into NuGet config
#RUN --mount=type=secret,id=github_token \
#    test -s /run/secrets/github_token && \
#    sed "s|%GITHUB_TOKEN%|$(cat /run/secrets/github_token)|" /app/nuget.config > /app/nuget.config.patched && \
#    mv /app/nuget.config.patched /app/nuget.config

# Restore and publish
#RUN dotnet restore TraktorGoogleDrive.sln
#RUN dotnet publish TraktorGoogleDrive/TraktorGoogleDrive.csproj -c Release -o /app/dist


# Runtime stage: serve with nginx
FROM nginx:alpine AS runtime
#COPY --from=build /app/dist/wwwroot /usr/share/nginx/html
RUN rm /etc/nginx/conf.d/default.conf

COPY out/wwwroot /usr/share/nginx/html
COPY nginx.conf /etc/nginx/nginx.conf
