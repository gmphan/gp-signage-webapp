#!/bin/sh
dotnet build -c Release src/src.slnx
dotnet publish -c Release src/src.slnx
#About Azure Docker - I use "Azure App Service for Containers"
#Therefore now need to have the whole stack configuration.
# Build for linux/amd64 and push directly to GitHub Container Registry
docker buildx build --platform linux/amd64 -t ghcr.io/gmphan/gp-signage-webapp:latest --push .
# Build for linux/arm64 (Raspberry Pi) and push directly to GitHub Container Registry
docker buildx build --platform linux/arm64 -t ghcr.io/gmphan/gp-signage-webapp:pi-latest --push .
#build for multi-platform image and push directly to Azure Container Registry
#docker buildx build --platform linux/amd64,linux/arm64 -t ocbuuregistry.azurecr.io/gp-signage-webapp:latest --push .

