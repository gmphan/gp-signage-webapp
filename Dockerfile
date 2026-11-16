FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Install timezone data and set timezone
RUN apt-get update && apt-get install -y --no-install-recommends tzdata \
&& ln -fs /usr/share/zoneinfo/America/New_York /etc/localtime \
&& dpkg-reconfigure -f noninteractive tzdata \
&& rm -rf /var/lib/apt/lists/*
# Ensure full globalization (ICU is present in aspnet image) and set TZ
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV TZ=America/New_York
RUN mkdir /app
COPY ./src/GpSignageWebapp/bin/Release/net10.0/publish /app
WORKDIR /app
ENTRYPOINT ["dotnet", "GpSignageWebapp.dll"]

