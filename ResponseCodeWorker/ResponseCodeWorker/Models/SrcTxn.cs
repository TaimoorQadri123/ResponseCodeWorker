using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ResponseCodeWorker.Models
{
    public class SrcTxn
    {
        public int TranId { get; set; }
        public string CustomerName { get; set; } = string.Empty;
        public string CardNumber { get; set; } = string.Empty;
        public string MerchantId { get; set; } = string.Empty;
        public string Channel { get; set; } = string.Empty;
        public string Currency { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? ResponseCode { get; set; }     
        public string? Description { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}