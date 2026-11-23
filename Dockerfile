FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY CourtBook.csproj ./
RUN dotnet restore CourtBook.csproj
COPY . .
RUN dotnet publish CourtBook.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out ./
COPY deploy/appsettings.Production.json ./appsettings.Production.json
COPY deploy/entrypoint.sh ./entrypoint.sh
RUN mkdir -p /app/keys && chown app:app /app/keys
ENV ASPNETCORE_ENVIRONMENT=Production \
    COURTBOOK_PORTAL=Customer \
    DataProtection__KeyDirectory=/app/keys
USER app
EXPOSE 8080
ENTRYPOINT ["/bin/sh", "/app/entrypoint.sh"]
