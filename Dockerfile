#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/runtime:3.1 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:3.1 AS build

ARG LYRA_POS_WALLET_NAME
ARG LYRA_POS_WALLET_PASSWORD
ARG LYRA_NETWORK
ARG LYRA_DB_NAME
ARG LYRA_DB_USER
ARG LYRA_DB_PASSWORD
ARG LYRA_P2P_PORT
ARG LYRA_API_PORT

ENV LYRA_POS_WALLET_NAME ${LYRA_POS_WALLET_NAME}
ENV LYRA_POS_WALLET_PASSWORD ${LYRA_POS_WALLET_PASSWORD}
ENV LYRA_NETWORK ${LYRA_NETWORK}

ENV LYRA_DB_NAME ${LYRA_DB_NAME}
ENV LYRA_DB_USER ${LYRA_DB_USER}
ENV LYRA_DB_PASSWORD ${LYRA_DB_PASSWORD}
ENV LYRA_P2P_PORT ${LYRA_P2P_PORT}
ENV LYRA_API_PORT ${LYRA_API_PORT}

ENV ASPNETCORE_URLS http://*:${LYRA_P2P_PORT};https://*:${LYRA_API_PORT}
ENV ASPNETCORE_HTTPS_PORT ${LYRA_API_PORT}
ENV LYRA_ApplicationConfiguration__LyraNode__Lyra__Database__DBConnect=mongodb://${LYRA_DB_USER}:${LYRA_DB_PASSWORD}@127.0.0.1/${LYRA_DB_NAME}
ENV LYRA_ApplicationConfiguration__LyraNode__Lyra__Wallet__Name ${LYRA_POS_WALLET_NAME}
ENV LYRA_ApplicationConfiguration__LyraNode__Lyra__Wallet__Password ${LYRA_POS_WALLET_PASSWORD}

WORKDIR /src
RUN ls -l

COPY ["Client/Client.CLI/Client.CLI.csproj", "Client/Client.CLI/"]
COPY ["Core/Lyra.Node2/Noded.csproj", "Core/Lyra.Node2/"]
COPY ["Core/Lyra.Core/Core.csproj", "Core/Lyra.Core/"]
COPY ["Core/Lyra.Data/Lyra.Data.csproj", "Core/Lyra.Data/"]
COPY ["Core/Lyra.Shared/Shared.csproj", "Core/Lyra.Shared/"]
RUN dotnet restore "Client/Client.CLI/Client.CLI.csproj"
RUN dotnet restore "Core/Lyra.Node2/Noded.csproj"
COPY . .

WORKDIR "/src/Client/Client.CLI"
RUN dotnet build "Client.CLI.csproj" -c Release -o /app/buildc

FROM build AS publish
RUN dotnet publish "Client.CLI.csproj" -c Release -o /app/publishc

WORKDIR /app/publishc
RUN /bin/sh -c "dotnet lyra.dll -n ${LYRA_NETWORK} -g ${LYRA_POS_WALLET_NAME} --password ${LYRA_POS_WALLET_PASSWORD}"

WORKDIR "/src/Core/Lyra.Node2"
RUN dotnet build "Noded.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Noded.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

ENTRYPOINT ["dotnet", "lyra.noded.dll"]
