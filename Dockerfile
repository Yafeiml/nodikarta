FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ProjectHub.Core/ProjectHub.Core.csproj ./
RUN dotnet restore ProjectHub.Core.csproj
COPY src/ProjectHub.Core/ ./
RUN dotnet publish ProjectHub.Core.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.title="Nodicarta Core" \
      org.opencontainers.image.source="https://github.com/Yafeiml/nodikarta" \
      org.opencontainers.image.version="1.0.0" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build /out/ ./
COPY LICENSE /usr/share/doc/nodicarta/LICENSE
COPY release/licenses/Npgsql-LICENSE.txt /usr/share/doc/nodicarta/Npgsql-LICENSE.txt
ENV ASPNETCORE_HTTP_PORTS=8080 ProjectHub__KeyDirectory=/var/lib/projecthub/keys
RUN mkdir -p /var/lib/projecthub/keys && chown -R app:app /var/lib/projecthub && chmod 700 /var/lib/projecthub/keys
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "ProjectHub.Core.dll"]
