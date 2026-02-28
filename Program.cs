using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ErpDbContext>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapPost("/webhook/zenbridge/inbound-850", async (ZenbridgeInbound850Dto payload, ErpDbContext db) =>
{
    var newOrder = new PurchaseOrder
    {
        PoNumber = payload.po_number,
        PartnerId = payload.sender_id,
        OrderDate = DateTime.TryParse(payload.date_created, out var parsedDate) ? parsedDate : DateTime.UtcNow,
        TotalAmount = payload.items.Sum(i => i.qty * i.price),
        LineItems = payload.items.Select(i => new PoLineItem
        {
            Sku = i.product_code,
            Quantity = i.qty,
            UnitPrice = i.price
        }).ToList()
    };

    db.PurchaseOrders.Add(newOrder);
    await db.SaveChangesAsync();

    return Results.Ok(new { status = "success", message = "EDI 850 Processed", erp_reference_id = newOrder.Id });
});

app.MapGet("/erp/orders", async (ErpDbContext db) =>
{
    var orders = await db.PurchaseOrders.Include(o => o.LineItems).ToListAsync();
    return Results.Ok(orders);
});

app.MapPost("/erp/orders/{id}/send-invoice", async (int id, ErpDbContext db, IHttpClientFactory httpClientFactory) =>
{
    var order = await db.PurchaseOrders.Include(o => o.LineItems).FirstOrDefaultAsync(o => o.Id == id);
    
    if (order == null) 
    {
        return Results.NotFound(new { error = "Order not found" });
    }

    var invoicePayload = new List<ZenbridgeOutboundPayload>
    {
        new ZenbridgeOutboundPayload
        {
            data = new ZenbridgeDataWrapper
            {
                section1 = new Section1
                {
                    beginningSegmentForInvoice = new BeginningSegment
                    {
                        date = DateTime.UtcNow.ToString("yyyyMMdd"),
                        invoiceNumber = $"INV-{order.Id}-{DateTime.UtcNow.ToString("MMdd")}",
                        purchaseOrderNumber = order.PoNumber
                    }
                },
                section2 = new Section2(),
                section3 = new Section3
                {
                    totalMonetaryValueSummary = new TotalMonetaryValueSummary
                    {
                        amount = order.TotalAmount
                    },
                    transactionTotals = new TransactionTotals
                    {
                        hashTotal = order.TotalAmount,
                        numberOfLineItems = order.LineItems.Count
                    }
                }
            }
        }
    };

    var client = httpClientFactory.CreateClient();
    
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "1c5f875c-edc2-4992-8405-76dfcc35703b");
    
    var zenbridgeApiUrl = "https://api.sandbox.zenbridge.io/customer/edi/send/AnwerShah/Invoice?connectionName=shah_sftp"; 
    
    var response = await client.PostAsJsonAsync(zenbridgeApiUrl, invoicePayload);

    if (response.IsSuccessStatusCode)
    {
        return Results.Ok(new { message = "Invoice 810 transmitted successfully", sent_payload = invoicePayload });
    }

    var errorResponse = await response.Content.ReadAsStringAsync();
    return Results.Problem($"Status: {response.StatusCode}. Details: {errorResponse}");
});

app.Run();

class ZenbridgeInbound850Dto
{
    public required string document_id { get; set; }
    public required string sender_id { get; set; }
    public required string po_number { get; set; }
    public required string date_created { get; set; }
    public required List<ZenbridgeItemDto> items { get; set; }
}

class ZenbridgeItemDto
{
    public required string product_code { get; set; }
    public int qty { get; set; }
    public decimal price { get; set; }
}

class PurchaseOrder
{
    public int Id { get; set; }
    public required string PoNumber { get; set; }
    public required string PartnerId { get; set; }
    public DateTime OrderDate { get; set; }
    public decimal TotalAmount { get; set; }
    public List<PoLineItem> LineItems { get; set; } = new(); 
}

class PoLineItem
{
    public int Id { get; set; }
    public required string Sku { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public int PurchaseOrderId { get; set; }
}

class ZenbridgeOutboundPayload
{
    public required ZenbridgeDataWrapper data { get; set; }
}

class ZenbridgeDataWrapper
{
    public required Section1 section1 { get; set; }
    public required Section2 section2 { get; set; }
    public required Section3 section3 { get; set; }
}

class Section1
{
    public required BeginningSegment beginningSegmentForInvoice { get; set; }
}

class BeginningSegment
{
    public required string date { get; set; } 
    public required string invoiceNumber { get; set; }
    public required string purchaseOrderNumber { get; set; }
}

class Section2
{
    public List<object> groupBaselineItemDataInvoice_210 { get; set; } = new();
}

class Section3
{
    public required TotalMonetaryValueSummary totalMonetaryValueSummary { get; set; }
    public required TransactionTotals transactionTotals { get; set; }
}

class TotalMonetaryValueSummary
{
    public decimal amount { get; set; }
}

class TransactionTotals
{
    public decimal hashTotal { get; set; }
    public int numberOfLineItems { get; set; }
}

class ErpDbContext : DbContext
{
    public DbSet<PurchaseOrder> PurchaseOrders { get; set; }
    public DbSet<PoLineItem> PoLineItems { get; set; } 

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite("Data Source=erp_system.db"); 
    }
}