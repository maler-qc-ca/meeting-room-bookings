FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY BookingApp/BookingApp.csproj BookingApp/
RUN dotnet restore BookingApp/BookingApp.csproj

COPY BookingApp/ BookingApp/
RUN dotnet publish BookingApp/BookingApp.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN mkdir -p /data && chown "$APP_UID:$APP_UID" /data
COPY --from=build /app/publish .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "BookingApp.dll"]
