#!/bin/sh
dotnet build -c Release src/GpSignageWebapp
dotnet publish -c Release src/GpSignageWebapp
# Build for linux/amd64 and push directly to GitHub Container Registry
docker buildx build --platform linux/amd64 -t ghcr.io/gmphan/gp-signage-webapp:latest --push .
#build for multi-platform image and push directly to Azure Container Registry
#docker buildx build --platform linux/amd64,linux/arm64 -t ocbuuregistry.azurecr.io/gp-signage-webapp:latest --push .

