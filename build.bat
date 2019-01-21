dotnet restore src/DenryuRebalancer
dotnet build src/DenryuRebalancer

dotnet restore tests/DenryuRebalancer.Tests
dotnet build tests/DenryuRebalancer.Tests
dotnet test tests/DenryuRebalancer.Tests
