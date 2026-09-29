FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY AgentMandate.slnx ./
COPY src/Mandate.Core/Mandate.Core.csproj src/Mandate.Core/
COPY src/Mandate.Api/Mandate.Api.csproj src/Mandate.Api/
RUN dotnet restore src/Mandate.Api/Mandate.Api.csproj
COPY src/ src/
RUN dotnet publish src/Mandate.Api/Mandate.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Mandate.Api.dll"]
