using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using ResponseCodeWorker.Models;

namespace ResponseCodeWorker.Data
{
    public class ResponseDbContext : DbContext
    {
        public ResponseDbContext(DbContextOptions<ResponseDbContext> options)
            : base(options)
        {
        }

        public DbSet<SrcTxn> SrcTxns { get; set; }
        public DbSet<ModifyPoint> ModifyPoints { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SrcTxn>(e =>
            {
                e.ToTable("SrcTxn");
                e.HasKey(x => x.TranId);
                e.Property(x => x.TranId).ValueGeneratedOnAdd();
                e.Property(x => x.Amount).HasPrecision(18, 2);
            });

            modelBuilder.Entity<ModifyPoint>().ToTable("ModifyPoint");
        }
    }
}
