using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using MarketHub.Shared;
using MarketHub.Shared.Infrastructure;
using MarketHub.Modules.Identity;
using MarketHub.Modules.Catalog;
using MarketHub.Modules.Sales;
using MarketHub.Modules.Payments;
using MarketHub.Modules.Finance;
using MarketHub.Modules.Notifications;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, cfg) => cfg
        .MinimumLevel.Information()
        .ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File("logs/mh-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30));

    // ---------- Shared ----------
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUser, CurrentUser>();
    builder.Services.AddScoped<IFileStorage>(sp =>
        (builder.Configuration["FileStorage:Provider"] ?? "Local") == "Minio"
            ? throw new GatewayNotConfiguredException("MinIO storage adapter (register MinioFileStorage in production)")
            : new LocalFileStorage(builder.Configuration));

    // ---------- Modules (modular monolith) ----------
    builder.Services.AddMediatR(cfg =>
    {
        cfg.RegisterServicesFromAssembly(typeof(MarketHub.Shared.Core).Assembly);
        cfg.RegisterServicesFromAssembly(typeof(IdentityModule).Assembly);
        cfg.RegisterServicesFromAssembly(typeof(CatalogModule).Assembly);
        cfg.RegisterServicesFromAssembly(typeof(SalesModule).Assembly);
        cfg.RegisterServicesFromAssembly(typeof(PaymentsModule).Assembly);
        cfg.RegisterServicesFromAssembly(typeof(FinanceModule).Assembly);
        cfg.RegisterServicesFromAssembly(typeof(NotificationsModule).Assembly);
    });

    builder.Services.AddIdentityModule(builder.Configuration);
    builder.Services.AddCatalogModule(builder.Configuration);
    builder.Services.AddSalesModule(builder.Configuration);
    builder.Services.AddPaymentsModule(builder.Configuration);
    builder.Services.AddFinanceModule(builder.Configuration);
    builder.Services.AddNotificationsModule(builder.Configuration);
    builder.Services.AddHostedService<OutboxProcessor>();
    builder.Services.AddMarketHubQuartz();

    // ---------- AuthN / AuthZ ----------
    var jwt = builder.Configuration.GetSection("Jwt");
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwt["Issuer"] ?? "MarketHub",
                ValidateAudience = true,
                ValidAudience = jwt["Audience"] ?? "MarketHubClients",
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"] ?? throw new InvalidOperationException("Jwt:Key missing"))),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
                RoleClaimType = "role",
                NameClaimType = "sub",
            };
        });

    builder.Services.AddAuthorizationBuilder()
        .AddPolicy("Admin", p => p.RequireRole("Admin"))
        .AddPolicy("Vendor", p => p.RequireRole("Vendor"))
        .AddPolicy("Buyer", p => p.RequireRole("Buyer"))
        .AddPolicy("VendorOrAdmin", p => p.RequireRole("Vendor", "Admin"))
        // back-office staff management: only the SuperAdmin sub-role may patch staff accounts
        .AddPolicy("SuperAdminOnly", p => p.RequireRole("Admin").RequireAssertion(ctx =>
            string.Equals(ctx.User.FindFirst("adminRole")?.Value, "SuperAdmin", StringComparison.OrdinalIgnoreCase)));

    // ---------- Rate limiting ----------
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.AddPolicy("api", ctx => Microsoft.AspNetCore.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "anon", _ => new Microsoft.AspNetCore.RateLimiting.FixedWindowRateLimiterOptions
            { PermitLimit = 100, Window = TimeSpan.FromMinutes(1) }));
        o.AddPolicy("auth", ctx => Microsoft.AspNetCore.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "anon", _ => new Microsoft.AspNetCore.RateLimiting.FixedWindowRateLimiterOptions
            { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    });

    // ---------- CORS / Health / Swagger ----------
    var origins = (builder.Configuration["Cors:Origins"] ?? "http://localhost:3000").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
    builder.Services.AddHealthChecks().AddNpgSql(builder.Configuration.GetConnectionString("Default")!);
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "MarketHub API", Version = "v1", Description = "Ethiopian multivendor marketplace — modular monolith (.NET 9)" });
        c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Name = "Authorization", Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
            In = Microsoft.OpenApi.Models.ParameterLocation.Header, Description = "JWT access token",
        });
        c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
        {
            { new Microsoft.OpenApi.Models.OpenApiSecurityScheme { Reference = new Microsoft.OpenApi.Models.OpenApiReference { Id = "Bearer", Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme } }, Array.Empty<string>() },
        });
    });

    var app = builder.Build();

    // ---------- Middleware pipeline ----------
    app.UseSerilogRequestLogging();
    app.UseMiddleware<GlobalExceptionMiddleware>();
    app.UseRateLimiter();
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "MarketHub API v1"));
        await DatabaseSeeder.MigrateAndSeedAsync(app); // dev only: auto-migrate + idempotent seed
    }
    app.UseCors();
    app.UseAuthentication();
    app.UseAuthorization();

    // ---------- Endpoints ----------
    IdentityEndpoints.MapEndpoints(app);
    CatalogEndpoints.MapEndpoints(app);
    SalesEndpoints.MapEndpoints(app);
    PaymentsEndpoints.MapEndpoints(app);
    FinanceEndpoints.MapEndpoints(app);
    NotificationsEndpoints.MapEndpoints(app);
    app.MapHealthChecks("/health");

    app.Logger.LogInformation("MarketHub API started (env {Env})", app.Environment.EnvironmentName);
    await app.RunAsync();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "MarketHub API terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
