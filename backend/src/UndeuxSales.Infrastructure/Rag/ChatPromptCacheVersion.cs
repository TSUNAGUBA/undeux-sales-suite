namespace UndeuxSales.Infrastructure.Rag;

/// <summary>
/// チャット system プロンプト（マスタ由来の安定部）のキャッシュ世代管理。
/// マスタ変更時に Bump することで、TTL を待たずに次のチャットへ反映させる（SoT→派生の同期）。
/// </summary>
public sealed class ChatPromptCacheVersion
{
    private long _version;

    public long Current => Interlocked.Read(ref _version);

    public void Bump() => Interlocked.Increment(ref _version);
}
