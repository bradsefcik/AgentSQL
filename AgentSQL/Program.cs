
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Stripe;
using Stripe.Checkout;
using SendGrid;
using SendGrid.Helpers.Mail;

var builder = WebApplication.CreateBuilder(args);

// Configure for Replit environment - bind to all interfaces
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5000);
});

builder.Services.AddRazorPages();
builder.Services.AddRouting();
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();

// PWA: minimal service worker/manifest already in wwwroot
app.MapRazorPages();

// --- Helpers ---
string B64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');

string CreateToken(string email, string tier, DateTimeOffset exp, string secret) {
    var payloadObj = new { Email = email, Tier = tier, Exp = exp };
    var payload = JsonSerializer.Serialize(payloadObj);
    var payloadB = Encoding.UTF8.GetBytes(payload);
    using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret ?? ""));
    var sig = h.ComputeHash(payloadB);
    return $"{B64Url(payloadB)}.{B64Url(sig)}";
}

bool VerifyToken(string token, string secret) {
    try{
        var parts = token.Split('.');
        if (parts.Length != 2) return false;
        var payloadJson = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0].Replace('-','+').Replace('_','/') + new string('=', (4 - parts[0].Length % 4) % 4)));
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret ?? ""));
        var sig = h.ComputeHash(Encoding.UTF8.GetBytes(payloadJson));
        var sigB64 = Convert.ToBase64String(sig).TrimEnd('=').Replace('+','-').Replace('/','_');
        return sigB64 == parts[1];
    }catch { return false; }
}

// --- Analytics: basic, privacy-friendly JSONL ---
app.MapPost("/api/analytics/track", async (HttpContext ctx) => {
    try{
        var env = ctx.RequestServices.GetRequiredService<IHostEnvironment>();
        var dir = Path.Combine(env.ContentRootPath, "App_Data"); Directory.CreateDirectory(dir);
        using var sr = new StreamReader(ctx.Request.Body);
        var body = await sr.ReadToEndAsync();
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : "evt";
        var variant = ctx.Request.Cookies["AgentSQL_AdVariant"] ?? "";
        var plan = (ctx.Request.Cookies["AgentSQL_Pro"] == "1") ? "pro" : "basic";
        var evt = new {
            ts = DateTimeOffset.UtcNow,
            name = name,
            plan = plan,
            adVariant = variant,
            ua = ctx.Request.Headers.UserAgent.ToString()
        };
        await System.IO.File.AppendAllTextAsync(Path.Combine(dir, "analytics.jsonl"), JsonSerializer.Serialize(evt) + "\n");
        return Results.Ok(new { ok = true });
    }catch(Exception ex){ return Results.Ok(new { ok=false, error = ex.Message }); }
});

// --- Admin auth ---
app.MapPost("/api/admin/login", async (HttpContext ctx) =>
{
    using var sr = new StreamReader(ctx.Request.Body);
    var body = await sr.ReadToEndAsync();
    var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement;
    var key = doc.TryGetProperty("key", out var el) ? el.GetString() : null;
    var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
    var expected = Environment.GetEnvironmentVariable("ADMIN_KEY") ?? cfg["Admin:Key"];
    if (string.IsNullOrWhiteSpace(expected)) return Results.BadRequest(new { ok=false, error="Admin key not configured" });
    if (key != expected) return Results.Ok(new { ok=false, error="Invalid key" });
    ctx.Response.Cookies.Append("AgentSQL_Admin", "1", new CookieOptions{ Expires=DateTimeOffset.UtcNow.AddDays(7), HttpOnly=false, IsEssential=true, SameSite=SameSiteMode.Lax });
    return Results.Ok(new { ok=true });
});

