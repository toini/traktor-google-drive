FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY out/ ./
ENTRYPOINT ["dotnet", "TraktorGoogleDrive.Server.dll"]
