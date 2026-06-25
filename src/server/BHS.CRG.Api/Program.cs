using System.Text;
using System.Text.Json.Serialization;
using BHS.CRG.Api.Endpoints.Attachments;
using BHS.CRG.Api.Endpoints.Auth;
using BHS.CRG.Api.Endpoints.Backup;
using BHS.CRG.Api.Endpoints.Catalog;
using BHS.CRG.Api.Endpoints.DataSets;
using BHS.CRG.Api.Endpoints.Documents;
using BHS.CRG.Api.Endpoints.Generation;
using BHS.CRG.Api.Endpoints.Templates;
using BHS.CRG.Api.Hubs;
using BHS.CRG.Application.Catalog;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Plugins;
using BHS.CRG.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Minio;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.ConfigureHttpJsonOptions(opt =>
    opt.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(cfg.GetConnectionString("Postgres")));

// ── Identity ──────────────────────────────────────────────────────────────────
builder.Services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(opt =>
    {
        opt.Password.RequireDigit = false;
        opt.Password.RequireNonAlphanumeric = false;
        opt.Password.RequiredLength = 6;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

// ── JWT ───────────────────────────────────────────────────────────────────────
var jwtSection = cfg.GetSection("Jwt");
builder.Services.AddAuthentication(opt =>
    {
        opt.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        opt.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwtSection["Audience"],
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtSection["Key"]!)),
        };
        opt.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) &&
                    ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

// ── MediatR ───────────────────────────────────────────────────────────────────
builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssemblies(
        typeof(CatalogHandlers).Assembly,
        typeof(GenerateDocumentHandler).Assembly));

// ── Repositories ──────────────────────────────────────────────────────────────
builder.Services.AddScoped<IRepository<CatalogEntity>, Repository<CatalogEntity>>();
builder.Services.AddScoped<IRepository<PrimitiveType>, Repository<PrimitiveType>>();
builder.Services.AddScoped<IRepository<DocumentType>, Repository<DocumentType>>();
builder.Services.AddScoped<IRepository<Construction>, ConstructionRepository>();
builder.Services.AddScoped<IRepository<Section>, Repository<Section>>();
builder.Services.AddScoped<IRepository<DocumentSet>, DocumentSetRepository>();
builder.Services.AddScoped<IRepository<DocumentInstance>, DocumentInstanceRepository>();
builder.Services.AddScoped<IRepository<Template>, Repository<Template>>();
builder.Services.AddScoped<IRepository<CommonDataEntry>, Repository<CommonDataEntry>>();
builder.Services.AddScoped<IRepository<GeneratedFile>, Repository<GeneratedFile>>();
builder.Services.AddScoped<IRepository<TypstUserLib>, Repository<TypstUserLib>>();

// ── Backup ────────────────────────────────────────────────────────────────────
builder.Services.AddScoped<BackupService>();

// ── Generation ────────────────────────────────────────────────────────────────
builder.Services.AddScoped<IEntityResolver, EntityResolver>();
builder.Services.AddScoped<IMetadataExtractor, MetadataExtractor>();
builder.Services.AddScoped<IDataSetResolver, DataSetResolver>();
builder.Services.AddSingleton<TypstGenerator>();
builder.Services.AddSingleton<IDocumentGeneratorFactory, DocumentGeneratorFactory>();

// ── DataSets ──────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IDataSetParser, CsvDataSetParser>();
builder.Services.AddSingleton<IDataSetParser, XlsxDataSetParser>();
builder.Services.AddSingleton<IDataSetParser, XmlDataSetParser>();
builder.Services.AddSingleton<IDataSetParser, JsonDataSetParser>();
builder.Services.AddSingleton<IDataSetParser, ZipDataSetParser>();
builder.Services.AddSingleton<DataSetParserFactory>();
builder.Services.AddScoped<IDataSetService, DataSetService>();

// ── MinIO ─────────────────────────────────────────────────────────────────────
var blobOpts = cfg.GetSection("BlobStorage").Get<BlobStorageOptions>() ?? new();
builder.Services.AddSingleton(blobOpts);
builder.Services.AddMinio(c => c
    .WithEndpoint(blobOpts.Endpoint)
    .WithCredentials(blobOpts.AccessKey, blobOpts.SecretKey)
    .WithSSL(blobOpts.UseSSL));
builder.Services.AddSingleton<IBlobStorage, MinIOBlobStorage>();

// ── Plugins ───────────────────────────────────────────────────────────────────
var pluginOpts = cfg.GetSection("Plugins").Get<PluginHostOptions>() ?? new();
builder.Services.AddSingleton(pluginOpts);
builder.Services.AddSingleton<IPluginHost, PluginHost>();

// ── SignalR + CORS ────────────────────────────────────────────────────────────
builder.Services.AddSignalR();
builder.Services.AddCors(opt => opt.AddDefaultPolicy(p =>
    p.WithOrigins(cfg["AllowedOrigins"]?.Split(',') ?? ["http://localhost:5173"])
     .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.AddOpenApi();

var app = builder.Build();

// ExcelDataReader требует регистрации кодировок для .xls файлов
System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseExceptionHandler(exApp => exApp.Run(async ctx =>
{
    var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var ex = feature?.Error;
    ctx.Response.ContentType = "application/json";
    ctx.Response.StatusCode = ex switch
    {
        KeyNotFoundException => 404,
        UnauthorizedAccessException => 403,
        ArgumentException => 400,
        _ => 500,
    };
    await ctx.Response.WriteAsJsonAsync(new { error = ex?.Message ?? "Internal server error" });
}));

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapAttachmentEndpoints();
app.MapAuthEndpoints();
app.MapBackupEndpoints();
app.MapCatalogEndpoints();
app.MapPrimitiveTypeEndpoints();
app.MapDocumentTypeEndpoints();
app.MapCommonDataEndpoints();
app.MapTemplateEndpoints();
app.MapTypstUserLibEndpoints();
app.MapPrintFormEndpoints();
app.MapDocumentSetEndpoints();
app.MapGenerationEndpoints();
app.MapDataSetEndpoints();
app.MapDataSetBindingEndpoints();
app.MapDataSetBindingTemplateEndpoints();
app.MapHub<GenerationHub>("/hubs/generation");

app.Run();