// --- Admin analytics JSON with revenue + cohorts and optional date range ---
app.MapGet("/api/admin/analytics", (HttpContext ctx, int days) =>
{
    try{
        var env = ctx.RequestServices.GetRequiredService<IHostEnvironment>();
        var path = Path.Combine(env.ContentRootPath, "App_Data", "analytics.jsonl");
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset start; DateTimeOffset end;
        var q = ctx.Request.Query;
        if (q.ContainsKey("from") && q.ContainsKey("to") && DateTimeOffset.TryParse(q["from"], out start) && DateTimeOffset.TryParse(q["to"], out end)) { }
        else { start = now.AddDays(-Math.Max(1, days)); end = now; }

        var byDay = new Dictionary<string, Dictionary<string, object>>();
        var dow = new Dictionary<string, Dictionary<string, object>>{
            {"Sun", new(){{"page_view",0},{"checkout_click",0},{"purchase",0},{"revenue_cents_total",0L}}},
            {"Mon", new(){{"page_view",0},{"checkout_click",0},{"purchase",0},{"revenue_cents_total",0L}}},
            {"Tue", new(){{"page_view",0},{"checkout_click",0},{"purchase",0},{"revenue_cents_total",0L}}},
            {"Wed", new(){{"page_view",0},{"checkout_click",0},{"purchase",0},{"revenue_cents_total",0L}}},
            {"Thu", new(){{"page_view",0},{"checkout_click",0},{"purchase",0},{"revenue_cents_total",0L}}},
            {"Fri", new(){{"page_view",0},{"checkout_click",0},{"purchase",0},{"revenue_cents_total",0L}}},
            {"Sat", new(){{"page_view",0},{"checkout_click",0},{"purchase",0},{"revenue_cents_total",0L}}}
        };
        var hour = new Dictionary<string, Dictionary<string, object>>();
        for(int h=0; h<24; h++) hour[h.ToString("D2")] = new(){{"page_view",0},{"checkout_click",0},{"purchase",0},{"revenue_cents_total",0L}};

        if (System.IO.File.Exists(path)){
            foreach(var line in System.IO.File.ReadAllLines(path)){
                if (string.IsNullOrWhiteSpace(line)) continue;
                try{
                    var el = JsonDocument.Parse(line).RootElement;
                    var ts = el.GetProperty("ts").GetDateTimeOffset();
                    if (ts < start || ts > end) continue;
                    var day = ts.UtcDateTime.ToString("yyyy-MM-dd");
                    var dayOfWeek = ts.UtcDateTime.DayOfWeek.ToString().Substring(0,3);
                    var hourKey = ts.UtcDateTime.ToString("HH");
                    if (!byDay.ContainsKey(day))
                        byDay[day] = new Dictionary<string, object>{{"page_view",0},{"ad_impression_A",0},{"ad_impression_B",0},{"checkout_click",0},{"purchase",0},{"revenue_cents_total",0L},{"revenue_cents_A",0L},{"revenue_cents_B",0L}};
                    var name = el.GetProperty("name").GetString();
                    if (name=="page_view") { byDay[day]["page_view"] = (int)byDay[day]["page_view"] + 1; dow[dayOfWeek]["page_view"] = (int)dow[dayOfWeek]["page_view"] + 1; hour[hourKey]["page_view"] = (int)hour[hourKey]["page_view"] + 1; }
                    else if (name=="ad_impression"){
                        var variant = el.TryGetProperty("variant", out var v) ? v.GetString() : (el.TryGetProperty("adVariant", out var av) ? av.GetString() : "");
                        var key = variant=="B" ? "ad_impression_B" : "ad_impression_A";
                        byDay[day][key] = (int)byDay[day][key] + 1;
                    }else if (name=="checkout_click") { byDay[day]["checkout_click"] = (int)byDay[day]["checkout_click"] + 1; dow[dayOfWeek]["checkout_click"] = (int)dow[dayOfWeek]["checkout_click"] + 1; hour[hourKey]["checkout_click"] = (int)hour[hourKey]["checkout_click"] + 1; }
                    else if (name=="purchase") {
                        byDay[day]["purchase"] = (int)byDay[day]["purchase"] + 1;
                        long amount = 0; try { amount = el.TryGetProperty("amount", out var a) ? a.GetInt64() : 0; } catch { amount = 0; }
                        var variant = el.TryGetProperty("adVariant", out var v2) ? v2.GetString() : "";
                        byDay[day]["revenue_cents_total"] = (long)byDay[day]["revenue_cents_total"] + amount;
                        if (variant=="B") byDay[day]["revenue_cents_B"] = (long)byDay[day]["revenue_cents_B"] + amount; else byDay[day]["revenue_cents_A"] = (long)byDay[day]["revenue_cents_A"] + amount;
                        dow[dayOfWeek]["purchase"] = (int)dow[dayOfWeek]["purchase"] + 1;
                        hour[hourKey]["purchase"] = (int)hour[hourKey]["purchase"] + 1;
                        dow[dayOfWeek]["revenue_cents_total"] = (long)dow[dayOfWeek]["revenue_cents_total"] + amount;
                        hour[hourKey]["revenue_cents_total"] = (long)hour[hourKey]["revenue_cents_total"] + amount;
                    }
                }catch {}
            }
        }
        return Results.Ok(new { ok=true, data=byDay, cohorts = new { dow, hour } });
    }catch(Exception ex){
        return Results.Ok(new { ok=false, error=ex.Message });
    }
});

