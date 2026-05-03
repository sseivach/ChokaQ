# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ChokaQ.sln ./
COPY src/ChokaQ.Abstractions/ChokaQ.Abstractions.csproj src/ChokaQ.Abstractions/
COPY src/ChokaQ.Core/ChokaQ.Core.csproj src/ChokaQ.Core/
COPY src/ChokaQ.Storage.SqlServer/ChokaQ.Storage.SqlServer.csproj src/ChokaQ.Storage.SqlServer/
COPY src/ChokaQ.TheDeck/ChokaQ.TheDeck.csproj src/ChokaQ.TheDeck/
COPY samples/ChokaQ.Sample.Bus/ChokaQ.Sample.Bus.csproj samples/ChokaQ.Sample.Bus/

RUN dotnet restore samples/ChokaQ.Sample.Bus/ChokaQ.Sample.Bus.csproj

COPY . .
RUN dotnet publish samples/ChokaQ.Sample.Bus/ChokaQ.Sample.Bus.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ChokaQ.Sample.Bus.dll"]
