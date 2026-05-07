using System.Threading.RateLimiting;
using Cocorra.API.Hubs;
using Cocorra.API.Seeder;
using Cocorra.BLL.Services.AdminService;
using Cocorra.BLL.Services.Auth;
using Cocorra.BLL.Services.AuthServices;
using Cocorra.BLL.Services.ChatService;
using Cocorra.BLL.Services.Email;
using Cocorra.BLL.Services.FriendService;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.BLL.Services.OTPService;
using Cocorra.BLL.Services.ProfileService;
using Cocorra.BLL.Services.RolesService;
using Cocorra.BLL.Services.RoomService;
using Cocorra.BLL.Services.SupportService;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.Data;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.FriendRepository;
using Cocorra.DAL.Repository.GenericRepository;
using Cocorra.DAL.Repository.MessageRepository;
using Cocorra.DAL.Repository.NotificationRepository;
using Cocorra.DAL.Repository.RoomRepository;
using Cocorra.DAL.Repository.SupportRepository;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;
using System.Security.Claims;

using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;

var builder = WebApplication.CreateBuilder(args);

// Initialize Firebase Admin SDK
FirebaseApp.Create(new AppOptions
{
    Credential = GoogleCredential.FromFile("firebase-config.json")
});

var jwtSettings = builder.Configuration.GetSection("JWTSetting");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Cocorra API",
        Version = "v1",
        Description = "API for Cocorra Application"
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Add JWT Here"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] {}
        }
    });
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPolicy", builder =>
        builder
            .SetIsOriginAllowed(_ => true)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials());
});

#region AddScopedServices
builder.Services.AddSignalR(options =>
{
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.MaximumReceiveMessageSize = 32 * 1024; // 32KB max per message
    options.StreamBufferCapacity = 10;
});
builder.Services.AddScoped<IUploadVoice, UploadVoice>();
builder.Services.AddScoped<IRoomRepository, RoomRepository>();
builder.Services.AddScoped<IRoomService, RoomService>();
builder.Services.AddScoped<IAuthServices, AuthServices>();
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.AddScoped<IRolesService, RolesService>();
builder.Services.AddScoped<IFriendRepository, FriendRepository>();
builder.Services.AddScoped<IFriendService, FriendService>();
builder.Services.AddScoped<INotificationRepository, NotificationRepository>();
builder.Services.AddScoped<IPushNotificationService, PushNotificationService>();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<IMessageRepository, MessageRepository>();
builder.Services.AddScoped<IUploadImage, UploadImage>();
builder.Services.AddScoped<IProfileService, ProfileService>();
builder.Services.AddScoped<Cocorra.DAL.Repository.UserRepository.IUserRepository, Cocorra.DAL.Repository.UserRepository.UserRepository>();
builder.Services.AddScoped<ISupportRepository, SupportRepository>();
builder.Services.AddScoped<ISupportService, SupportService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IOTPService, OTPService>();
builder.Services.AddScoped<Cocorra.DAL.Repository.UserBlockRepository.IUserBlockRepository, Cocorra.DAL.Repository.UserBlockRepository.UserBlockRepository>();
builder.Services.AddScoped<Cocorra.BLL.Services.BlockService.IBlockService, Cocorra.BLL.Services.BlockService.BlockService>();
builder.Services.AddScoped(typeof(IGenericRepositoryAsync<>), typeof(GenericRepositoryAsync<>));
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
    cfg.RegisterServicesFromAssembly(typeof(Cocorra.BLL.Events.UserRequestedToJoinRoomEvent).Assembly);
});
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<Cocorra.BLL.Services.RealTimeNotifier.IRealTimeNotifier, Cocorra.API.Services.SignalRNotifier>();
#endregion



#region AddDbContext
builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"),
    b =>
    {
        b.MigrationsAssembly("Cocorra.DAL");
        b.EnableRetryOnFailure(3, TimeSpan.FromSeconds(3), null);
    });
});
#endregion





#region AddIdentity
builder.Services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(op =>
{
    op.User.RequireUniqueEmail = true;
    op.Password.RequireLowercase = true;
    op.Password.RequireUppercase = true;
    op.Password.RequireNonAlphanumeric = true;
    op.Password.RequireDigit = true;
    op.Password.RequiredLength = 8;
    op.Lockout.AllowedForNewUsers = false;
    op.Lockout.MaxFailedAccessAttempts = 5;
    op.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();
#endregion




#region Authentication & JWT
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.SaveToken = true;
    options.RequireHttpsMetadata = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtSettings["ValidIssuer"],
        ValidAudience = jwtSettings["ValidAudience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings["securityKey"]!))
    };
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;

            if (!string.IsNullOrEmpty(accessToken) &&
                (path.StartsWithSegments("/hubs/chat") || path.StartsWithSegments("/hubs/rooms") || path.StartsWithSegments("/hubs/support")))
            {
                context.Token = accessToken;
            }
            return Task.CompletedTask;
        },
        OnTokenValidated = async context =>
        {
            var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
            var userId = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (!string.IsNullOrEmpty(userId))
            {
                var user = await userManager.FindByIdAsync(userId);
                if (user != null && await userManager.IsLockedOutAsync(user))
                {
                    context.Fail("User is locked out.");
                }
            }
        }
    };
});

// SECURITY: Custom Authorization Policies for Voice Verification flow.
// Forcing VerificationStatus=Active as the default policy is the strictly better security posture.
// Any existing Active users with old tokens (missing the claim) will need to re-login once.
// This is acceptable because JWT expiry is 1 day, so the migration window is at most 24 hours.
builder.Services.AddAuthorization(options =>
{
    // Default policy: Requires VerificationStatus=Active.
    // Every bare [Authorize] attribute will enforce this automatically.
    options.DefaultPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim("VerificationStatus", "Active")
        .Build();

    // Named policy for verification-stage endpoints (ReRecord, etc.).
    // Allows Pending, ReRecord, AND Active users — so admins/active users can also reach it if needed.
    options.AddPolicy("VerificationOnly", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("VerificationStatus", "Pending", "ReRecord", "Active");
    });
});
#endregion

#region Rate Limiting & Exception Handling
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? httpContext.Request.Headers.Host.ToString(),
            factory: partition => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 100, // max 100 requests per minute per IP
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1)
            }));
});
#endregion



var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

    try
    {
        await RoleSeeder.SeedRolesAsync(services);

        await IdentitySeeder.SeedAsync(userManager, roleManager, app.Configuration);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"An error occurred while seeding the database: {ex.Message}");
    }
}
app.UseHttpsRedirection();
var contentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".m4a"] = "audio/mp4";
contentTypeProvider.Mappings[".aac"] = "audio/aac";

app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypeProvider,
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.Append("Cache-Control", "public,max-age=604800");
    }
});
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            var response = new
            {
                statusCode = 500,
                succeeded = false,
                message = "An unexpected internal server error occurred.",
                errors = new List<string>(),
                data = (object?)null
            };
            await context.Response.WriteAsJsonAsync(response);
        });
    });
}

app.UseWebSockets();
app.UseRateLimiter();
app.UseCors("CorsPolicy");

app.UseAuthentication();
app.UseAuthorization();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Cocorra API v1");
});

app.MapHub<RoomHub>("/hubs/rooms");
app.MapHub<ChatHub>("/hubs/chat");
app.MapHub<Cocorra.API.Hubs.SupportHub>("/hubs/support");
app.MapControllers();
app.MapGet("/", () => "Welcome to Cocorra API - System is Running Successfully! ");

app.Run();