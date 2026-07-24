using Microsoft.Extensions.DependencyInjection;
using UndeuxSales.Core.Rag;
using UndeuxSales.Infrastructure.Ai;
using UndeuxSales.Infrastructure.Database;
using UndeuxSales.Infrastructure.Import;
using UndeuxSales.Infrastructure.Queries;
using UndeuxSales.Infrastructure.Rag;

namespace UndeuxSales.Infrastructure;

/// <summary>Infrastructure 層のDI登録拡張。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>データアクセス・取込・分析・AI/RAG の各サービスを登録する。</summary>
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
        services.AddScoped<InventoryFlagRepository>();

        // 組織マスタ・ナレッジ（RAG）・AI チャット
        services.AddMemoryCache();
        services.AddSingleton<ChatPromptCacheVersion>();
        services.AddScoped<OrgMasterRepository>();
        services.AddScoped<KnowledgeRepository>();
        services.AddScoped<KnowledgeIngestionService>();
        services.AddScoped<KnowledgeSeeder>();
        services.AddScoped<ChatContextService>();
        services.AddSingleton<IAiChatClient, AnthropicAiClient>();

        return services;
    }
}
