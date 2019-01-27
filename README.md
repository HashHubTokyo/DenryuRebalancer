# DenryuRebalancer

A [Giraffe](https://github.com/giraffe-fsharp/Giraffe) web application, which has been created via the `dotnet new giraffe` command.

## Building and lauching the application

First, take a look for `src/DenryuRebalancer/appsettings.json` and edit environment specific info (e.g. credential information) as you need.
then `build.sh` and `dotnet run src/DenryuRebalancer` .

After the application has started visit [http://localhost:5000](http://localhost:5000) in your preferred browser.

## testing application

1. start external dependencies by `docker-compose up`
2. run `./test.sh`