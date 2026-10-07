# ---------------------------------------------------------------------------
# A Dockerfile is a RECIPE for building a "container image": a sealed package
# holding the server plus everything it needs to run, identical on any machine.
#
# This one uses a "multi-stage build":
#   stage 1 (build)    - a big image with the full .NET SDK compiles the code
#   stage 2 (runtime)  - a small image with only the .NET runtime runs it
# Only the finished program is copied into the final image, so it is small and
# contains no compilers or source code.
# ---------------------------------------------------------------------------

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy ONLY the project file first and restore packages. Docker caches each step, so unless
# the packages change, this slow step is skipped on later builds.
COPY src/GameServices.Api/GameServices.Api.csproj src/GameServices.Api/
RUN dotnet restore src/GameServices.Api/GameServices.Api.csproj

# Now the rest of the code, then compile and package it.
COPY src/GameServices.Api/ src/GameServices.Api/
RUN dotnet publish src/GameServices.Api/GameServices.Api.csproj -c Release -o /app --no-restore

# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Never run a server as an all-powerful "root" user: the .NET images include an
# unprivileged user called "app" for exactly this.
USER app

# Inside the container the server listens on 8080 (docker-compose maps it to 5080 on your PC).
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "GameServices.Api.dll"]
