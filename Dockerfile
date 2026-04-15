# Build Stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY ["Urchin.csproj", "."]
RUN dotnet restore "./Urchin.csproj"

# Copy everything else and build
COPY . .
RUN dotnet build "Urchin.csproj" -c Release -o /app/build

# Publish Stage
FROM build AS publish
RUN dotnet publish "Urchin.csproj" -c Release -o /app/publish

# Runtime Stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Urchin.dll"]