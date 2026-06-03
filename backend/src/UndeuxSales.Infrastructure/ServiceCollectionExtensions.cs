using Microsoft.Extensions.DependencyInjection;
using UndeuxSales.Infrastructure.Database;
using UndeuxSales.Infrastructure.Import;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Infrastructure;

/// <summary>Infrastructure 層のDI登録拡張。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>データアクセス・取込・分析の各サービスを登録する。</summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, string connectionString)
    {
        DapperConfiguration.Initialize();

        services.AddSingleton<IDbConnectionFactory>(
            _ => new NpgsqlConnectionFactory(connectionString));

        services.AddScoped<SalesImportService>();
        services.AddScoped<ImportBatchRepository>();
        services.AddScoped<SalesAnalyticsRepository>();
        services.AddScoped<MasterRepository>();
        services.AddScoped<ProductMasterRepository>();
        services.AddScoped<ProductAnalyticsRepository>();
        services.AddScoped<AnalysisRepository>();
        services.AddScoped<MartAnalyticsRepository>();

        return services;
    }
}
