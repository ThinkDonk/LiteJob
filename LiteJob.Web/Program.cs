using LiteJob.Core.Db;
using LiteJob.Core.Db.Migrations;
using LiteJob.Core.Security;
using LiteJob.Web.Bootstrap;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddRazorPages();

builder.Services.AddSingleton<SqliteConnectionFactory>(_ =>
    new SqliteConnectionFactory(builder.Configuration.GetConnectionString("sqlite")
        ?? throw new InvalidOperationException("缺少连接串 ConnectionStrings:sqlite")));
builder.Services.AddSingleton<LoginThrottler>();
builder.Services.AddSingleton<MigrationRunner>();
builder.Services.AddHostedService<AdminSeeder>();

var jwtKey = builder.Configuration["Security:JwtKey"]
    ?? throw new InvalidOperationException("缺少配置 Security:JwtKey");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var authorization = context.Request.Headers.Authorization.ToString();
                if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.CompletedTask;
                }
                context.Token = context.Request.Cookies["lj_token"];
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

Directory.CreateDirectory("data");

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<MigrationRunner>().RunAsync();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();

app.Run();
