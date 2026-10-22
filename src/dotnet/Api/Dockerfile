FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY ["Api/Api.csproj", "Api/"]
RUN dotnet restore "Api/Api.csproj"

# Copy everything else and build
COPY . .
WORKDIR "/src/Api"
RUN dotnet publish -c Release -o /app

# Final stage: runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app .

# Expose the port
EXPOSE 8080
ENTRYPOINT ["dotnet", "Api.dll"]
