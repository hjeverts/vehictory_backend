using System.Text;
using Vehictory.Api.Data;
using Vehictory.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// --- Database ---
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' ontbreekt.");
builder.Services.AddDbContext<VehictoryDbContext>(options => options.UseNpgsql(connectionString));

// --- JWT Auth ---
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddSingleton<EmailService>();
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admin"));

var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtKey = jwtSection["Key"] ?? throw new InvalidOperationException("Jwt:Key ontbreekt.");

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtSection["Issuer"],
        ValidAudience = jwtSection["Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
    };
});
builder.Services.AddAuthorization();

// --- CORS: Angular-frontend en Android-app benaderen deze API cross-origin ---
const string CorsPolicy = "AllowFrontend";
builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicy, policy =>
    {
        var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
    });
});

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddMemoryCache();

var app = builder.Build();

// Pas bij opstarten automatisch eventuele nieuwe EF Core-migraties toe.
// Zo hoeft update.sh geen aparte 'dotnet ef database update' stap uit te voeren.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<VehictoryDbContext>();
    db.Database.Migrate();

    var adminOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AdminOptions>>().Value;
    var bootstrapEmails = adminOptions.GetBootstrapEmails();
    if (bootstrapEmails.Count > 0)
    {
        var bootstrapUsers = db.Users.Where(user => bootstrapEmails.Contains(user.Email)).ToList();
        foreach (var user in bootstrapUsers) user.IsAdmin = true;
        db.SaveChanges();
    }

    // Eenmalige backfill: voertuigfoto's die vóór de resize/thumbnail-functionaliteit zijn
    // geüpload hebben nog geen FotoThumbnail. Zonder deze stap verdwijnen die foto's uit de
    // voertuigenlijst (die alleen de thumbnail toont) totdat de eigenaar opnieuw uploadt.
    // Idempotent: na de eerste run heeft elk voertuig met een Foto ook een FotoThumbnail,
    // dus de query levert bij volgende opstarts niets meer op.
    var vehiclesToBackfill = db.Vehicles.Where(v => v.Foto != null && v.FotoThumbnail == null).ToList();
    if (vehiclesToBackfill.Count > 0)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        foreach (var vehicle in vehiclesToBackfill)
        {
            try
            {
                var (content, contentType, thumbnail) = Vehictory.Api.Controllers.AuthController.ProcessImage(
                    vehicle.Foto!,
                    Vehictory.Api.Controllers.VehiclesController.PhotoMaxDimension,
                    Vehictory.Api.Controllers.VehiclesController.PhotoThumbnailDimension);
                vehicle.Foto = content;
                vehicle.FotoContentType = contentType;
                vehicle.FotoThumbnail = thumbnail;
            }
            catch (SixLabors.ImageSharp.ImageFormatException exception)
            {
                logger.LogWarning(exception,
                    "Kon bestaande foto van voertuig {VehicleId} niet backfillen naar thumbnail, sla over.",
                    vehicle.Id);
            }
        }
        db.SaveChanges();
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Verzoeken passeren twee interne, door onszelf beheerde proxy-hops voor ze de API
// bereiken (host-nginx -> frontend-container-nginx, zie vehictory_frontend/nginx.conf),
// vandaar ForwardLimit 2. De API is niet rechtstreeks vanaf het publieke internet
// bereikbaar (poort alleen aan 127.0.0.1 gebonden), dus deze headers zijn hier te
// vertrouwen; zonder deze middleware ziet de API voor elk verzoek hetzelfde interne
// proxy-adres in plaats van het echte client-IP.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 2,
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

app.UseHttpsRedirection();
app.UseCors(CorsPolicy);
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();
