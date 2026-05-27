FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY FoundryMemo.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Install fonts for PdfSharp PDF generation (Liberation Sans = Arial-compatible)
RUN apt-get update && apt-get install -y --no-install-recommends \
    fonts-liberation \
    curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

EXPOSE 8088
ENV ASPNETCORE_URLS=http://+:8088

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD curl -f http://localhost:8088/ || exit 1

ENTRYPOINT ["dotnet", "FoundryMemo.dll"]
