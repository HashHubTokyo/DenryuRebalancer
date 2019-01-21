#!/bin/sh

dotnet restore tests/DenryuRebalancer.Tests
dotnet build tests/DenryuRebalancer.Tests
dotnet test tests/DenryuRebalancer.Tests
