module DenryuRebalancer.AppDbContext

open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Sqlite
open BTCPayServer.Lightning
open Microsoft.Extensions.Configuration
open BTCPayServer.Lightning

type AppDbContext(conf : IConfiguration) =
  inherit DbContext()

  [<DefaultValue>] val mutable NodeInfo : DbSet<LightningNodeInformation>

  override this.OnConfiguring(optionsBuilder: DbContextOptionsBuilder) =
    let connString = conf.GetSection("db").GetValue<string>("ConnectionString")
    optionsBuilder.UseSqlite(connString) |> ignore

  override this.OnModelCreating(builder : ModelBuilder) =
    base.OnModelCreating builder |> ignore