// Daily CSV
app.MapGet("/api/admin/analytics.csv", (HttpContext ctx, int days) =>
{
    try{
        var env = ctx.RequestServices.GetRequiredService<IHostEnvironment>();
        var path = Path.Combine(env.ContentRootPath, "App_Data", "analytics.jsonl");
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset start; DateTimeOffset end;
        var q = ctx.Request.Query;
        if (q.ContainsKey("from") && q.ContainsKey("to") && DateTimeOffset.TryParse(q["from"], out start) && DateTimeOffset.TryParse(q["to"], out end)) { }
        else { start = now.AddDays(-Math.Max(1, days)); end = now; }
        var byDay = new SortedDictionary<string, Dictionary<string, object>>();
        if (System.IO.File.Exists(path)){
            foreach(var line in System.IO.File.ReadAllLines(path)){
                if (string.IsNullOrWhiteSpace(line)) continue;
                try{
                    var el = JsonDocument.Parse(line).RootElement;
                    var ts = el.GetProperty("ts").GetDateTimeOffset();
                    if (ts < start || ts > end) continue;
                    var day = ts.UtcDateTime.ToString("yyyy-MM-dd");
                    if (!byDay.ContainsKey(day)) byDay[day] = new Dictionary<string, object>{{"page_view",0},{"ad_impression_A",0},{"ad_impression_B",0},{"checkout_click",0},{"purchase",0},{"revenue_cents_total",0L},{"revenue_cents_A",0L},{"revenue_cents_B",0L}};
                    var name = el.GetProperty("name").GetString();
                    if (name=="page_view") byDay[day]["page_view"] = (int)byDay[day]["page_view"] + 1;
                    else if (name=="ad_impression"){
                        var variant = el.TryGetProperty("variant", out var v) ? v.GetString() : (el.TryGetProperty("adVariant", out var av) ? av.GetString() : "");
                        var key = variant=="B" ? "ad_impression_B" : "ad_impression_A";
                        byDay[day][key] = (int)byDay[day][key] + 1;
                    }else if (name=="checkout_click") byDay[day]["checkout_click"] = (int)byDay[day]["checkout_click"] + 1;
                    else if (name=="purchase") {
                        byDay[day]["purchase"] = (int)byDay[day]["purchase"] + 1;
                        long amount = 0; try { amount = el.TryGetProperty("amount", out var a) ? a.GetInt64() : 0; } catch { amount = 0; }
                        var variant = el.TryGetProperty("adVariant", out var v2) ? v2.GetString() : "";
                        byDay[day]["revenue_cents_total"] = (long)byDay[day]["revenue_cents_total"] + amount;
                        if (variant=="B") byDay[day]["revenue_cents_B"] = (long)byDay[day]["revenue_cents_B"] + amount; else byDay[day]["revenue_cents_A"] = (long)byDay[day]["revenue_cents_A"] + amount;
                    }
                }catch {}
            }
        }
        var sb = new StringBuilder();
        sb.AppendLine("date,page_view,ad_impression_A,ad_impression_B,checkout_click,purchase,revenue_cents_total,revenue_cents_A,revenue_cents_B,revenue_usd");
        foreach (var kv in byDay)
        {
            var d = kv.Value;
            var cents = (long)d["revenue_cents_total"];
            var usd = (cents/100.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
            sb.AppendLine(string.Join(",", new [] {
                kv.Key,
                d["page_view"].ToString(),
                d["ad_impression_A"].ToString(),
                d["ad_impression_B"].ToString(),
                d["checkout_click"].ToString(),
                d["purchase"].ToString(),
                cents.ToString(),
                ((long)d["revenue_cents_A"]).ToString(),
                ((long)d["revenue_cents_B"]).ToString(),
                usd
            }));
        }
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return Results.File(bytes, "text/csv", $"analytics.csv");
    }catch(Exception ex){
        return Results.BadRequest(ex.Message);
    }
});

// Cohorts CSV
app.MapGet("/api/admin/cohorts.csv", (HttpContext ctx, int days) =>
{
    try{
        var env = ctx.RequestServices.GetRequiredService<IHostEnvironment>();
        var path = Path.Combine(env.ContentRootPath, "App_Data", "analytics.jsonl");
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset start; DateTimeOffset end;
        var q = ctx.Request.Query;
        if (q.ContainsKey("from") && q.ContainsKey("to") && DateTimeOffset.TryParse(q["from"], out start) && DateTimeOffset.TryParse(q["to"], out end)) { }
        else { start = now.AddDays(-Math.Max(1, days)); end = now; }

        var dow = new Dictionary<string, Dictionary<string, object>>{
            {"Sun", new(){{"page_view",0},{"checkout_click",0},{"purchase",0},{"revenue_cents_total",0L}}},
            {"Mon", new(){{"page_view",0},{"checkout_click",0},{"purchase",0},{"revenue_cents_total",0L}}},
            {"Tue", new(){{"page_view",0},{"checkout_click",0},{"purchase",0},{"revenue_cents_total",0L}}},
            {"Wed", new(){{"page_view",0},{"checkout_click",0},{"purchase",0},{"revenue_cents_total",0L}}},
            {"Thu", new(){{"page_view",0},{"checkout_click",0},{"purchase",0},{"revenue_cents_total",0L}}},
            {"Fri", new(){{"page_view",0},{"checkout_click",0},{"purchase",0},{"revenue_cents_total",0L}}},
            {"Sat", new(){{"page_view",0},{"checkout_click",0},{"purchase",0},{"revenue_cents_total",0L}}}
        };
        var hour = new Dictionary<string, Dictionary<string, object>>();
        for(int h=0; h<24; h++) hour[h.ToString("D2")] = new(){{"page_view",0},{"checkout_click",0},{"purchase",0},{"revenue_cents_total",0L}};

        if (System.IO.File.Exists(path)){
            foreach(var line in System.IO.File.ReadAllLines(path)){
                if (string.IsNullOrWhiteSpace(line)) continue;
                try{
                    var el = JsonDocument.Parse(line).RootElement;
                    var ts = el.GetProperty("ts").GetDateTimeOffset();
                    if (ts < start || ts > end) continue;
                    var d3 = ts.UtcDateTime;
                    var dayOfWeek = d3.DayOfWeek.ToString().Substring(0,3);
                    var hourKey = d3.ToString("HH");
                    var name = el.GetProperty("name").GetString();
                    if (name=="page_view") { dow[dayOfWeek]["page_view"] = (int)dow[dayOfWeek]["page_view"] + 1; hour[hourKey]["page_view"] = (int)hour[hourKey]["page_view"] + 1; }
                    else if (name=="checkout_click") { dow[dayOfWeek]["checkout_click"] = (int)dow[dayOfWeek]["checkout_click"] + 1; hour[hourKey]["checkout_click"] = (int)hour[hourKey]["checkout_click"] + 1; }
                    else if (name=="purchase") {
                        dow[dayOfWeek]["purchase"] = (int)dow[dayOfWeek]["purchase"] + 1;
                        hour[hourKey]["purchase"] = (int)hour[hourKey]["purchase"] + 1;
                        long amount = 0; try { amount = el.TryGetProperty("amount", out var a) ? a.GetInt64() : 0; } catch { amount = 0; }
                        dow[dayOfWeek]["revenue_cents_total"] = (long)dow[dayOfWeek]["revenue_cents_total"] + amount;
                        hour[hourKey]["revenue_cents_total"] = (long)hour[hourKey]["revenue_cents_total"] + amount;
                    }
                }catch {}
            }
        }
        var sb = new StringBuilder();
        sb.AppendLine("group,key,page_view,checkout_click,purchase,revenue_cents_total,revenue_usd");
        foreach (var k in new []{"Sun","Mon","Tue","Wed","Thu","Fri","Sat"}){
            var d = dow[k];
            var cents = (long)d["revenue_cents_total"];
            var usd = (cents/100.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
            sb.AppendLine(string.Join(",", new []{"dow", k, d["page_view"].ToString(), d["checkout_click"].ToString(), d["purchase"].ToString(), cents.ToString(), usd }));
        }
        for(int h=0; h<24; h++){
            var key = h.ToString("D2");
            var d = hour[key];
            var cents = (long)d["revenue_cents_total"];
            var usd = (cents/100.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
            sb.AppendLine(string.Join(",", new []{"hour", key, d["page_view"].ToString(), d["checkout_click"].ToString(), d["purchase"].ToString(), cents.ToString(), usd }));
        }
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return Results.File(bytes, "text/csv", "analytics_cohorts.csv");
    }catch(Exception ex){
        return Results.BadRequest(ex.Message);
    }
});

// License activation endpoint (sets Pro cookie if signature valid and not expired)
app.MapPost("/api/license/activate", async (HttpContext ctx) => {
    using var sr = new StreamReader(ctx.Request.Body);
    var body = await sr.ReadToEndAsync();
    var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement;
    var token = doc.TryGetProperty("token", out var el) ? el.GetString() : null;
    var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
    var secret = Environment.GetEnvironmentVariable("License__Secret") ?? cfg["License:Secret"];
    if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(secret)) return Results.Ok(new { ok=false, error="Missing token or secret" });
    if (!VerifyToken(token, secret)) return Results.Ok(new { ok=false, error="Invalid token" });
    // naive exp check
    try{
        var payloadJson = Encoding.UTF8.GetString(Convert.FromBase64String(token.Split('.')[0].Replace('-','+').Replace('_','/') + new string('=', (4 - token.Split('.')[0].Length % 4) % 4)));
        var payload = JsonDocument.Parse(payloadJson).RootElement;
        var exp = payload.GetProperty("Exp").GetDateTimeOffset();
        if (exp < DateTimeOffset.UtcNow) return Results.Ok(new { ok=false, error="Expired" });
    }catch {}
    ctx.Response.Cookies.Append("AgentSQL_Pro", "1", new CookieOptions{ Expires = DateTimeOffset.UtcNow.AddYears(1), HttpOnly=false, IsEssential=true, SameSite=SameSiteMode.Lax });
    return Results.Ok(new { ok=true });
});

// Stripe: create checkout session
app.MapPost("/api/stripe/create-checkout-session", async (HttpContext ctx) =>
{
    var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
    var secretKey = Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY") ?? cfg["Stripe:SecretKey"];
    var priceId = Environment.GetEnvironmentVariable("STRIPE_PRICE_ID") ?? cfg["Stripe:PriceId"];
    if (string.IsNullOrWhiteSpace(secretKey) || string.IsNullOrWhiteSpace(priceId)) return Results.BadRequest("Stripe not configured");
    StripeConfiguration.ApiKey = secretKey;

    using var sr = new StreamReader(ctx.Request.Body);
    var body = await sr.ReadToEndAsync();
    var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement;
    var email = doc.TryGetProperty("email", out var el) ? el.GetString() : null;
    var variant = ctx.Request.Cookies["AgentSQL_AdVariant"] ?? "A";

    var domain = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    var options = new SessionCreateOptions
    {
        Mode = "payment",
        LineItems = new List<SessionLineItemOptions> {
            new SessionLineItemOptions{ Price = priceId, Quantity = 1 }
        },
        SuccessUrl = $"{domain}/license?session_id={{CHECKOUT_SESSION_ID}}",
        CancelUrl = $"{domain}/?canceled=true",
        Metadata = new Dictionary<string,string> { { "email", email ?? "" }, { "adVariant", variant } }
    };
    var service = new SessionService();
    var session = await service.CreateAsync(options);
    return Results.Ok(new { url = session.Url });
});

// Stripe: webhook
app.MapPost("/api/stripe/webhook", async (HttpContext ctx) =>
{
    var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
    var secretKey = Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY") ?? cfg["Stripe:SecretKey"];
    var webhookSecret = Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET") ?? cfg["Stripe:WebhookSecret"];
    var licenseSecret = Environment.GetEnvironmentVariable("License__Secret") ?? cfg["License:Secret"];
    if (string.IsNullOrWhiteSpace(secretKey) || string.IsNullOrWhiteSpace(webhookSecret)) return Results.BadRequest("Stripe not configured");
    StripeConfiguration.ApiKey = secretKey;

    var json = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    Event stripeEvent;
    try{
        stripeEvent = EventUtility.ConstructEvent(json, ctx.Request.Headers["Stripe-Signature"], webhookSecret);
    }catch (Exception e){
        return Results.BadRequest(e.Message);
    }

    if (stripeEvent.Type == "checkout.session.completed")
    {
        var session = stripeEvent.Data.Object as Session;
        var id = session?.Id ?? Guid.NewGuid().ToString("n");
        var email = session?.CustomerDetails?.Email ?? session?.Metadata.GetValueOrDefault("email") ?? "unknown@example.com";
        var exp = DateTimeOffset.UtcNow.AddYears(1);
        var token = CreateToken(email, "pro", exp, licenseSecret ?? "");

        var env = ctx.RequestServices.GetRequiredService<IHostEnvironment>();
        var dir = Path.Combine(env.ContentRootPath, "wwwroot", "licenses"); Directory.CreateDirectory(dir);
        await System.IO.File.WriteAllTextAsync(Path.Combine(dir, id + ".txt"), token);

        // Log purchase event (analytics)
        try{
            var logDir = Path.Combine(env.ContentRootPath, "App_Data"); Directory.CreateDirectory(logDir);
            var amt = session?.AmountTotal ?? 0; var cur = session?.Currency ?? "usd";
            var variant = session?.Metadata.GetValueOrDefault("adVariant") ?? "";
            var evt = new { ts = DateTimeOffset.UtcNow, name = "purchase", session = id, email = email, amount = amt, currency = cur, adVariant = variant };
            await System.IO.File.AppendAllTextAsync(Path.Combine(logDir, "analytics.jsonl"), JsonSerializer.Serialize(evt) + "\n");
        }catch {}

        // Email via SendGrid if configured
        try{
            var sgKey = Environment.GetEnvironmentVariable("SENDGRID_API_KEY") ?? cfg["SendGrid:ApiKey"];
            var fromEmail = cfg["SendGrid:FromEmail"];
            var fromName = cfg["SendGrid:FromName"] ?? "AgentSQL";
            if (!string.IsNullOrWhiteSpace(sgKey) && !string.IsNullOrWhiteSpace(fromEmail) && !string.IsNullOrWhiteSpace(email)){
                var client = new SendGridClient(sgKey);
                var from = new EmailAddress(fromEmail, fromName);
                var to = new EmailAddress(email);
                var subject = "Your AgentSQL Pro License";
                var domain = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
                var licenseUrl = domain + "/license?session_id=" + id;
                var plain = $"Thanks for your purchase!\n\nHere is your license key:\n{token}\n\nYou can also retrieve it here: {licenseUrl}\n\n— AgentSQL";
                var html = $"<p>Thanks for your purchase!</p><p><strong>License:</strong><br><code>{token}</code></p><p>You can also retrieve it here: <a href='{licenseUrl}'>{licenseUrl}</a></p><p>&mdash; AgentSQL</p>";
                var msg = MailHelper.CreateSingleEmail(from, to, subject, plain, html);
                await client.SendEmailAsync(msg);
            }
        }catch {}
    }

    return Results.Ok();
});

app.Run();
