# DenryuRebalancer

A [Giraffe](https://github.com/giraffe-fsharp/Giraffe) web application, which has been created via the `dotnet new giraffe` command.

## Building and lauching the application

First, take a look for `src/DenryuRebalancer/appsettings.json` and edit environment specific info (e.g. credential information) as you need.
then `build.sh` and `dotnet run src/DenryuRebalancer` .

After the application has started visit [http://localhost:5000](http://localhost:5000) in your preferred browser.

## testing application

Just run `./test.sh`

Under the hood, it starts `docker-compose up` for each test case using compose file in `tests/FNBitcoin.TestFramework` .
This is a great way to assure the independence of each test.

But sometime, you may want more granular control about LN daemons and bitcoind.
In that case, you can launch manually with `cd tests/DenryuRebalancer.Tests && docker-compose up`
for tweeking docker-compose instance launched in this way, we prepared facade scripts under `docker-scripts`
