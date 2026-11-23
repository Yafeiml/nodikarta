using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;
using ProjectHub.Core;

var builder = WebApplication.CreateBuilder(Array.Empty<string>());
// The framework's normal request-start line contains the raw search query string.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
var connectionString = builder.Configuration.GetConnectionString("Core")
    ?? throw new InvalidOperationException("缺少 ConnectionStrings__Core，请配置 ProjectHub 专用的 PostgreSQL 数据库。");
await using var dataSource = NpgsqlDataSource.Create(connectionString);
var requestContext = new HttpContextAccessor();
builder.Services.AddSingleton<IHttpContextAccessor>(requestContext);
var store = new CoreStore(dataSource, requestContext);
if (args.Length != 0)
{
    if (args is ["reset-password"])
        return await PasswordReset.RunAsync(store);
    Console.Error.WriteLine("支持的命令为 reset-password；直接启动时运行 Core 网页。");
    return 2;
}

var localHttp = builder.Configuration.GetValue<bool>("ProjectHub:LocalHttp");
var cookieSecurity = localHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
var publicAddress = builder.Configuration["ProjectHub:PublicUrl"];
if (!string.IsNullOrWhiteSpace(publicAddress) &&
    (!Uri.TryCreate(publicAddress, UriKind.Absolute, out var address) ||
     !(address.Scheme == "https" || (localHttp && address.Scheme == "http")) ||
     address.UserInfo.Length > 0 || address.Query.Length > 0 || address.Fragment.Length > 0 || address.AbsolutePath != "/"))
    throw new InvalidOperationException("ProjectHub__PublicUrl 应为 HTTPS 根地址；启用 ProjectHub__LocalHttp 后也可使用 HTTP 根地址。");

builder.Services.AddSingleton(store);
builder.Services.AddHostedService<ActivityMaintenance>();
var keyDirectory = builder.Configuration["ProjectHub:KeyDirectory"];
var protection = builder.Services.AddDataProtection().SetApplicationName("ProjectHub.Core");
if (!string.IsNullOrWhiteSpace(keyDirectory))
    protection.PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));

var proxy = builder.Configuration["ProjectHub:TrustedProxy"];
if (!string.IsNullOrWhiteSpace(proxy))
{
    var trustedAddress = IPAddress.Parse(proxy);
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(trustedAddress);
    });
}
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.Cookie.Name = "ProjectHub.Admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = cookieSecurity;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
        options.Events.OnValidatePrincipal = async context =>
        {
            var principal = context.Principal!;
            if (await store.SessionValidAsync(AdminSession.OwnerId(principal), AdminSession.Version(principal)))
                return;
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync();
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options => options.Cookie.SecurePolicy = cookieSecurity);
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Setup");
    options.Conventions.AllowAnonymousToPage("/Login");
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var request = context.Request;
        var pairing = request.Path.StartsWithSegments("/v1/device-pairings") ||
            request.Path.StartsWithSegments("/v1/tokens/refresh");
        var client = context.Connection.RemoteIpAddress?.ToString() ?? "local";
        if (HttpMethods.IsGet(request.Method) && request.Path.StartsWithSegments("/login"))
            return RateLimitPartition.GetFixedWindowLimiter("challenge:" + client,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 90, Window = TimeSpan.FromMinutes(1) });
        if (!HttpMethods.IsPost(request.Method) || !(pairing ||
            request.Path.StartsWithSegments("/login") || request.Path.StartsWithSegments("/setup") ||
            request.Path.StartsWithSegments("/account/password")))
            return RateLimitPartition.GetNoLimiter("other");
        return RateLimitPartition.GetFixedWindowLimiter((pairing ? "device:" : "admin:") + client,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = pairing ? 60 : 15, Window = TimeSpan.FromMinutes(1) });
    });
});

var app = builder.Build();
await store.InitializeAsync();
var installation = await store.IssueSetupCodeAsync();
if (installation is { } setup)
{
    Console.WriteLine($"ProjectHub 首次设置：{(string.IsNullOrWhiteSpace(publicAddress) ? "请用浏览器打开服务器 IP 和映射端口，或已配置的域名。" : publicAddress)}");
    Console.WriteLine($"Setup code / 首次安装码: {setup.Code}");
    Console.WriteLine($"失效时间: {setup.ExpiresAt:O}");
}
if (!string.IsNullOrWhiteSpace(proxy))
    app.UseForwardedHeaders();
app.UseWhen(context => !context.Request.Path.StartsWithSegments("/v1"),
    web => web.UseRequestLocalization(WebText.LanguageOptions()));
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    context.Response.StatusCode = 503;
    context.Response.ContentType = "text/plain; charset=utf-8";
    await context.Response.WriteAsync(WebText.T("Core 暂时无法处理请求，请检查服务和数据库状态。", "Core is temporarily unavailable. Check the service and database status."));
}));
app.Use(async (context, next) =>
{
    if (!localHttp && !context.Request.IsHttps)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync(WebText.T("请通过配置的 HTTPS 入口访问 Core。", "Use the configured HTTPS address to access Core."));
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'; form-action 'self'";
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseWhen(context => context.Request.Path.StartsWithSegments("/v1") || context.Request.Path.StartsWithSegments("/projects"),
    branch => branch.Use((context, next) => store.ObserveRequestAsync(context, next)));
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/", async (HttpContext context) =>
{
    var path = context.User.Identity?.IsAuthenticated == true ? "/projects" :
        await store.IsInstalledAsync() ? "/login" : "/setup";
    context.Response.Redirect(path);
});
app.MapRazorPages();
app.MapDeviceApi();
await app.RunAsync();
return 0;

namespace ProjectHub.Core
{
    internal static class AdminSession
    {
        public static Guid OwnerId(ClaimsPrincipal principal) =>
            Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

        public static long Version(ClaimsPrincipal principal) =>
            long.Parse(principal.FindFirstValue("session_version")!, CultureInfo.InvariantCulture);

        public static Task SignInAsync(HttpContext context, AdminIdentity admin)
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
                new Claim(ClaimTypes.Name, admin.Username),
                new Claim("session_version", admin.SessionVersion.ToString(CultureInfo.InvariantCulture))
            }, CookieAuthenticationDefaults.AuthenticationScheme);
            return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        }
    }
}
