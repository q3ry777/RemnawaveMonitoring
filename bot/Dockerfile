# Сборка из корня репозитория: docker build -f bot/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG VERSION=1.0.0
WORKDIR /src
COPY Shared/ Shared/
COPY bot/ bot/
RUN dotnet publish bot/RemnawaveBot.csproj -c Release -o /out -p:Version=${VERSION} -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /out .
ENV DATA_DIR=/data \
    DOTNET_gcServer=0 \
    DOTNET_GCHeapHardLimit=0x8000000
VOLUME /data
ENTRYPOINT ["dotnet", "RemnawaveBot.dll"]
