using System.ComponentModel;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

// Render (and most PaaS) inject the port to listen on via the PORT env var.
// Bind to 0.0.0.0 so the container is reachable; fall back to 8080 locally.
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// MCP server over Streamable HTTP. Tools are discovered from the attributed class below.
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<EcommerceTool>();

var app = builder.Build();

// Optional shared-secret guard. When MCP_AUTH_TOKEN is set, every MCP request must send
// "Authorization: Bearer <token>". Left unset (e.g. local dev) the endpoint is open.
var authToken = Environment.GetEnvironmentVariable("MCP_AUTH_TOKEN");
if (!string.IsNullOrEmpty(authToken))
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await next();
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        if (header != $"Bearer {authToken}")
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        await next();
    });
}

// Plain HTTP 200 for Render's health checks (does not require MCP auth).
app.MapGet("/health", () => Results.Ok("healthy"));

// Maps the MCP Streamable HTTP endpoint at the application root.
app.MapMcp();

app.Run();

[McpServerToolType]
public sealed class EcommerceTool
{
    [McpServerTool(Name = "get_product_details"),
     Description("Retrieve detailed information about a product including name, price, stock, and description")]
    public static string GetProductDetails(
        [Description("Product ID or SKU")] string productId)
    {
        return $@"{{
    ""productId"": ""{productId}"",
    ""name"": ""Premium Wireless Headphones"",
    ""price"": 149.99,
    ""currency"": ""USD"",
    ""stockQuantity"": 45,
    ""category"": ""Electronics"",
    ""description"": ""High-quality wireless headphones with noise cancellation and 30-hour battery life"",
    ""rating"": 4.5,
    ""reviews"": 1247
}}";
    }

    [McpServerTool(Name = "search_products"),
     Description("Search for products by keyword, category, or filter criteria. Returns a list of matching products")]
    public static string SearchProducts(
        [Description("Search keyword or phrase")] string query,
        [Description("Filter by category (e.g., Electronics, Clothing, Home)")] string category = "All")
    {
        return $@"{{
    ""query"": ""{query}"",
    ""category"": ""{category}"",
    ""totalResults"": 3,
    ""products"": [
        {{
            ""productId"": ""PRD-001"",
            ""name"": ""Wireless Mouse"",
            ""price"": 29.99,
            ""stockQuantity"": 120,
            ""rating"": 4.3
        }},
        {{
            ""productId"": ""PRD-002"",
            ""name"": ""Mechanical Keyboard"",
            ""price"": 89.99,
            ""stockQuantity"": 67,
            ""rating"": 4.7
        }},
        {{
            ""productId"": ""PRD-003"",
            ""name"": ""USB-C Hub"",
            ""price"": 34.99,
            ""stockQuantity"": 89,
            ""rating"": 4.4
        }}
    ]
}}";
    }

    [McpServerTool(Name = "get_order_status"),
     Description("Check the current status and tracking information for a customer order")]
    public static string GetOrderStatus(
        [Description("Order number or ID")] string orderId)
    {
        return $@"{{
    ""orderId"": ""{orderId}"",
    ""status"": ""In Transit"",
    ""orderDate"": ""2025-10-08T14:30:00Z"",
    ""estimatedDelivery"": ""2025-10-14T18:00:00Z"",
    ""trackingNumber"": ""TRK-{orderId}-XYZ"",
    ""carrier"": ""FastShip Express"",
    ""items"": [
        {{
            ""productId"": ""PRD-001"",
            ""name"": ""Wireless Mouse"",
            ""quantity"": 2,
            ""price"": 29.99
        }}
    ],
    ""totalAmount"": 59.98,
    ""shippingAddress"": ""123 Main St, Anytown, USA""
}}";
    }

    [McpServerTool(Name = "add_to_cart"),
     Description("Add a product to the shopping cart with specified quantity")]
    public static string AddToCart(
        [Description("Product ID to add")] string productId,
        [Description("Quantity to add (must be positive)")] int quantity)
    {
        return $@"{{
    ""success"": true,
    ""message"": ""Product added to cart successfully"",
    ""cartId"": ""CART-123456"",
    ""productId"": ""{productId}"",
    ""quantity"": {quantity},
    ""cartTotal"": {quantity * 49.99:F2},
    ""itemsInCart"": {quantity + 2}
}}";
    }

    [McpServerTool(Name = "get_customer_info"),
     Description("Retrieve customer account information including order history, loyalty points, and preferences")]
    public static string GetCustomerInfo(
        [Description("Customer ID or email address")] string customerId)
    {
        return $@"{{
    ""customerId"": ""{customerId}"",
    ""name"": ""John Doe"",
    ""email"": ""john.doe@example.com"",
    ""memberSince"": ""2023-05-15"",
    ""loyaltyPoints"": 2450,
    ""tier"": ""Gold"",
    ""totalOrders"": 23,
    ""totalSpent"": 3456.78,
    ""preferredCategories"": [""Electronics"", ""Books"", ""Home & Garden""],
    ""savedAddresses"": 2,
    ""paymentMethodsOnFile"": 3
}}";
    }
}
