using LiteJob.Core.Db;
using LiteJob.Core.Db.Migrations;
using LiteJob.Core.Executors;
using LiteJob.Core.Notify;
using LiteJob.Core.Repositories;
using LiteJob.Core.Scheduling;
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

builder.Services.AddSingleton<CryptoService>(_ => new CryptoService(builder.Configuration["Security:AesKey"]
    ?? throw new InvalidOperationException("缺少配置 Security:AesKey")));
builder.Services.AddSingleton<CredentialRepository>();
builder.Services.AddSingleton<ConfigRepository>();
builder.Services.AddSingleton<CredentialResolver>();
builder.Services.AddHttpClient("executor", client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<HttpAuthHelper>(sp =>
    new HttpAuthHelper(sp.GetRequiredService<IHttpClientFactory>().CreateClient("executor")));
builder.Services.AddSingleton<ITaskExecutor>(sp =>
    new HttpTaskExecutor(sp.GetRequiredService<IHttpClientFactory>().CreateClient("executor"),
        sp.GetRequiredService<CredentialResolver>(), sp.GetRequiredService<HttpAuthHelper>()));
builder.Services.AddHttpClient<MailAttachmentCollector>(client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<ITaskExecutor>(sp =>
    new MailTaskExecutor(sp.GetRequiredService<MailAttachmentCollector>(),
        sp.GetRequiredService<ConfigRepository>(), sp.GetRequiredService<CryptoService>(),
        sp.GetRequiredService<CredentialResolver>(), sp.GetRequiredService<ILogger<MailTaskExecutor>>()));
builder.Services.AddSingleton<INotifyDispatcher, RealNotifyDispatcher>();
builder.Services.AddSingleton<NotifyChannelRepository>();
builder.Services.AddHttpClient("notify", client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<NotifySender>(sp =>
    new NotifySender(sp.GetRequiredService<IHttpClientFactory>().CreateClient("notify"),
        sp.GetRequiredService<NotifyChannelRepository>(),
        sp.GetRequiredService<ILogger<NotifySender>>()));
builder.Services.AddSingleton<ExecutorRegistry>();
builder.Services.AddSingleton<TaskRepository>();
builder.Services.AddSingleton<RunRepository>();
builder.Services.AddSingleton<ExecutionPipeline>();
builder.Services.AddSingleton<SimpleTaskScheduler>();
builder.Services.AddSingleton<ITaskScheduler>(sp => sp.GetRequiredService<SimpleTaskScheduler>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<SimpleTaskScheduler>());

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
