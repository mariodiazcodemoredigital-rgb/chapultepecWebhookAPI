# 1. Imagen base con el runtime de ASP.NET
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80

# 2. Imagen con SDK para compilar
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiar la solución y los proyectos para restaurar dependencias
COPY ChapultepecEvoWebhookApi.sln ./
COPY Crm.Webhook.Api/Crm.Webhook.Api.csproj Crm.Webhook.Api/
COPY Crm.Webhook.Core/Crm.Webhook.Core.csproj Crm.Webhook.Core/

# Restaurar paquetes de toda la solución
RUN dotnet restore

# Copiar todo el código de ambos proyectos
COPY Crm.Webhook.Api/ Crm.Webhook.Api/
COPY Crm.Webhook.Core/ Crm.Webhook.Core/

# Publicar el proyecto de la API
RUN dotnet publish Crm.Webhook.Api/Crm.Webhook.Api.csproj -c Release -o /app/publish --no-restore

# 3. Imagen final de ejecución
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

# Configurar puerto para Easypanel
ENV ASPNETCORE_URLS=http://+:80

ENTRYPOINT ["dotnet", "Crm.Webhook.Api.dll"]