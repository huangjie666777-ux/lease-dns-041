using System.Text.Json;
using SubnetPlanner.Api;
using SubnetPlanner.Api.Dhcp;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<DhcpPool>();
builder.Services.AddHostedService<DhcpServer>();
var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapPost("/plan", async (HttpContext http) =>
{
    JsonDocument doc;
    try
    {
        doc = await JsonDocument.ParseAsync(http.Request.Body);
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { errors = new[] { new { field = "$", message = $"JSON解析失败: {ex.Message}" } } });
    }

    using (doc)
    {
        if (!PlanRequestParser.TryParse(doc.RootElement, out var request, out var errors))
            return Results.BadRequest(new { errors = errors.Select(e => new { field = e.Field, message = e.Message }) });

        if (!Planner.TryPlan(request!, out var response, out var failure))
            return Results.UnprocessableEntity(new
            {
                failure = new { departmentId = failure!.DepartmentId, reason = failure.Reason }
            });

        return Results.Ok(new
        {
            departments = response!.Departments.Select(d => new
            {
                id = d.Id,
                cidr = d.Cidr,
                network = d.Network,
                broadcast = d.Broadcast,
                firstUsable = d.FirstUsable,
                lastUsable = d.LastUsable,
                usableCapacity = d.UsableCapacity
            }),
            remainingCidrs = response.RemainingCidrs,
            summary = new
            {
                totalAddresses = response.TotalAddresses,
                occupiedAddresses = response.OccupiedAddresses,
                allocatedAddresses = response.AllocatedAddresses,
                remainingAddresses = response.RemainingAddresses
            }
        });
    }
});

app.MapPost("/dhcp/activate", async (HttpContext http, DhcpPool pool) =>
{
    JsonDocument doc;
    try
    {
        doc = await JsonDocument.ParseAsync(http.Request.Body);
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { errors = new[] { new { field = "$", message = $"JSON解析失败: {ex.Message}" } } });
    }

    using (doc)
    {
        var root = doc.RootElement;
        if (!PlanRequestParser.TryParse(root, out var request, out var errors))
            return Results.BadRequest(new { errors = errors.Select(e => new { field = e.Field, message = e.Message }) });

        var extraErrors = new List<object>();
        string? departmentId = null;
        if (!root.TryGetProperty("departmentId", out var deptEl) || deptEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(deptEl.GetString()))
            extraErrors.Add(new { field = "departmentId", message = "部门ID必须是非空字符串" });
        else
            departmentId = deptEl.GetString()!;

        if (!TryGetPositiveInt(root, "offerSeconds", out var offerSeconds))
            extraErrors.Add(new { field = "offerSeconds", message = "报价秒数必须是正整数" });
        if (!TryGetPositiveInt(root, "leaseSeconds", out var leaseSeconds))
            extraErrors.Add(new { field = "leaseSeconds", message = "租约秒数必须是正整数" });
        if (extraErrors.Count > 0)
            return Results.BadRequest(new { errors = extraErrors });

        if (!Planner.TryPlan(request!, out var response, out var failure))
            return Results.UnprocessableEntity(new
            {
                failure = new { departmentId = failure!.DepartmentId, reason = failure.Reason }
            });

        var department = response!.Departments.FirstOrDefault(d => d.Id == departmentId);
        if (department is null)
            return Results.BadRequest(new
            {
                errors = new[] { new { field = "departmentId", message = $"部门 '{departmentId}' 不在规划结果中" } }
            });

        var firstUsable = IpMath.TryParseIPv4(department.FirstUsable, out var first) ? first : 0u;
        var lastUsable = IpMath.TryParseIPv4(department.LastUsable, out var last) ? last : 0u;
        var network = IpMath.TryParseIPv4(department.Network, out var net) ? net : 0u;
        var prefix = int.Parse(department.Cidr.Split('/')[1]);

        var config = new PoolConfig(
            department.Id, network, prefix, firstUsable, lastUsable,
            offerSeconds, leaseSeconds, 0x7F000001); // 服务器标识 127.0.0.1

        if (!pool.Activate(config))
            return Results.Conflict(new
            {
                error = "当前地址池存在未过期的报价或租约，拒绝替换",
                status = ToStatusJson(pool.GetStatus())
            });

        var status = pool.GetStatus();
        return Results.Ok(new
        {
            activated = true,
            departmentId = status.DepartmentId,
            cidr = status.Cidr,
            firstUsable = status.FirstUsable,
            lastUsable = status.LastUsable,
            offerSeconds = status.OfferSeconds,
            leaseSeconds = status.LeaseSeconds
        });
    }
});

app.MapGet("/dhcp/status", (DhcpPool pool) => Results.Ok(ToStatusJson(pool.GetStatus())));

app.Run();

static bool TryGetPositiveInt(JsonElement root, string field, out int value)
{
    value = 0;
    return root.TryGetProperty(field, out var el) && el.ValueKind == JsonValueKind.Number
        && el.TryGetInt32(out value) && value > 0;
}

static object ToStatusJson(PoolStatus status) => new
{
    active = status.Active,
    departmentId = status.DepartmentId,
    cidr = status.Cidr,
    firstUsable = status.FirstUsable,
    lastUsable = status.LastUsable,
    offerSeconds = status.OfferSeconds,
    leaseSeconds = status.LeaseSeconds,
    offers = status.Offers.Select(o => new
    {
        mac = o.Mac,
        address = IpMath.Format(o.Address),
        expiresAt = o.ExpiresAt
    }),
    leases = status.Leases.Select(l => new
    {
        mac = l.Mac,
        address = IpMath.Format(l.Address),
        expiresAt = l.ExpiresAt
    })
};

public partial class Program { }
