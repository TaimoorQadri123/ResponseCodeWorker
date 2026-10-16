using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ResponseCodeWorker.Models
{
    public class ModifyPoint
    {
        public int Id { get; set; }
        public int TranId { get; set; }
        public DateTime ModifyOn { get; set; }
    }
}
