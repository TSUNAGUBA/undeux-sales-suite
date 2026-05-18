using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace UndeuxSales.Api.Configuration;

/// <summary>Swagger / OpenAPI の生成設定。</summary>
public static class SwaggerSetup
{
    /// <summary>SwaggerGen のオプションを構成する（JWT Bearer 認証を含む）。</summary>
    public static void Configure(SwaggerGenOptions options)
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "UndeuxSales 売上参照API",
            Version = "v1",
            Description = "小売から提供される週次売上参照データを格納・可視化するためのAPI。",
        });

        var securityScheme = new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Firebase ID トークンを Bearer スキームで指定してください。",
            Reference = new OpenApiReference
            {
                Type = ReferenceType.SecurityScheme,
                Id = "Bearer",
            },
        };

        options.AddSecurityDefinition("Bearer", securityScheme);
        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            [securityScheme] = Array.Empty<string>(),
        });
    }
}
