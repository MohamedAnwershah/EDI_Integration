using Microsoft.EntityFrameworkCore;

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
    Console.WriteLine($"\n[WEBHOOK TRIGGERED] Receiving PO: {payload.po_number} from {payload.sender_id}");

    var newOrder = new PurchaseOrder
    {
        PoNumber = payload.po_number,
        PartnerId = payload.sender_id,
        OrderDate = DateTime.TryParse(payload.date_created, out var parsedDate) ? parsedDate : DateTime.UtcNow,
        TotalAmount = payload.items.Sum(i => i.qty * i.price),
        LineItems = [.. payload.items.Select(i => new PoLineItem
        {
            Sku = i.product_code,
            Quantity = i.qty,
            UnitPrice = i.price
        })]
    };

    db.PurchaseOrders.Add(newOrder);
    await db.SaveChangesAsync();

    Console.WriteLine($"[SUCCESS] Order {newOrder.PoNumber} mapped and saved to ERP database (ID: {newOrder.Id}).\n");

    return Results.Ok(new { status = "success", message = "EDI 850 Processed Successfully", erp_reference_id = newOrder.Id });
});

app.MapGet("/erp/orders", async (ErpDbContext db) =>
{
    Console.WriteLine("\n[ERP SYSTEM] Fetching all Purchase Orders...");

    var orders = await db.PurchaseOrders
        .Include(o => o.LineItems)
        .ToListAsync();

    return Results.Ok(orders);
});

app.MapPost("/erp/orders/{id}/send-invoice", async (int id, ErpDbContext db, IHttpClientFactory httpClientFactory) =>
{
    Console.WriteLine($"\n[ERP SYSTEM] Generating Invoice for Order ID: {id}...");

    var order = await db.PurchaseOrders.FindAsync(id);
    if (order == null) 
    {
        return Results.NotFound(new { error = "Order not found in ERP database." });
    }

    var invoicePayload = new ZenbridgeOutbound810Dto
    {
        receiver_id = order.PartnerId,
        po_reference = order.PoNumber,
        invoice_date = DateTime.UtcNow.ToString("yyyy-MM-dd"), 
        total_amount = order.TotalAmount
    };

    var client = httpClientFactory.CreateClient();
    var zenbridgeApiUrl = "https://postman-echo.com/post"; 
    
    Console.WriteLine($"[NETWORK] Sending 810 Invoice payload to {zenbridgeApiUrl}...");
    var response = await client.PostAsJsonAsync(zenbridgeApiUrl, invoicePayload);

    if (response.IsSuccessStatusCode)
    {
        Console.WriteLine("[SUCCESS] Invoice successfully received by Zenbridge!");
        return Results.Ok(new { 
            message = "Invoice 810 transmitted successfully.", 
            sent_payload = invoicePayload 
        });
    }

    return Results.Problem("Failed to communicate with Zenbridge API.");
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
// --- Zenbridge Outbound JSON Payload (810 Invoice) ---
class ZenbridgeOutbound810Dto
{
    public string document_type { get; set; } = "810"; // Always 810 for Invoices
    public required string receiver_id { get; set; }
    public required string po_reference { get; set; }
    public required string invoice_date { get; set; }
    public decimal total_amount { get; set; }
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