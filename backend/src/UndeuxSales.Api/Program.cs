using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using UndeuxSales.Api;
using UndeuxSales.Api.Configuration;
using UndeuxSales.Api.Middleware;
using UndeuxSales.Core;
using UndeuxSales.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "接続文字列 ConnectionStrings:Default が設定されていません。"
        + "環境変数 ConnectionStrings__Default 等で指定してください。");
}

builder.Services.AddInfrastructure(connectionString);

builder.Services.AddControllers();

// モデルバインド検証エラーも統一エラー形式（ApiError）で返す。
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var errors = context.ModelState
            .Where(entry => entry.Value is { Errors.Count: > 0 })
            .Select(entry =>
                $"{entry.Key}: {string.Join(" / ", entry.Value!.Errors.Select(e => e.ErrorMessage))}")
            .ToList();

        var apiError = new ApiError(
            ErrorCodes.InvalidRequest.Code,
            ErrorCodes.InvalidRequest.Summary,
            ErrorCodes.InvalidRequest.Remedy,
            "リクエストパラメータの検証に失敗しました。",
            errors);

        return new BadRequestObjectResult(apiError);
    };
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(SwaggerSetup.Configure);

builder.Services.Configure<ImportSettings>(
    builder.Configuration.GetSection(ImportSettings.SectionName));

var firebaseOptions = builder.Configuration
    .GetSection(FirebaseAuthOptions.SectionName)
    .Get<FirebaseAuthOptions>() ?? new FirebaseAuthOptions();

var corsSettings = builder.Configuration
    .GetSection(CorsSettings.SectionName)
    .Get<CorsSettings>() ?? new CorsSettings();

// Firebase Authentication が発行するIDトークン（JWT）を検証する。
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var issuer = $"https://securetoken.google.com/{firebaseOptions.ProjectId}";
        options.Authority = issuer;
        options.IncludeErrorDetails = builder.Environment.IsDevelopment();

        // Firebase カスタムクレーム（role 等）をそのままのクレーム名で扱う。
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = firebaseOptions.ProjectId,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };
    });

builder.Services.AddAuthorization(options =>
{
    // 取込（データ更新操作）は role=admin のカスタムクレームを持つ利用者に限定する。
    options.AddPolicy(AuthorizationPolicies.Importer, policy =>
        policy.RequireClaim(AuthorizationPolicies.RoleClaim, AuthorizationPolicies.AdminRole));
});

const string corsPolicyName = "frontend";
builder.Services.AddCors(options =>
    options.AddPolicy(corsPolicyName, policy =>
    {
        if (corsSettings.AllowedOrigins.Length > 0)
        {
            policy.WithOrigins(corsSettings.AllowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
    }));

var app = builder.Build();

if (string.IsNullOrWhiteSpace(firebaseOptions.ProjectId))
{
    app.Logger.LogWarning(
        "Firebase:ProjectId が未設定です。/api/health 以外の保護エンドポイントは認証に失敗します。");
}

if (corsSettings.AllowedOrigins.Length == 0)
{
    app.Logger.LogWarning(
        "Cors:AllowedOrigins が未設定です。ブラウザからのAPI呼び出しは CORS により拒否されます。");
}

// 例外ハンドリングは最外層に配置し、後続すべての例外を捕捉する。
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(corsPolicyName);
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

/// <summary>統合テスト（WebApplicationFactory）が参照するための公開マーカー型。</summary>
public partial class Program;
