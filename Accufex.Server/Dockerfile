# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Accufex.Server.csproj", "./"]
RUN dotnet restore "Accufex.Server.csproj"

COPY . .
RUN dotnet build "Accufex.Server.csproj" -c Release -o /app/build

# Stage 2: Publish
FROM build AS publish
RUN dotnet publish "Accufex.Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Create storage directory for tenant files
RUN mkdir -p /app/Storage/Statements

COPY --from=publish /app/publish .

ENTRYPOINT ["dotnet", "Accufex.Server.dll"]
