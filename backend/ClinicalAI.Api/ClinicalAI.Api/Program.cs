using Amazon.BedrockRuntime;
using ClinicalAI.Api.Application.Services;
using ClinicalAI.Api.Domain.Entities;
using ClinicalAI.Api.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
//using Amazon.S3;
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

//// Register R2 client (S3-compatible)
//builder.Services.AddSingleton<IAmazonS3>(sp =>
//{
//    var config = builder.Configuration;
//    var accountId = config["R2:AccountId"];
//    var s3Config = new AmazonS3Config
//    {
//        ServiceURL = $"https://{accountId}.r2.cloudflarestorage.com",
//        ForcePathStyle = true
//    };
//    return new AmazonS3Client(config["R2:AccessKey"], config["R2:SecretKey"], s3Config);
//});

//builder.Services.AddScoped<IR2StorageService, R2StorageService>();

builder.Services.AddScoped<IBedrockService, BedrockService>();

builder.Services.AddScoped<IPatientSummaryService, PatientSummaryService>();
builder.Services.AddScoped<IPiiRedactionService, PiiRedactionService>();
builder.Services.AddScoped<IAuditService, AuditService>();
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