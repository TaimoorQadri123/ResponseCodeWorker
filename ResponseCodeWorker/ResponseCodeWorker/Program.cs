using Microsoft.EntityFrameworkCore;
using ResponseCodeWorker;
using ResponseCodeWorker.Data;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<ResponseDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHostedService<InsertWorker>();   
builder.Services.AddHostedService<SyncWorker>();     

var host = builder.Build();
host.Run();