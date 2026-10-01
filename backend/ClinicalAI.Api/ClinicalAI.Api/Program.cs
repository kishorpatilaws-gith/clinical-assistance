using Amazon.BedrockRuntime;
using ClinicalAI.Api.Application.Services;
using ClinicalAI.Api.Domain.Entities;
using ClinicalAI.Api.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Amazon.Runtime;
using Amazon.S3;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// ... after builder is created
builder.Services.AddDbContext<ClinicalDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Supabase")));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register Bedrock Runtime client
builder.Services.AddSingleton<IAmazonBedrockRuntime>(sp =>
{
    return new AmazonBedrockRuntimeClient(Amazon.RegionEndpoint.USEast1);
});

// Register the R2 client. R2 speaks the S3 API, so this is a standard S3 client
// pointed at the account's R2 endpoint.
builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var cfg = builder.Configuration;
    string Required(string key) => cfg[key]
        ?? throw new InvalidOperationException(
            $"{key} is not configured. Set it with: dotnet user-secrets set \"{key}\" \"<value>\"");

    var s3Config = new AmazonS3Config
    {
        ServiceURL = $"https://{Required("R2:AccountId")}.r2.cloudflarestorage.com",
        ForcePathStyle = true,
        AuthenticationRegion = "auto",

        // R2 does not implement the newer checksum headers the v4 SDK attaches to
        // every request by default; limit them to operations that actually require
        // one, or uploads fail with a signature/checksum error.
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
    };

    return new AmazonS3Client(Required("R2:AccessKey"), Required("R2:SecretKey"), s3Config);
});

builder.Services.AddScoped<IR2StorageService, R2StorageService>();

// Typed client for the Python document processor (a separate process on :8000).
builder.Services.AddHttpClient<IDocumentProcessorClient, DocumentProcessorClient>(client =>
{
    var cfg = builder.Configuration.GetSection("DocumentProcessor");
    client.BaseAddress = new Uri(cfg["BaseUrl"] ?? "http://localhost:8000");
    // The client applies its own per-call timeouts (TimeoutSeconds, IndexTimeoutSeconds).
    client.Timeout = Timeout.InfiniteTimeSpan;
});

// Uploads are indexed in the background: the request stores the file and queues it.
builder.Services.AddSingleton<IDocumentIndexingQueue, DocumentIndexingQueue>();
builder.Services.AddScoped<IDocumentIndexer, DocumentIndexer>();
builder.Services.AddHostedService<DocumentIndexingWorker>();

builder.Services.AddScoped<IBedrockService, BedrockService>();

builder.Services.AddScoped<IPatientSummaryService, PatientSummaryService>();
builder.Services.AddScoped<IPiiRedactionService, PiiRedactionService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IRagService, RagService>();
// CORS for local React dev
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactApp", policy =>
        policy.WithOrigins("http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod());
});

// Configure JWT authentication before building the app
var jwtSecret = builder.Configuration["Supabase:JwtSecret"]!;
var supabaseUrl = builder.Configuration["Supabase:Url"]!;

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    // Validate Supabase-issued access tokens against the project's auth endpoint.
    options.Authority = $"{supabaseUrl}/auth/v1";
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = $"{supabaseUrl}/auth/v1",
        ValidateAudience = true,
        ValidAudience = "authenticated",
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true
    };

    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = context =>
        {
            // Promote the Supabase app_role metadata value to a .NET role claim,
            // so [Authorize(Roles = "...")] checks can use it.
            var appMetadataClaim = context.Principal?.FindFirst("app_metadata")?.Value;
            if (!string.IsNullOrEmpty(appMetadataClaim))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(appMetadataClaim);
                if (doc.RootElement.TryGetProperty("app_role", out var roleElement))
                {
                    var role = roleElement.GetString();
                    if (!string.IsNullOrEmpty(role))
                    {
                        var identity = (System.Security.Claims.ClaimsIdentity?)context.Principal?.Identity;
                        identity?.AddClaim(new System.Security.Claims.Claim(
                            System.Security.Claims.ClaimTypes.Role, role));
                    }
                }
            }
            return Task.CompletedTask;
        }
    };
});

// Enables authorization checks after authentication has validated the token.
builder.Services.AddAuthorization();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ClinicalDbContext>();
    db.Database.Migrate();

    if (!db.Patients.Any())
    {
        db.Patients.AddRange(
            new Patient
            {
                FirstName = "Jordan",
                LastName = "Rivera",
                DateOfBirth = new DateOnly(1985, 3, 12),
                Sex = "Male",
                Allergies = "Penicillin",
                CurrentMedications = "Lisinopril 10mg daily",
                Symptoms = "Intermittent chest tightness, mild shortness of breath on exertion for 2 weeks",
                MedicalHistory = "Hypertension, former smoker (quit 2019)"
            },
            new Patient
            {
                FirstName = "Priya",
                LastName = "Nair",
                DateOfBirth = new DateOnly(1993, 7, 28),
                Sex = "Female",
                Allergies = "None known",
                CurrentMedications = "Levothyroxine 75mcg daily",
                Symptoms = "Fatigue, unintentional weight gain, cold intolerance over past month",
                MedicalHistory = "Hypothyroidism diagnosed 2021"
            }
        );
        db.SaveChanges();
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowReactApp");
//app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();