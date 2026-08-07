# EasyShare development targets
# Run `just` or `just help` to list targets.

default: help

help:
    @just --list

build:
    dotnet build src/EasyShare.App/EasyShare.App.csproj -c Release

test:
    dotnet test tests/EasyShare.Core.Tests/EasyShare.Core.Tests.csproj -c Release

integration:
    dotnet run --project tests/EasyShare.IntegrationTests/EasyShare.IntegrationTests.csproj -c Release

lint:
    dotnet format EasyShare.slnx --verify-no-changes

check: build test integration lint

install:
    powershell -ExecutionPolicy Bypass -File ./install.ps1

publish:
    dotnet publish src/EasyShare.App/EasyShare.App.csproj -c Release -r win-x64 --self-contained -o publish

clean:
    dotnet clean
