using System.ComponentModel;
using System.Text;
using System.Text.Json;
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
    .WithTools<JawwalTools>();

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
public sealed class JawwalTools
{
    // ----- E-Commerce tools (Bagisto) ---------------------------------------

    [McpServerTool(Name = "search_catalog"),
     Description("Search the product catalog by keyword and/or category. Returns matching products.")]
    public static Task<string> SearchCatalog(
        [Description("Search keyword or phrase")] string query = "",
        [Description("Filter by category (e.g. data_bundle, sim_card)")] string category = "",
        [Description("Maximum number of results to return")] int limit = 10)
    {
        var args = new Dictionary<string, object> { ["limit"] = limit };
        if (!string.IsNullOrWhiteSpace(query)) args["query"] = query;
        if (!string.IsNullOrWhiteSpace(category)) args["category"] = category;
        return JawwalMcpClient.CallToolAsync("search_catalog", args);
    }

    [McpServerTool(Name = "get_price_availability"),
     Description("Get the current price, currency and stock availability for a product.")]
    public static Task<string> GetPriceAvailability(
        [Description("Product ID or SKU")] string productId)
    {
        return JawwalMcpClient.CallToolAsync("get_price_availability", new { product_id = productId });
    }

    [McpServerTool(Name = "manage_cart"),
     Description("View the cart, add an item, or remove an item. action = view | add | remove.")]
    public static Task<string> ManageCart(
        [Description("Cart action: view, add, or remove")] string action,
        [Description("Product ID (required for add)")] string productId = "",
        [Description("Quantity to add (required for add)")] int quantity = 1,
        [Description("Cart item ID (required for remove)")] string cartItemId = "")
    {
        var args = new Dictionary<string, object> { ["action"] = action };
        if (!string.IsNullOrWhiteSpace(productId)) args["product_id"] = productId;
        if (string.Equals(action, "add", StringComparison.OrdinalIgnoreCase)) args["quantity"] = quantity;
        if (!string.IsNullOrWhiteSpace(cartItemId)) args["cart_item_id"] = cartItemId;
        return JawwalMcpClient.CallToolAsync("manage_cart", args);
    }

    [McpServerTool(Name = "get_order_status"),
     Description("Check the current status and tracking information for an order.")]
    public static Task<string> GetOrderStatus(
        [Description("Order number or ID")] string orderId)
    {
        return JawwalMcpClient.CallToolAsync("get_order_status", new { order_id = orderId });
    }

    // ----- Admin tools ------------------------------------------------------

    [McpServerTool(Name = "check_inventory"),
     Description("Check warehouse inventory levels (admin). Optionally filter by warehouse or low stock only.")]
    public static Task<string> CheckInventory(
        [Description("Warehouse name, or 'all' for every warehouse")] string warehouse = "all",
        [Description("When true, only return products that are low on stock")] bool lowStockOnly = false)
    {
        return JawwalMcpClient.CallToolAsync("check_inventory", new
        {
            warehouse,
            low_stock_only = lowStockOnly
        });
    }

    [McpServerTool(Name = "get_sales_statistics"),
     Description("Get aggregated sales statistics over a date range (admin).")]
    public static Task<string> GetSalesStatistics(
        [Description("Start date (YYYY-MM-DD)")] string startDate,
        [Description("End date (YYYY-MM-DD)")] string endDate,
        [Description("Grouping: day, week, or month")] string groupBy = "day")
    {
        return JawwalMcpClient.CallToolAsync("get_sales_statistics", new
        {
            start_date = startDate,
            end_date = endDate,
            group_by = groupBy
        });
    }
}

/// <summary>
/// Thin client that forwards tool calls to the Jawwal MCP server using the standard
/// MCP JSON-RPC 2.0 protocol (POST {base}/mcp). This is the same endpoint Claude Desktop
/// uses and requires no Bearer token (the server defaults to the ai_agent channel).
/// </summary>
internal static class JawwalMcpClient
{
    // Configurable via env var; defaults to the Postman collection's base_url.
    private static readonly string Endpoint =
        Environment.GetEnvironmentVariable("JAWWAL_MCP_URL") ?? "http://localhost:8000/mcp";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static int _requestId;

    public static async Task<string> CallToolAsync(string toolName, object arguments)
    {
        var requestId = Interlocked.Increment(ref _requestId);
        var payload = new
        {
            jsonrpc = "2.0",
            id = requestId,
            method = "tools/call",
            @params = new
            {
                name = toolName,
                arguments
            }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            // Streamable HTTP servers may answer with either JSON or an SSE stream.
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");

            using var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return Error($"Backend returned HTTP {(int)response.StatusCode} for tool '{toolName}'.", body);
            }

            return ParseJsonRpcResult(ExtractJson(body), toolName);
        }
        catch (Exception ex)
        {
            return Error($"Failed to reach Jawwal MCP server at {Endpoint} for tool '{toolName}'.", ex.Message);
        }
    }

    /// <summary>
    /// Streamable HTTP may wrap the JSON-RPC envelope in Server-Sent Events framing
    /// (lines like "data: {...}"). Pull the JSON payload out of either form.
    /// </summary>
    private static string ExtractJson(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return body;
        }

        var builder = new StringBuilder();
        foreach (var line in body.Split('\n'))
        {
            var trimmedLine = line.TrimStart();
            if (trimmedLine.StartsWith("data:", StringComparison.Ordinal))
            {
                builder.Append(trimmedLine["data:".Length..].Trim());
            }
        }

        return builder.Length > 0 ? builder.ToString() : body;
    }

    /// <summary>
    /// Unwraps the JSON-RPC envelope: returns the text content of a successful tool
    /// result, surfaces JSON-RPC errors, and falls back to the raw result otherwise.
    /// </summary>
    private static string ParseJsonRpcResult(string json, string toolName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : "unknown error";
                return Error($"Tool '{toolName}' returned a JSON-RPC error.", message ?? "unknown error");
            }

            if (root.TryGetProperty("result", out var result))
            {
                // MCP tool results expose their payload under result.content[].text.
                if (result.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.Array)
                {
                    var builder = new StringBuilder();
                    foreach (var item in content.EnumerateArray())
                    {
                        if (item.TryGetProperty("text", out var text) &&
                            text.ValueKind == JsonValueKind.String)
                        {
                            if (builder.Length > 0) builder.Append('\n');
                            builder.Append(text.GetString());
                        }
                    }

                    if (builder.Length > 0)
                    {
                        return builder.ToString();
                    }
                }

                return result.GetRawText();
            }

            return json;
        }
        catch (JsonException)
        {
            // Not parseable as JSON — hand back whatever the server sent.
            return json;
        }
    }

    private static string Error(string summary, string detail) =>
        JsonSerializer.Serialize(new { error = summary, detail });
}
