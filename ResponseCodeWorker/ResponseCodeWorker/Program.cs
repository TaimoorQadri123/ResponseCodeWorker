using Microsoft.EntityFrameworkCore;
using ResponseCodeWorker;
using ResponseCodeWorker.Data;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<ResponseDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHostedService<InsertWorker>();   // Service 1
builder.Services.AddHostedService<SyncWorker>();     // Service 2

var host = builder.Build();
host.Run();