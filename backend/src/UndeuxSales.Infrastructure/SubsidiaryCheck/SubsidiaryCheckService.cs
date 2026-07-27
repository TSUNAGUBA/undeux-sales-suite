using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UndeuxSales.Core;
using UndeuxSales.Core.Rag;
using UndeuxSales.Core.SubsidiaryCheck;
using UndeuxSales.Infrastructure.Ai;
using UndeuxSales.Infrastructure.Rag;

namespace UndeuxSales.Infrastructure.SubsidiaryCheck;

/// <summary>アップロードされたチェック画像1枚（コントローラ→サービスの入力）。</summary>
public sealed record SubsidiaryCheckImageUpload(string FileName, string ContentType, byte[] Data);

/// <summary>
/// 画像配信の枠（上限は <see cref="SubsidiaryCheckService.MaxConcurrentImageDownloads"/>）を
/// 1つ取得したスコープ。<see cref="Dispose"/> で解放する。
/// <para>
/// <b>なぜスコープ型か:</b> 枠は「DB 読取 → 応答本文の書き出し」の全区間で保持する必要があり
/// （<see cref="SubsidiaryCheckService.MaxConcurrentImageDownloads"/> の解説を参照）、
/// 途中の失敗パス（404・キャンセル・書き出し中の切断）でも確実に解放しなければ
/// 429 が恒久化して画像が一切表示できなくなる。<c>using</c> で構造的に保証する。
/// </para>
/// 解放は1回だけ（<see cref="Interlocked.Exchange(ref int, int)"/> で二重解放を防ぐ。
/// 過剰解放は上限を無効化してメモリ防御を崩すため）。
/// </summary>
public sealed class ImageDownloadSlotLease : IDisposable
{
    /// <summary>解放責務を保持しているか（1＝保持、0＝解放済み）。</summary>
    private int _owned = 1;

    internal ImageDownloadSlotLease()
    {
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _owned, 0) == 1)
        {
            SubsidiaryCheckService.ReleaseImageDownloadSlot();
        }
    }
}

/// <summary>
/// 受付枠（AI チェックの実行中＋待機中の総数。上限は
/// <see cref="SubsidiaryCheckService.MaxConcurrentAiChecks"/>）を1つ予約したスコープ。
/// <para>
/// <b>なぜスコープ型か:</b> 受付枠は「画像バッファ（1リクエストあたり最大
/// <see cref="SubsidiaryCheckService.MaxTotalImageBytes"/>＝20MB）を確保する<b>前</b>」に
/// 予約しないとピークメモリを有界化できない（429 で拒否される要求まで 20MB を確保してしまい、
/// 受付枠が満杯の状態で同時アップロードが届くと GC ヒープハードリミットを突破しうる）。
/// そのため予約はコントローラの入口（バッファ確保前）で行うことになり、
/// 予約から解放までがリクエスト処理の広い範囲＋バックグラウンドタスクに跨る。
/// 解放漏れは 429 の恒久化（機能停止）、過剰解放はメモリ防御の無効化に直結するため、
/// <see cref="IDisposable"/> のスコープとして解放を構造的に保証する。
/// </para>
/// <para>
/// <b>解放責務は常に1箇所だけ:</b> 予約したスコープの <see cref="Dispose"/> が解放する。
/// AI 実行をバックグラウンドへ切り離せた場合のみ <see cref="HandOffToBackground"/> で
/// 責務を新しいスコープへ移し、元のスコープの <see cref="Dispose"/> は何もしなくなる
/// （＝リクエスト側の <c>using</c> とバックグラウンドの <c>finally</c> が二重解放しない）。
/// </para>
/// </summary>
public sealed class AiCheckSlotLease : IDisposable
{
    private readonly ILogger _logger;

    /// <summary>解放責務を保持しているか（1＝保持、0＝解放済みまたは移譲済み）。</summary>
    private int _owned;

    /// <summary>
    /// 予約済みの受付枠を表すスコープを作る（このコンストラクタ自体は枠を消費しない）。
    /// 枠の消費は <see cref="TryReserve"/>、責務の移譲は <see cref="HandOffToBackground"/> が行う。
    /// </summary>
    private AiCheckSlotLease(ILogger logger)
    {
        _logger = logger;
        _owned = 1;
    }

    /// <summary>受付枠を1つ予約する。上限に達している場合は予約せず null。</summary>
    internal static AiCheckSlotLease? TryReserve(ILogger logger)
        => SubsidiaryCheckService.TryReserveAiCheckSlot() ? new AiCheckSlotLease(logger) : null;

    /// <summary>
    /// 受付枠を1つ予約する。上限に達している場合は 429（<see cref="ErrorCodes.AiCheckBusy"/>）。
    /// </summary>
    internal static AiCheckSlotLease Reserve(ILogger logger)
        => TryReserve(logger)
           ?? throw new AppException(ErrorCodes.AiCheckBusy, 429,
               "実行中・待機中の AI チェックが上限"
               + $"（{SubsidiaryCheckService.MaxConcurrentAiChecks} 件）に達しています。"
               + "実行中のチェックが完了してから再試行してください。");

    /// <summary>
    /// 解放責務をバックグラウンドタスク用の新しいスコープへ移す。
    /// 移譲後、このスコープの <see cref="Dispose"/> は何もしない（二重解放の構造的な防止）。
    /// </summary>
    /// <returns>解放責務を引き継いだスコープ（バックグラウンドの finally で必ず Dispose する）。</returns>
    internal AiCheckSlotLease HandOffToBackground()
        => TryTakeOwnership()
            ? new AiCheckSlotLease(_logger)
            : throw new InvalidOperationException("受付枠の予約は既に解放または移譲されています。");

    /// <summary>予約を解放する（解放済み・移譲済みなら何もしない＝冪等）。</summary>
    public void Dispose()
    {
        if (TryTakeOwnership())
        {
            SubsidiaryCheckService.ReleaseAiCheckSlot(_logger);
        }
    }

    /// <summary>解放責務を自分のものとして取り出す（成功するのは1スコープにつき1回だけ）。</summary>
    private bool TryTakeOwnership() => Interlocked.Exchange(ref _owned, 0) == 1;
}

/// <summary>
/// 副資材チェックのオーケストレーション。
/// <para>
/// フロー: 受付枠の予約（コントローラ入口・画像バッファ確保前）→ 検証 →
/// INSERT（processing。SoT への記録が先）→ <b>即時応答</b> →
/// （バックグラウンド）AI 呼出 → 応答解析 → UPDATE（completed / failed）。
/// </para>
/// <para>
/// <b>非同期実行の根拠:</b> 公開経路の共用リバースプロキシ（nginx-proxy）のタイムアウトは約60秒で、
/// AI 呼出（順番待ち最大420秒＋呼出最大120秒）を HTTP リクエスト内で待つと利用者には 504 が返る一方
/// サーバ側は completed で確定し、失敗と誤認した再送が重複チェック・重複 AI コスト・重複画像を招く。
/// そのため <c>POST /api/mart/rebuild</c>（mart 再構築）と同じ
/// 「実行権を確定して即応答 → 本体はリクエストから切り離したバックグラウンドタスク →
/// status で状態管理 → フロントがポーリング → 滞留した processing は孤児として再実行許可」
/// 構造に揃える（原則3。docs/star-schema-design.md「非同期実行・タイムアウトしない設計」）。
/// </para>
/// <para>
/// AI 呼出・解析の失敗、AI 呼出タイムアウト、順番待ち超過、再実行準備（DB 読取）の失敗は
/// すべて例外にせず failed 記録＋エラー格納に落とす
/// （グレースフルデグラデーション・原則4。登録済み記録は残り、再実行（rerun）で回復できる）。
/// バックグラウンドタスクごと消えるケース（プロセスクラッシュ）は processing のまま残るが、
/// <see cref="ProcessingStaleAfter"/> 経過後の rerun で回復できる。
/// </para>
/// </summary>
public sealed class SubsidiaryCheckService
{
    /// <summary>指示書画像の最小枚数。</summary>
    public const int MinInstructionImages = 1;

    /// <summary>指示書画像の最大枚数。</summary>
    public const int MaxInstructionImages = 3;

    /// <summary>タグ画像の最小枚数。</summary>
    public const int MinTagImages = 1;

    /// <summary>タグ画像の最大枚数。</summary>
    public const int MaxTagImages = 10;

    /// <summary>
    /// 画像1枚の上限サイズ。Anthropic API の画像上限（約5MB/枚）に由来する既存定数を再利用する（原則3）。
    /// </summary>
    public const long MaxImageSizeBytes = KnowledgeIngestionService.MaxImageFileSizeBytes;

    /// <summary>
    /// 全画像（指示書＋タグ）の合計 raw サイズ上限（20MB）。
    /// 根拠: Anthropic Messages API はリクエスト全体で 32MB 制限があり、画像は base64 で約 1.33 倍に
    /// 膨張する。20MB raw ≒ 26.6MB base64 ＋ プロンプト分で 32MB に対して安全側の値。
    /// この上限がないと「各5MB × 最大13枚 = 65MB」の正当入力が AI 呼出で構造的に失敗する。
    /// フロント（utils/subsidiaryCheck.ts の SUBSIDIARY_TOTAL_IMAGE_MAX_BYTES）と
    /// UNDX-REQ-008 のメッセージ文言はこの値と同期させること。
    /// </summary>
    public const long MaxTotalImageBytes = 20 * 1024 * 1024;

    /// <summary>
    /// 商品ラベル（クライアント指定の任意テキスト）の最大文字数（200文字）。
    /// 根拠: 商品ラベルは「品番・商品名相当の短い識別文字列」を想定した表示用スナップショットであり、
    /// 上限がないと Kestrel の ValueLengthLimit（既定 4MB）まで受理してしまう。
    /// 巨大テキストの永続化・一覧展開・AI プロンプトへの混入という増幅経路を塞ぐ
    /// （inventory_action_flag.note を 1,000 文字に制限した先例と同じ方針）。
    /// フロント（utils/subsidiaryCheck.ts の SUBSIDIARY_PRODUCT_LABEL_MAX_LENGTH）と同期させること。
    /// </summary>
    public const int MaxProductLabelLength = 200;

    /// <summary>
    /// processing のまま経過した場合に「孤児（プロセスクラッシュ等で結果が確定しないレコード）」と
    /// みなして再実行（rerun）を許可するまでの時間。基準時刻は started_at（最後の AI 実行開始日時）。
    /// <para>
    /// 根拠: 1回のバックグラウンド実行の processing 滞留時間は
    /// 「セマフォ待機（<see cref="AiCallQueueTimeout"/> = 420秒で打切り）
    /// ＋ AI 呼出（<see cref="AiCallTimeout"/> = 120秒で打切り）＋ 記録処理（DB 更新・秒オーダ）」で
    /// <b>有界</b>であり、上限は 420＋120＋α ＝ 約9分。待機超過・呼出タイムアウトはいずれも
    /// failed 記録で確定するため、これを超えて processing に留まるのは
    /// <b>プロセス消失（クラッシュ・強制終了）、およびバックグラウンド実行の起動自体の失敗</b>
    /// （<see cref="StartBackgroundRun"/> のスコープ生成・解決失敗。failed 記録も残せない経路）だけ。
    /// 有界化された最大滞留時間 約9分に対し十分な余裕を見て20分とする
    /// （正常に実行待ちのチェックを孤児と誤判定しないことを優先。誤判定すると同一チェックの
    /// AI 呼出が多重化し、外部有料 API のコストが二重に発生する）。
    /// </para>
    /// フロント（utils/subsidiaryCheck.ts の SUBSIDIARY_PROCESSING_STALE_MS）と同期させること。
    /// </summary>
    public static readonly TimeSpan ProcessingStaleAfter = TimeSpan.FromMinutes(20);

    /// <summary>
    /// AI チェック（画像分析）専用の最大出力トークン数。
    /// チャット用のグローバル設定（AiOptions.MaxOutputTokens。既定 2048）とは意図的に分離する:
    /// findings JSON（3カテゴリ×複数指摘）は 2048 トークンでは切り詰められ UNDX-AI-009 になるため。
    /// </summary>
    private const int CheckMaxOutputTokens = SubsidiaryCheckPromptBuilder.RecommendedMaxTokens;

    /// <summary>
    /// AI 呼出1回のタイムアウト（120秒）。
    /// 根拠: 最大13枚の画像分析でも通常応答は数十秒であり、ネットワーク断・API 側の張り付き等の
    /// 異常時にバックグラウンドタスクと processing 状態が無期限に滞留するのを防ぐ余裕値。
    /// </summary>
    private static readonly TimeSpan AiCallTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// 同時 AI チェック実行数の上限（1＝直列化）。
    /// <para>
    /// <b>メモリ収支（同時実行数を 1 にした根拠）:</b> api コンテナのメモリ上限は本番
    /// （infra/aws/docker-compose.ec2.yml）・ローカル（docker-compose.yml）とも 512m で、
    /// cgroup 制限下の .NET GC ヒープハードリミットは既定でその 75% ＝ 384MiB。
    /// 一方 AI 呼出中の1件（バックグラウンドタスク）が同時に保持する量は、設計上許容された正常系の最大入力
    /// （<see cref="MaxTotalImageBytes"/> = 20MiB）で概算 <b>約100MiB</b>。
    /// <b>本収支のメモリ量はすべて MiB 表記</b>（上限・ハードリミット・画像上限がいずれも 2 の冪で
    /// 定義されているため同一単位で突き合わせる。docs/design.md §13.5 と同じ数値）。内訳:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>画像バッファ byte[]: 20MiB</description></item>
    ///   <item><description>base64 文字列: 20MiB → 約 28.0M 文字。.NET string は UTF-16
    ///     （2バイト/文字）のため約 53MiB</description></item>
    ///   <item><description>HTTP リクエストボディの UTF-8 直列化（base64 は ASCII のため
    ///     1バイト/文字）: 約 27MiB</description></item>
    /// </list>
    /// <para>
    /// 同時1件なら実行中のピークは 100MiB ＋ ASP.NET Core のベースライン（約100〜150MiB）＝ 約250MiB で、
    /// 384MiB に対し余裕がある（待機中の画像バッファ分は <see cref="MaxConcurrentAiChecks"/> の
    /// 収支に含めて評価している）。3並列では約300MiB ＋ ベースラインでハードリミットに到達し、
    /// <b>正常系の入力で OOM → コンテナ再起動（全機能停止）</b>に至るため直列化する。
    /// 想定利用（日次数件〜10件）に対し同時1件で機能上の問題はない。
    /// スループットが不足する場合は、同時実行数を上げる前にコンテナのメモリ上限引上げ
    /// （EC2 インスタンスの空き容量確認が前提のオペレーター判断）を選択肢とすること。
    /// </para>
    /// <para>
    /// AI 呼出部分のみを制限し、DB 操作はセマフォの外で行う（DB まで直列化しない）。
    /// 待機時間は <see cref="AiCallQueueTimeout"/> で有界化する。ただしセマフォが制限するのは
    /// <b>同時実行数だけで待機中の件数ではない</b>ため、待ち行列に画像バッファを抱えたまま滞留する
    /// 件数は <see cref="MaxConcurrentAiChecks"/>（実行中＋待機中の総数の上限）で別途抑制する。
    /// </para>
    /// <para>
    /// maxCount を 1 に明示する（<c>new SemaphoreSlim(1)</c> では上限が int.MaxValue になり、
    /// 余分な Release が1回でも起きると「同時1件」という唯一のメモリ防御が無言で緩和される）。
    /// 対称性が崩れた場合は <see cref="SemaphoreFullException"/> で即座に顕在化させる。
    /// </para>
    /// </summary>
    private static readonly SemaphoreSlim AiCallSemaphore = new(1, 1);

    /// <summary>
    /// 受け付ける AI チェックの総数（実行中＋バックグラウンド待機中）の上限。
    /// <para>
    /// <see cref="AiCallSemaphore"/> は AI 呼出の<b>同時実行数</b>のみを 1 に制限するが、
    /// 順番待ちの件数は制限しない。画像バッファ（1件あたり最大
    /// <see cref="MaxTotalImageBytes"/> = 20MiB）は AI 実行前に確保済みのため、待機 K 件で
    /// 20MiB×K が滞留し、GC ヒープハードリミット（384MiB）を待ち行列側から突破しうる。
    /// </para>
    /// <para>
    /// 実行中1件（AI 呼出中のピーク約100MiB）＋待機3件（20MiB×3＝60MiB）＝約160MiB に
    /// ASP.NET Core のベースライン（約100〜150MiB）を加えても約310MiB で、384MiB に対し余裕がある
    /// ため 4 とする。超過分は永続化・クレームの<b>前に</b> 429（<see cref="ErrorCodes.AiCheckBusy"/>）で
    /// 拒否し、無駄なレコード・画像や「クレーム直後の failed」で記録を汚さない。
    /// </para>
    /// <para>
    /// <b>この収支が成立する前提:</b> 予約が<b>画像バッファの確保より前</b>に行われること。
    /// 予約より前にバッファを確保すると、429 で拒否される要求まで 20MiB を確保するため
    /// 「受付枠が満杯＋同時 POST 4本」で +80MiB となり収支（約310MiB）が崩れる。
    /// そのため予約は <see cref="AiCheckSlotLease"/> でコントローラの入口
    /// （<c>file.Length</c> 合算による合計サイズ判定の直後・バッファ確保前）から行う。
    /// なお multipart のフォームバッファリングは予約より前に完了しているが、
    /// <c>FormOptions.MemoryBufferThreshold</c>（既定 65,536 バイト ＝ 64KiB。本リポジトリで上書きなし）を超える
    /// ファイルはディスクへ退避されるため、マネージドヒープへの寄与は
    /// 最大13枚 × 64KiB ≒ 0.8MiB/リクエストに留まり、上の収支を崩さない。
    /// </para>
    /// </summary>
    public const int MaxConcurrentAiChecks = 4;

    /// <summary>
    /// 受付済み（実行中＋バックグラウンド待機中）の AI チェック件数を制限するセマフォ。
    /// 予約はリクエスト側（画像バッファ確保・永続化・クレームのいずれよりも前）、
    /// 解放はバックグラウンドタスクの終了時に行い、「受け付けてから実行が終わるまで」を漏れなく囲う。
    /// 予約・解放の対称性は <see cref="AiCheckSlotLease"/>（IDisposable スコープ）で構造的に保証する。
    /// <para>
    /// カウンタ変数（Interlocked の増減）ではなく <see cref="AiCallSemaphore"/> と同じ
    /// <see cref="SemaphoreSlim"/> を使う（原則3）。理由は2つ:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>maxCount を明示できるため、過剰解放が
    ///     <b>遅くとも全枠が解放される時点までに</b> <see cref="SemaphoreFullException"/> として
    ///     必ず顕在化する（カウンタ方式では負に沈んだまま気付けず、受付上限が恒久的に緩和される）。
    ///     ただし他の予約が残っている間の過剰解放は即座には検知されず、
    ///     例外が出る箇所も原因箇所とは限らない（＝件数ベースの検証と併用する）。</description></item>
    ///   <item><description>「加算してから超過なら巻き戻す」方式の一時的な超過中に、
    ///     空き枠があるのに別リクエストが 429 になる誤検知の窓が生じない。</description></item>
    /// </list>
    /// </summary>
    private static readonly SemaphoreSlim AcceptanceSemaphore =
        new(MaxConcurrentAiChecks, MaxConcurrentAiChecks);

    /// <summary>
    /// AI 呼出の順番待ち（セマフォ待機）の上限（420秒＝7分）。
    /// <para>
    /// <b>値の根拠:</b> 受付上限は <see cref="MaxConcurrentAiChecks"/> ＝ 4件（実行中1＋待機3）で、
    /// 同時実行は1件・AI 呼出は <see cref="AiCallTimeout"/> ＝ 120秒で打ち切られる。
    /// したがって受け付けられたチェックの最悪の待ち時間は「先行3件 × 120秒 ＝ 360秒」であり、
    /// これに余裕を見た 420秒 とする。
    /// 非同期化により HTTP リクエストはこの待ちを待たないため、
    /// 上限を「受付上限と整合する値」まで引き上げても利用者を待たせることはない。
    /// この算式は (1) 待ち行列がおおむね FIFO であること（<see cref="SemaphoreSlim"/> は
    /// 仕様上 FIFO を保証しないため「おおむね」）、(2) AI 呼出が必ず
    /// <see cref="AiCallTimeout"/> で打ち切られること、の2点を前提とする。
    /// 前提が崩れて待ちが 420秒 を超えた場合も failed 記録＋rerun で回復できる（致命的ではない）。
    /// </para>
    /// <para>
    /// <b>短すぎる値にしない理由:</b> 同期実行時代の 30秒 のままだと、2人が同時に実行しただけで
    /// 2件目が AI を一度も呼ばれずに failed になり、手動 rerun が常態化する
    /// （原則1「手動ステップを残さない」に抵触）。
    /// </para>
    /// <para>
    /// <b>無制限にしない理由:</b> 待機を無制限にすると、画像バッファ（1件あたり最大20MB）を
    /// 保持したままの実行が長時間居座り、受付枠（<see cref="MaxConcurrentAiChecks"/>）が解放されず
    /// 新規受付が 429 で塞がり続ける。また processing の滞留時間が非有界になり、孤児判定
    /// （<see cref="ProcessingStaleAfter"/>）の根拠が成り立たなくなる。
    /// 超過時は AI を呼ばずに failed 記録に落とし、rerun で回復できる
    /// （待機「件数」の上限は <see cref="MaxConcurrentAiChecks"/> が担う）。
    /// </para>
    /// <b>変更時は <see cref="ProcessingStaleAfter"/> を再計算すること</b>
    /// （孤児判定は「順番待ち上限＋AI 呼出上限＋記録処理」の有界性を根拠にしているため）。
    /// </summary>
    private static readonly TimeSpan AiCallQueueTimeout = TimeSpan.FromSeconds(420);

    /// <summary>
    /// 許可する画像 Content-Type（jpeg / png のみ）。
    /// 比較は大文字小文字非依存で行う（RFC 9110 上 media type は case-insensitive で、
    /// クライアントが "IMAGE/JPEG" 等を送っても正当な入力のため）。
    /// </summary>
    private static readonly IReadOnlyList<string> AllowedContentTypes = new[]
    {
        "image/jpeg", "image/png",
    };

    /// <summary>PNG の Content-Type（マジックバイト分岐の判定に使う）。</summary>
    private const string PngContentType = "image/png";

    // ---- AI 実行スロット制御の内部シーム（統合テスト専用。InternalsVisibleTo=UndeuxSales.Tests） ----
    // 本番では両オーバーライドとも null で、上の定数どおり（順番待ち420秒 / 呼出120秒）に動作する。
    // 待機超過・呼出タイムアウトの各経路を実時間で待たずに検証するためだけに用意している。

    /// <summary>順番待ち上限のテスト用オーバーライド（null＝<see cref="AiCallQueueTimeout"/>）。</summary>
    internal static TimeSpan? AiCallQueueTimeoutOverride;

    /// <summary>AI 呼出タイムアウトのテスト用オーバーライド（null＝<see cref="AiCallTimeout"/>）。</summary>
    internal static TimeSpan? AiCallTimeoutOverride;

    /// <summary>
    /// 再実行準備（商品情報・画像バイナリの DB 読取）に失敗を注入するテスト用フック
    /// （null＝注入なし＝本番の挙動）。クレーム後の準備失敗が failed 記録に落ちること
    /// （＝無保護区間がないこと）を、実際の DB 障害を起こさずに検証するためだけに用意している。
    /// </summary>
    internal static Func<Guid, Exception?>? RerunPrepareFailureOverride;

    /// <summary>実行スロットを即時取得できたか（テストから待ち行列状態を作るために使う）。</summary>
    internal static Task<bool> TryOccupyAiSlotAsync() => AiCallSemaphore.WaitAsync(TimeSpan.Zero);

    /// <summary><see cref="TryOccupyAiSlotAsync"/> で取得したスロットを解放する。</summary>
    internal static void ReleaseAiSlot() => AiCallSemaphore.Release();

    /// <summary>画像配信枠の空き数（テストから占有状態を観測するために使う）。</summary>
    internal static int AvailableImageDownloadSlots => ImageDownloadSemaphore.CurrentCount;

    /// <summary>画像配信枠を即時取得できたか（テストから順番待ち状態を作るために使う）。</summary>
    internal static Task<bool> TryOccupyImageDownloadSlotAsync() =>
        ImageDownloadSemaphore.WaitAsync(TimeSpan.Zero);

    /// <summary>
    /// 画像配信枠を1つ解放する。<b>セマフォを解放する唯一の関数</b>にしておくこと
    /// （解放時の不変条件チェックやログを足すときに経路ごとの分岐を作らないため）。
    /// 本番の解放は <see cref="ImageDownloadSlotLease.Dispose"/> 経由、
    /// テストは <see cref="TryOccupyImageDownloadSlotAsync"/> と対で使う。
    /// </summary>
    internal static void ReleaseImageDownloadSlot() => ImageDownloadSemaphore.Release();

    /// <summary>
    /// 受付枠を1つ占有する（テストから受付上限に達した状態を作るために使う）。上限到達時は null。
    /// 解放は本番と同じ <see cref="AiCheckSlotLease.Dispose"/> で行う（テスト専用の解放経路を作らない）。
    /// 注意: ロガーは <see cref="NullLogger"/> のため、<b>このリースでの過剰解放はログに出ない</b>
    /// （テストが保持する枠の対称性は残枠数で検証すること）。
    /// </summary>
    internal static AiCheckSlotLease? TryReserveAiCheckSlotForTest()
        => AiCheckSlotLease.TryReserve(NullLogger.Instance);

    private readonly SubsidiaryCheckRepository _repository;
    private readonly IAiChatClient _aiClient;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SubsidiaryCheckService> _logger;

    public SubsidiaryCheckService(
        SubsidiaryCheckRepository repository,
        IAiChatClient aiClient,
        IServiceScopeFactory scopeFactory,
        ILogger<SubsidiaryCheckService> logger)
    {
        _repository = repository;
        _aiClient = aiClient;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>AI 実行本体への入力（商品情報・表示ラベル・AI へ渡す画像列）。</summary>
    private sealed record AiRunInput(
        SubsidiaryCheckProductInfo? Product,
        string ProductLabel,
        IReadOnlyList<AiImageInput> Images);

    /// <summary>
    /// 新規チェックを登録し、AI チェックを<b>バックグラウンドで開始</b>して
    /// processing 状態の詳細を即座に返す（リバースプロキシのタイムアウト回避。クラス注記を参照）。
    /// <para>
    /// 入力検証エラー（枚数・形式・サイズ・ラベル長）は従来どおり同期的に 4xx を返す。
    /// AI 未設定（IsConfigured=false）は永続化前に 503（UNDX-AI-008）、受付上限超過は
    /// <b>画像バッファの確保前</b>に 429（UNDX-REQ-009）を throw する
    /// （いずれも無駄なレコード・画像を作らない）。
    /// </para>
    /// </summary>
    /// <param name="reservation">
    /// コントローラが<b>画像バッファ確保前に</b>取得済みの受付枠（W-1）。
    /// 受け取った場合はここで再予約しない（二重予約の防止）。解放責務は呼出元の <c>using</c> にあり、
    /// バックグラウンド起動に成功したときだけ <see cref="StartBackgroundRun"/> が責務を引き取る。
    /// サービスを直接呼ぶ経路（テスト等。画像は既にメモリ上）では null を渡し、ここで予約する。
    /// </param>
    public async Task<SubsidiaryCheckDetail> CreateAndStartAsync(
        Guid? productId,
        string? productLabel,
        IReadOnlyList<SubsidiaryCheckImageUpload> instructionImages,
        IReadOnlyList<SubsidiaryCheckImageUpload> tagImages,
        string createdBy,
        AiCheckSlotLease? reservation = null,
        CancellationToken cancellationToken = default)
    {
        // 未予約で呼ばれた場合のみ、このスコープで予約して確実に解放する
        // （予約済みの場合は null なので using は何もしない）。
        using var ownReservation = reservation is null ? ReserveAiCheckSlotOrThrow() : null;
        var lease = reservation ?? ownReservation!;

        ValidateImages(instructionImages, tagImages);

        if (!_aiClient.IsConfigured)
        {
            throw new AppException(ErrorCodes.AiNotConfigured, 503);
        }

        SubsidiaryCheckProductInfo? product = null;
        if (productId is { } id)
        {
            product = await _repository.GetProductInfoAsync(id, cancellationToken)
                      ?? throw new AppException(ErrorCodes.ProductNotFound, 404);
        }

        var label = ResolveProductLabel(productLabel, product);

        // SoT（subsidiary_check）への記録を先に確定してから AI を呼び出す（原則6）。
        // ここで失敗してもレコードが作られていないため failed 記録の対象はない
        // （受付枠は呼出元の using が解放する）。
        var checkId = Guid.NewGuid();
        await _repository.InsertAsync(
            checkId, productId, label, createdBy, BuildImageRecords(instructionImages, tagImages),
            cancellationToken);

        SubsidiaryCheckDetail accepted;
        AiRunInput input;
        try
        {
            // 応答は「受付時点（processing）」の状態にする。バックグラウンド起動より前に読むことで、
            // AI が即座に終わった場合でも応答内容が実行タイミングに左右されない。
            accepted = await GetDetailRequiredAsync(checkId, cancellationToken);

            // AI 実行入力の構築もこの try の内側で行う。呼出の引数式として評価すると
            // try の外になり、ここでの失敗が failed 記録も解放も伴わない無保護区間になる（監査 I-1）。
            input = new AiRunInput(product, label, ToAiImages(instructionImages, tagImages));
        }
        catch
        {
            // 登録済みだがバックグラウンドを起動できない区間。processing のまま放置せず
            // failed を記録する（孤児判定の経過時間を待たずに再実行できる・原則4）。
            // 受付枠の解放は呼出元の using（未移譲のため有効）が行う。
            await RecordFailureAsync(checkId, BuildFailureMessage(
                ErrorCodes.Unexpected, "チェックの登録後に受付処理へ失敗しました。再実行してください。"));
            throw;
        }

        // 起動に成功した時点で、受付枠の解放責務はバックグラウンドタスク側へ移る。
        // AI 完了を待たずに応答し、フロントは詳細をポーリングして completed / failed を待つ。
        StartBackgroundRun(checkId, input, lease);

        return accepted;
    }

    /// <summary>
    /// AI を再実行する（手動回復パス）。許可条件は
    /// 「status=failed」または「status=processing かつ最後の実行開始（started_at）から
    /// <see cref="ProcessingStaleAfter"/> 超経過（プロセスクラッシュ等で結果が確定しない
    /// processing 孤児の回復）」。completed のチェックは記録保護（原則2）のため 400 を返す。
    /// <para>
    /// 実行権は DB の1回の UPDATE（<see cref="SubsidiaryCheckRepository.ClaimForRerunAsync"/>）で
    /// 原子的にクレームする。read-then-act では「stale processing の rerun 実行中も status・
    /// 基準時刻が不変」のため、別タブ・別ユーザーから何度でも起動でき同一 checkId への AI 呼出が
    /// 多重化するが、クレーム成立と同時に started_at が now() へ進むことで孤児条件から外れ、
    /// 重複起動が構造的に成立しない。
    /// </para>
    /// <para>
    /// クレーム成立後の AI 実行（および画像・商品情報の読取）はバックグラウンドへ切り離し、
    /// 新規作成と同じく processing の詳細を即座に返す。
    /// </para>
    /// </summary>
    /// <param name="requestedBy">
    /// 実行者（<c>RequestIdentity.AuditUserOf</c> の値）。rerun は外部有料 API を起動する書込操作の
    /// ため、無記名で反復実行できる状態にしない（監査 W-3）。クレーム成立時にログへ記録する。
    /// </param>
    public async Task<SubsidiaryCheckDetail> RerunAsync(
        Guid checkId, string requestedBy, CancellationToken cancellationToken = default)
    {
        // 未存在は 404 で早期に返す（クレーム 0 行と「存在しない」を区別するため）。
        _ = await GetDetailRequiredAsync(checkId, cancellationToken);

        if (!_aiClient.IsConfigured)
        {
            throw new AppException(ErrorCodes.AiNotConfigured, 503);
        }

        // 受付枠の予約はクレームより前に行う（クレームしてから即 failed に落とすと記録を汚すため）。
        // 以降の全失敗パスでは using が解放し、起動に成功したときだけ責務がバックグラウンドへ移る。
        using var lease = ReserveAiCheckSlotOrThrow();

        // 実行権の原子的クレーム。0 行なら「他が実行中」「completed で確定済み」のいずれか。
        var claimed = await _repository.ClaimForRerunAsync(
            checkId, DateTime.UtcNow - ProcessingStaleAfter, cancellationToken);
        if (claimed == 0)
        {
            throw await BuildRerunRejectedAsync(checkId, cancellationToken);
        }

        SubsidiaryCheckDetail accepted;
        try
        {
            // 実行権を確保した時点で実行者を記録する（外部有料 API を起動する書込操作の帰属・監査 W-3）。
            // クレーム前に記録すると「拒否された要求」まで実行として残るため、成立後に出す。
            // クレーム済みの区間なので、この行も try の内側に置く（例外が出ても processing で
            // 取り残さず failed 記録に落とす。CreateAndStartAsync 側と同じ判断）。
            _logger.LogInformation(
                "副資材チェックを再実行します（checkId: {CheckId}、実行者: {User}）", checkId, requestedBy);

            // 新規作成と同じく、応答は受付時点（クレーム直後の processing）の状態にする。
            accepted = await GetDetailRequiredAsync(checkId, cancellationToken);
        }
        catch
        {
            // クレーム済みだがバックグラウンドを起動できない区間。実行権を握ったまま放置せず
            // failed を記録する（無保護区間を作らない）。受付枠の解放は using が行う。
            await RecordFailureAsync(checkId, BuildFailureMessage(
                ErrorCodes.Unexpected, "再実行の受付後に応答の生成へ失敗しました。再実行してください。"));
            throw;
        }

        // クレーム後は status=processing で実行権を占有している。AI 実行前の準備（商品情報・
        // 画像バイナリの DB 読取）はバックグラウンド側で行い、そこでの失敗も failed 記録に落とす
        // （無保護区間を作らない）。無保護にすると DB 障害等で processing のまま残り、
        // 孤児判定（ProcessingStaleAfter）まで rerun が拒否される＝回復パス自体が塞がるため
        // （原則4・原則6）。以降、予約の解放責務はバックグラウンドタスク側に移る。
        StartBackgroundRun(checkId, prepared: null, lease);

        return accepted;
    }

    /// <summary>
    /// AI 実行本体を HTTP リクエストから切り離してバックグラウンドで起動する
    /// （<c>MartController.Rebuild</c> と同じ流儀・原則3）。
    /// <para>
    /// scoped 依存（Repository / Service）はリクエスト終了とともに破棄されるため<b>捕捉せず</b>、
    /// <see cref="IServiceScopeFactory"/> から専用スコープを作って解決し直す
    /// （破棄済み DbConnection / Repository 参照の防止）。捕捉するのは checkId と純粋なデータ、
    /// およびシングルトンの <see cref="IServiceScopeFactory"/> / <see cref="ILogger{T}"/> のみ。
    /// トークンは渡さない（＝<see cref="CancellationToken.None"/> 相当。クライアント切断で
    /// AI 実行を中断させない）。
    /// </para>
    /// </summary>
    /// <param name="prepared">
    /// 実行入力。新規作成はリクエストで確保済みの入力を渡す。null（rerun）はバックグラウンド側で
    /// DB から読み直す。
    /// </param>
    /// <param name="lease">
    /// 呼出元が保持している受付枠の予約。起動と同時に解放責務をバックグラウンドへ移すため、
    /// 呼出元の <c>using</c> は以降なにもしない（＝二重解放しない）。
    /// </param>
    private void StartBackgroundRun(Guid checkId, AiRunInput? prepared, AiCheckSlotLease lease)
    {
        // シングルトン依存はローカルへ写してから閉じ込める。ラムダが this を捕捉しなくなるため、
        // リクエストスコープのサービス・リポジトリを保持しないことが構造的に保証される。
        var scopeFactory = _scopeFactory;
        var logger = _logger;

        // 受付枠の解放責務をバックグラウンドへ移す（以降、解放するのは owned だけ）。
        var owned = lease.HandOffToBackground();

        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<SubsidiaryCheckService>();
                    await service.RunBackgroundAsync(checkId, prepared);
                }
                catch (Exception ex)
                {
                    // RunBackgroundAsync は内部で全経路を failed 記録に落とすため、ここへ来るのは
                    // スコープ生成・解決の失敗（プロセス停止時の ObjectDisposedException 等）だけ。
                    // failed 記録もできない状態のため processing のまま残り、
                    // ProcessingStaleAfter 経過後の rerun（孤児回復パス）で回復する。
                    logger.LogError(ex,
                        "副資材チェックのバックグラウンド実行を開始できませんでした（checkId: {CheckId}）",
                        checkId);
                }
                finally
                {
                    owned.Dispose();
                }
            });
        }
        catch
        {
            // タスクをスケジュールすらできなかった場合（プロセス停止時等）。
            // 引き取った解放責務をここで果たす（受付枠を漏らさない）。
            owned.Dispose();
            throw;
        }
    }

    /// <summary>
    /// バックグラウンド実行の本体（新規作成・rerun 共通）。専用スコープで解決したインスタンスで動く。
    /// 準備（DB 読取）と AI 実行のいずれの失敗も failed 記録に落とし、例外を外へ出さない。
    /// </summary>
    private async Task RunBackgroundAsync(Guid checkId, AiRunInput? prepared)
    {
        var input = prepared;
        if (input is null)
        {
            try
            {
                input = await LoadRunInputAsync(checkId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "副資材チェックの再実行準備に失敗しました（checkId: {CheckId}）", checkId);
                var message = ex is AppException app
                    ? BuildFailureMessage(app.Error, app.Message)
                    : BuildFailureMessage(ErrorCodes.Unexpected, "再実行してください。");
                await RecordFailureAsync(checkId, message);
                return;
            }
        }

        await RunAiAsync(checkId, input.Product, input.ProductLabel, input.Images);
    }

    /// <summary>保存済みレコードから AI 実行入力（商品情報・ラベル・画像）を読み出す（rerun 用）。</summary>
    private async Task<AiRunInput> LoadRunInputAsync(Guid checkId)
    {
        if (RerunPrepareFailureOverride?.Invoke(checkId) is { } injected)
        {
            throw injected;
        }

        var current = await GetDetailRequiredAsync(checkId);

        // 商品が後から削除されている場合（FK SET NULL）は商品情報なしで再実行する。
        var product = current.Summary.ProductId is { } productId
            ? await _repository.GetProductInfoAsync(productId)
            : null;

        var stored = await _repository.GetImagesWithDataAsync(checkId);
        return new AiRunInput(
            product,
            current.Summary.ProductLabel,
            ToAiImages(
                stored.Where(i => i.Kind == SubsidiaryCheckImageKind.Instruction).ToList(),
                stored.Where(i => i.Kind == SubsidiaryCheckImageKind.Tag).ToList()));
    }

    /// <summary>
    /// 受付枠（実行中＋待機中の総数）を1つ予約する。上限に達している場合は予約せず false。
    /// 呼出は <see cref="AiCheckSlotLease"/> 経由に限る（解放とスコープで対にするため）。
    /// タイムアウト 0 のため待機せず即座に判定する（受付可否の判定であり、順番待ちではない）。
    /// </summary>
    internal static bool TryReserveAiCheckSlot() => AcceptanceSemaphore.Wait(TimeSpan.Zero);

    /// <summary>
    /// 予約した受付枠を解放する。呼出は <see cref="AiCheckSlotLease.Dispose"/> 経由に限る
    /// （1予約につき1回だけ実行されることをスコープ側が保証する）。
    /// <para>
    /// 予約と解放の対称性が崩れて過剰解放になった場合、<see cref="AcceptanceSemaphore"/> は
    /// maxCount を明示しているため（遅くとも全枠が解放される時点までに）
    /// <see cref="SemaphoreFullException"/> を投げる。
    /// 受付上限（＝ピークメモリの防御）はセマフォ側で維持されるが、
    /// 原因調査のためログで顕在化させる（例外が出た解放が原因箇所とは限らない点に注意）。
    /// <see cref="AiCheckSlotLease.Dispose"/> から例外を投げると
    /// using の巻き戻し中に本来のエラーを覆い隠す（＋バックグラウンドの finally では
    /// 未観測例外になる）ため、ここで握り潰す（原則4）。
    /// </para>
    /// </summary>
    internal static void ReleaseAiCheckSlot(ILogger logger)
    {
        try
        {
            AcceptanceSemaphore.Release();
        }
        catch (SemaphoreFullException ex)
        {
            try
            {
                logger.LogError(ex,
                    "副資材チェックの受付枠が過剰に解放されました（予約と解放の対称性が崩れています）。");
            }
            catch
            {
                // ログ基盤側の失敗で解放処理を失敗させない（同上・原則4）。
            }
        }
    }

    /// <summary>
    /// 受付枠を予約したスコープを返す。上限に達している場合は 429（UNDX-REQ-009）。
    /// <para>
    /// <b>画像バッファを確保する前に呼ぶこと。</b> バッファ確保後に呼ぶと、429 で拒否される要求まで
    /// 1件あたり最大 <see cref="MaxTotalImageBytes"/>（20MB）を確保することになり、
    /// 受付上限がピークメモリを有界化しなくなる（<see cref="MaxConcurrentAiChecks"/> の収支を参照）。
    /// </para>
    /// 返り値は <c>using</c> で囲み、成功時のみ <see cref="StartBackgroundRun"/> が
    /// 解放責務をバックグラウンドへ移す（それ以外の全経路ではスコープ終了時に解放される）。
    /// </summary>
    public AiCheckSlotLease ReserveAiCheckSlotOrThrow() => AiCheckSlotLease.Reserve(_logger);

    /// <summary>
    /// クレームできなかった（0 行）ときの拒否理由を、現在状態を読み直して構築する。
    /// completed（確定済み）と processing（実行中）でメッセージを出し分ける。
    /// </summary>
    private async Task<AppException> BuildRerunRejectedAsync(
        Guid checkId, CancellationToken cancellationToken)
    {
        var latest = await GetDetailRequiredAsync(checkId, cancellationToken);
        var reason = latest.Summary.Status == SubsidiaryCheckStatus.Completed
            ? "このチェックは完了（completed）済みのため再実行できません（確定した判定結果は保護されます）。"
            : "このチェックは現在実行中のため再実行できません。"
              + $"完了しない場合は{(int)ProcessingStaleAfter.TotalMinutes}分経過後に再実行できます。";
        return new AppException(ErrorCodes.InvalidRequest, 400, reason);
    }

    /// <summary>詳細を取得する。未存在は 404（UNDX-DATA-005）。</summary>
    public async Task<SubsidiaryCheckDetail> GetDetailRequiredAsync(
        Guid checkId, CancellationToken cancellationToken = default) =>
        await _repository.GetDetailAsync(checkId, cancellationToken)
        ?? throw new AppException(ErrorCodes.SubsidiaryCheckNotFound, 404);

    /// <summary>
    /// 画像配信（1枚あたり最大 <see cref="MaxImageSizeBytes"/>）の同時実行数の上限。
    /// <para>
    /// <b>なぜ必要か:</b> AI 実行経路は <see cref="MaxConcurrentAiChecks"/> と
    /// <see cref="AiCallSemaphore"/> でピークメモリが構造的に有界化されているが、画像配信には
    /// 認証以外の制限が無かった。詳細画面は1回開くだけで最大 13 枚
    /// （<see cref="MaxInstructionImages"/>＋<see cref="MaxTagImages"/>）を並列取得するため、
    /// 上限が無いと「同時閲覧者数 × 1チェックの画像合計（最大 20MiB）」が
    /// そのままピークメモリになり、GC ハードリミット 384MiB に対するヘッドルーム
    /// （約74MiB・<c>docs/design.md</c> §13.5）を 2 名程度の同時閲覧で超えうる。
    /// </para>
    /// <para>
    /// <b>値の根拠:</b> 1枠のピークは、DB 読取中が「byte[]（最大5MiB）＋ Npgsql の
    /// オーバーサイズバッファ（同量程度）」で約 10MiB、応答書き出し中が byte[] のみで約 5MiB。
    /// 全枠が同時に読取中という最悪ケースで 4 × 10MiB ＝ <b>約 40MiB がこの経路の上限</b>。
    /// </para>
    /// <para>
    /// <b>枠は応答本文の書き出しが終わるまで保持すること。</b> byte[] は
    /// <see cref="Microsoft.AspNetCore.Mvc.ControllerBase.File(byte[], string, string)"/> の
    /// ような <c>IActionResult</c> に載せて返すと、MVC が結果を実行する
    /// （＝本文を書き出す）のは<b>アクション復帰後</b>であり、枠を DB 読取だけで解放すると
    /// byte[] の生存が枠の外へ出る。書き出しに要する時間は本文を引き取る側の速度に依存し、
    /// 逆プロキシのバッファ設定（本リポジトリの管理外）次第で長くなりうるため、
    /// 解放が早いと「同時閲覧者数に比例」する状態に戻る。
    /// このためコントローラは本文を自分で書き出し、書き終えるまで枠を保持する。
    /// </para>
    /// </summary>
    public const int MaxConcurrentImageDownloads = 4;

    /// <summary>
    /// 画像配信の同時実行数を制限するセマフォ。
    /// <para>
    /// <b>待機側がメモリを持たないことが前提:</b> byte[] の実体化は
    /// <see cref="SubsidiaryCheckRepository.GetImageAsync"/> の内側で起きるため、
    /// 枠を<b>取得してから</b>読み出す限り、順番待ち中のリクエストが保持するのは
    /// HTTP コンテキストのみで画像バイト列は持たない。したがって待機件数はピークメモリに
    /// 寄与せず、件数上限（AI 実行側の <see cref="MaxConcurrentAiChecks"/> に相当するもの）を
    /// 別途設ける必要がない。<b>読み出しを枠の外へ動かさないこと。</b>
    /// 枠の保持区間が「読取のみ」から「読取＋応答の書き出し」へ延びた分、順番待ちは
    /// 以前より深くなりうるため、この前提の重要度はむしろ上がっている。
    /// </para>
    /// maxCount を明示し、解放の非対称が <see cref="SemaphoreFullException"/> で
    /// 顕在化するようにする（<see cref="AcceptanceSemaphore"/> と同じ流儀・原則3）。
    /// </summary>
    private static readonly SemaphoreSlim ImageDownloadSemaphore =
        new(MaxConcurrentImageDownloads, MaxConcurrentImageDownloads);

    /// <summary>
    /// 画像配信の順番待ち上限（30秒）。
    /// <para>
    /// AI 呼出（<see cref="AiCallQueueTimeout"/> ＝ 420秒）と違い、画像配信は利用者が
    /// 画面の前で待つ同期リクエストのため、待ちは短く打ち切る。
    /// 超過は 429（<see cref="ErrorCodes.AiCheckBusy"/>）とし、利用者は再読込で復帰できる
    /// （ギャラリーは1枚単位で失敗を表示するため、全体は止まらない・原則4）。
    /// </para>
    /// <para>
    /// <b>トレードオフ（受容）:</b> 枠は応答の書き出し完了まで保持するため、本文の引き取りが
    /// 遅い経路では1枠を長く占有しうる。その間に多数の同時閲覧が重なると 429 が増えるが、
    /// これは「メモリを守るために表示を待たせる」意図した縮退であり、
    /// OOM でコンテナごと落ちる（実行中のチェックが processing のまま滞留する）よりは軽い。
    /// クライアント側の同時取得を 3 に絞って（<c>SUBSIDIARY_GALLERY_FETCH_CONCURRENCY</c>）、
    /// 1名が全枠を占有しにくくしている。
    /// </para>
    /// </summary>
    private static readonly TimeSpan ImageDownloadQueueTimeout = TimeSpan.FromSeconds(30);

    /// <summary>順番待ち上限のテスト用オーバーライド（null＝<see cref="ImageDownloadQueueTimeout"/>）。</summary>
    internal static TimeSpan? ImageDownloadQueueTimeoutOverride;

    /// <summary>
    /// 画像配信の枠を1つ取得する。順番待ちが上限を超えた場合は 429（UNDX-REQ-009）。
    /// 取得した枠は <see cref="ImageDownloadSlotLease.Dispose"/> で必ず解放すること
    /// （<c>using</c> スコープが応答本文の書き出しまでを覆う位置に置く）。
    /// </summary>
    public async Task<ImageDownloadSlotLease> AcquireImageDownloadSlotAsync(
        CancellationToken cancellationToken = default)
    {
        var queueTimeout = ImageDownloadQueueTimeoutOverride ?? ImageDownloadQueueTimeout;
        if (!await ImageDownloadSemaphore.WaitAsync(queueTimeout, cancellationToken))
        {
            throw new AppException(ErrorCodes.AiCheckBusy, 429,
                "画像の取得が混み合っています。しばらく待ってから再読込してください。");
        }

        return new ImageDownloadSlotLease();
    }

    /// <summary>
    /// 画像バイナリを取得する。未存在は 404（UNDX-DATA-005）。
    /// <b>必ず <see cref="AcquireImageDownloadSlotAsync"/> で取得した枠の内側で呼ぶこと</b>
    /// （枠の外で呼ぶとピークメモリの有界化が崩れる）。
    /// </summary>
    public async Task<SubsidiaryCheckImagePayload> GetImageRequiredAsync(
        Guid checkId, Guid imageId, CancellationToken cancellationToken = default)
        => await _repository.GetImageAsync(checkId, imageId, cancellationToken)
           ?? throw new AppException(ErrorCodes.SubsidiaryCheckNotFound, 404,
               "指定された画像が見つかりません。");

    /// <summary>
    /// 画像の枚数・形式・サイズ（各上限＋合計上限）を検証する
    /// （コントローラの事前チェックと二重でも安全な再検証）。
    /// <para>
    /// 検証順序は意図的に「不足枚数 → 超過枚数 → 各画像（形式→サイズ→中身）→ 合計サイズ」とする:
    /// 利用者が最初に直すべき事項（そもそも枚数が足りない／多すぎる）を優先して提示し、
    /// 個別画像の不備は「合計を減らす」より具体的な指示になるため合計サイズより先に返す。
    /// </para>
    /// </summary>
    public static void ValidateImages(
        IReadOnlyList<SubsidiaryCheckImageUpload> instructionImages,
        IReadOnlyList<SubsidiaryCheckImageUpload> tagImages)
    {
        if (instructionImages.Count < MinInstructionImages || tagImages.Count < MinTagImages)
        {
            throw new AppException(ErrorCodes.SubsidiaryImageMissing, 400,
                $"指示書画像 {instructionImages.Count} 枚・タグ画像 {tagImages.Count} 枚が指定されました。"
                + $"指示書画像は {MinInstructionImages}〜{MaxInstructionImages} 枚、"
                + $"タグ画像は {MinTagImages}〜{MaxTagImages} 枚が必要です。");
        }

        if (instructionImages.Count > MaxInstructionImages || tagImages.Count > MaxTagImages)
        {
            throw new AppException(ErrorCodes.SubsidiaryImageTooMany, 400,
                $"指示書画像は {MaxInstructionImages} 枚以内、タグ画像は {MaxTagImages} 枚以内にしてください"
                + $"（指定: 指示書 {instructionImages.Count} 枚 / タグ {tagImages.Count} 枚）。");
        }

        foreach (var image in instructionImages.Concat(tagImages))
        {
            ValidateImage(image);
        }

        var totalBytes = instructionImages.Concat(tagImages).Sum(image => image.Data.LongLength);
        EnsureTotalSizeWithinLimit(totalBytes);
    }

    /// <summary>
    /// 全画像の合計サイズが上限（<see cref="MaxTotalImageBytes"/>）以内であることを検証する。
    /// 超過時は 413（UNDX-REQ-008）。コントローラのバッファ確保前チェックと本検証で共用する。
    /// </summary>
    public static void EnsureTotalSizeWithinLimit(long totalBytes)
    {
        if (totalBytes > MaxTotalImageBytes)
        {
            throw new AppException(ErrorCodes.UploadTotalTooLarge, 413,
                $"画像の合計サイズが上限（{MaxTotalImageBytes / 1024 / 1024}MB）を超えています"
                + $"（指定: 約 {totalBytes / 1024.0 / 1024.0:F1}MB）。"
                + "画像の枚数を減らすか、解像度を下げて再試行してください。");
        }
    }

    /// <summary>
    /// Content-Type が許可形式（JPEG / PNG）であることを検証する。
    /// コントローラのバッファ確保前チェックと <see cref="ValidateImage"/> で共用する。
    /// </summary>
    public static void EnsureAllowedContentType(string fileName, string contentType)
    {
        // media type は RFC 9110 上 case-insensitive のため大文字小文字を区別せず比較する。
        if (!AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            throw new AppException(ErrorCodes.SubsidiaryImageInvalidFormat, 400,
                $"{fileName} の形式（{contentType}）には対応していません（JPEG / PNG のみ）。");
        }
    }

    /// <summary>
    /// 画像1枚の形式・サイズを検証する（バッファ確保後の最終検証）。
    /// Content-Type の申告だけでなく、先頭バイト（マジックバイト）が JPEG（FF D8 FF）/
    /// PNG（89 50 4E 47）として妥当かも検証する（Content-Type 偽装対策）。
    /// </summary>
    public static void ValidateImage(SubsidiaryCheckImageUpload image)
    {
        EnsureAllowedContentType(image.FileName, image.ContentType);

        if (image.Data.LongLength == 0)
        {
            throw new AppException(ErrorCodes.SubsidiaryImageInvalidFormat, 400,
                $"{image.FileName} が空ファイルです。");
        }

        if (image.Data.LongLength > MaxImageSizeBytes)
        {
            throw new AppException(ErrorCodes.SubsidiaryImageTooLarge, 413,
                $"{image.FileName} のサイズが上限（{MaxImageSizeBytes / 1024 / 1024}MB）を超えています。");
        }

        // 分岐も Content-Type と同じく大文字小文字非依存で判定する（"IMAGE/PNG" を JPEG 扱いにしない）。
        var isPng = string.Equals(image.ContentType, PngContentType, StringComparison.OrdinalIgnoreCase);
        var magicValid = isPng ? HasPngMagic(image.Data) : HasJpegMagic(image.Data);
        if (!magicValid)
        {
            throw new AppException(ErrorCodes.SubsidiaryImageInvalidFormat, 400,
                $"{image.FileName} のファイル内容が {image.ContentType} の画像形式ではありません"
                + "（JPEG / PNG のみ対応）。");
        }
    }

    /// <summary>JPEG のマジックバイト（FF D8 FF）を持つか。</summary>
    private static bool HasJpegMagic(byte[] data)
        => data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    /// <summary>PNG のマジックバイト（89 50 4E 47）を持つか。</summary>
    private static bool HasPngMagic(byte[] data)
        => data.Length >= 4 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;

    /// <summary>
    /// AI チェックを実行し、結果（completed / failed）を記録する（バックグラウンドタスク上で動く）。
    /// AI 失敗・応答解析失敗・AI 呼出タイムアウト・順番待ち超過はいずれも throw せず failed 記録に変換する
    /// （呼出元はバックグラウンドタスクであり、例外を返す先が存在しないため）。
    /// HTTP リクエストのキャンセルトークンは渡さない（クライアント切断で AI 実行を中断させない）。
    /// </summary>
    private async Task RunAiAsync(
        Guid checkId,
        SubsidiaryCheckProductInfo? product,
        string productLabel,
        IReadOnlyList<AiImageInput> aiImages)
    {
        try
        {
            var systemPrompt = SubsidiaryCheckPromptBuilder.BuildSystemPrompt();
            var userPrompt = SubsidiaryCheckPromptBuilder.BuildUserPrompt(product, productLabel);

            // AI 呼出のみを同時実行制限・タイムアウトで保護する（DB 操作はセマフォの外で行う）。
            // 待機は有界（AiCallQueueTimeout）。待機超過は AI を呼ばずに failed 記録に落とすことで、
            // 画像バッファを保持したまま滞留する時間を抑える（メモリ保護・CRITICAL）。
            // 滞留「件数」の抑制は受付上限（MaxConcurrentAiChecks）が担う。
            var queueTimeout = AiCallQueueTimeoutOverride ?? AiCallQueueTimeout;
            if (!await AiCallSemaphore.WaitAsync(queueTimeout))
            {
                _logger.LogWarning(
                    "副資材チェックの AI 実行が順番待ちタイムアウトしました"
                    + "（checkId: {CheckId}、上限: {Timeout}秒）",
                    checkId, (int)queueTimeout.TotalSeconds);
                await RecordFailureAsync(checkId, BuildFailureMessage(
                    ErrorCodes.AiCallFailed,
                    "AI 実行の順番待ちがタイムアウトしました。時間をおいて再実行してください。"));
                return;
            }

            string? responseText = null;
            var callTimedOut = false;
            var callTimeout = AiCallTimeoutOverride ?? AiCallTimeout;
            try
            {
                using var timeoutCts = new CancellationTokenSource(callTimeout);
                try
                {
                    responseText = await _aiClient.AnalyzeImagesAsync(
                        aiImages, systemPrompt, userPrompt, CheckMaxOutputTokens, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    // 自前のタイムアウト発火: throw せず failed 記録へ進む（記録はセマフォ解放後）。
                    // タイムアウト以外の OperationCanceledException（AI SDK 由来の想定外キャンセル）は
                    // ここで握らず、下の汎用 catch で failed 記録にする。
                    callTimedOut = true;
                }
            }
            finally
            {
                AiCallSemaphore.Release();
            }

            if (callTimedOut)
            {
                // タイムアウト: failed 記録＋failed Detail の返却で応答する（キャンセル扱いにしない）。
                _logger.LogWarning(
                    "副資材チェックの AI 呼出がタイムアウトしました（checkId: {CheckId}、上限: {Timeout}秒）",
                    checkId, (int)callTimeout.TotalSeconds);
                await RecordFailureAsync(checkId, BuildFailureMessage(
                    ErrorCodes.AiCallFailed, "AI 呼出がタイムアウトしました。再実行してください。"));
                return;
            }

            var parsed = SubsidiaryCheckResponseParser.Parse(responseText!);
            if (!parsed.Success)
            {
                _logger.LogWarning(
                    "副資材チェックの AI 応答を解析できませんでした（checkId: {CheckId}）: {Error}",
                    checkId, parsed.Error);
                await RecordFailureAsync(checkId, BuildFailureMessage(
                    ErrorCodes.AiResponseUnparseable, parsed.Error));
                return;
            }

            var (failCount, warnCount) = SubsidiaryCheckJudgment.Count(parsed.Findings);
            var judgment = SubsidiaryCheckJudgment.Decide(failCount, warnCount);

            var updated = await _repository.UpdateResultAsync(
                checkId, SubsidiaryCheckStatus.Completed, judgment, failCount, warnCount,
                SubsidiaryCheckRepository.SerializeFindings(parsed.Findings),
                _aiClient.ChatModel, errorMessage: null);
            if (updated == 0)
            {
                // 並行 rerun 等で先に completed が確定していた場合。記録保護（原則2）のため結果は破棄する。
                _logger.LogWarning(
                    "副資材チェックは既に completed のため結果を破棄しました（checkId: {CheckId}）", checkId);
            }
        }
        catch (Exception ex)
        {
            // AI 呼出失敗は主要フロー（記録）を止めない（原則4）。failed 記録に落とし、rerun で回復できる。
            // error_message には採番コード＋日本語の汎用文言のみを保存し、
            // 生の例外メッセージ（SDK 内部文言等）はログに留める（ユーザー露出防止）。
            _logger.LogWarning(ex, "副資材チェックの AI 実行に失敗しました（checkId: {CheckId}）", checkId);
            var message = ex is AppException app
                ? BuildFailureMessage(app.Error, app.Message)
                : BuildFailureMessage(ErrorCodes.Unexpected, "再実行してください。");
            await RecordFailureAsync(checkId, message);
        }
    }

    /// <summary>
    /// failed 記録用のエラーメッセージを全経路で同一形状
    /// 「<c>{コード}: {概要} {詳細}</c>」に統一して構築する。
    /// 詳細が空、または概要と同一（<see cref="AppException"/> の detail 未指定時は
    /// Message＝Summary になる）の場合は重複を避けてコード＋概要のみとする。
    /// </summary>
    private static string BuildFailureMessage(ErrorCodeInfo error, string? detail)
    {
        var head = $"{error.Code}: {error.Summary}";
        return string.IsNullOrWhiteSpace(detail) || detail.Trim() == error.Summary
            ? head
            : $"{head} {detail.Trim()}";
    }

    /// <summary>
    /// failed 記録を書き込む。バックグラウンド実行からの記録が確実に届くよう CancellationToken.None で
    /// 実行し、記録自体の失敗はログのみとする（記録失敗で元のエラーを覆い隠さない）。
    /// 既に completed のチェックは状態遷移ガード（記録保護・原則2）により更新されない（警告ログのみ）。
    /// </summary>
    private async Task RecordFailureAsync(Guid checkId, string errorMessage)
    {
        try
        {
            var updated = await _repository.UpdateResultAsync(
                checkId, SubsidiaryCheckStatus.Failed, judgment: null, failCount: 0, warnCount: 0,
                findingsJson: null, _aiClient.ChatModel, errorMessage, CancellationToken.None);
            if (updated == 0)
            {
                _logger.LogWarning(
                    "副資材チェックは既に completed のため失敗記録を破棄しました（checkId: {CheckId}）", checkId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "副資材チェックの失敗記録に失敗しました（checkId: {CheckId}）", checkId);
        }
    }

    /// <summary>
    /// 保存・表示・AI プロンプトに使う商品ラベルを決定する。
    /// クライアント指定値を優先し、未指定なら商品マスタから導出する。
    /// クライアント指定値のみ長さ上限（<see cref="MaxProductLabelLength"/>）を課す
    /// （マスタ由来の値は運用管理下の内部データで、外部からの増幅経路ではないため）。
    /// </summary>
    public static string ResolveProductLabel(string? productLabel, SubsidiaryCheckProductInfo? product)
    {
        if (!string.IsNullOrWhiteSpace(productLabel))
        {
            var trimmed = productLabel.Trim();
            if (trimmed.Length > MaxProductLabelLength)
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400,
                    $"商品ラベルは {MaxProductLabelLength} 文字以内で指定してください"
                    + $"（指定: {trimmed.Length} 文字）。");
            }

            return trimmed;
        }

        return product is null
            ? string.Empty
            : $"{product.ProductSign} {product.ProductTypeCrd} {product.ProductName}";
    }

    private static IReadOnlyList<SubsidiaryCheckImagePayload> BuildImageRecords(
        IReadOnlyList<SubsidiaryCheckImageUpload> instructionImages,
        IReadOnlyList<SubsidiaryCheckImageUpload> tagImages)
    {
        var records = new List<SubsidiaryCheckImagePayload>(instructionImages.Count + tagImages.Count);
        records.AddRange(instructionImages.Select((image, index) => new SubsidiaryCheckImagePayload(
            SubsidiaryCheckImageKind.Instruction, image.FileName, image.ContentType, image.Data, index)));
        records.AddRange(tagImages.Select((image, index) => new SubsidiaryCheckImagePayload(
            SubsidiaryCheckImageKind.Tag, image.FileName, image.ContentType, image.Data, index)));
        return records;
    }

    /// <summary>新規アップロード画像を AI 入力（指示書→タグの順・ラベル付き）へ変換する。</summary>
    private static IReadOnlyList<AiImageInput> ToAiImages(
        IReadOnlyList<SubsidiaryCheckImageUpload> instructionImages,
        IReadOnlyList<SubsidiaryCheckImageUpload> tagImages)
        => BuildAiImages(
            instructionImages.Select(i => (i.Data, i.ContentType)).ToList(),
            tagImages.Select(i => (i.Data, i.ContentType)).ToList());

    /// <summary>保存済み画像を AI 入力へ変換する（再実行用）。</summary>
    private static IReadOnlyList<AiImageInput> ToAiImages(
        IReadOnlyList<SubsidiaryCheckImagePayload> instructionImages,
        IReadOnlyList<SubsidiaryCheckImagePayload> tagImages)
        => BuildAiImages(
            instructionImages.Select(i => (i.Data, i.ContentType)).ToList(),
            tagImages.Select(i => (i.Data, i.ContentType)).ToList());

    /// <summary>
    /// AI へ渡す media type を正規化する（小文字化）。
    /// <para>
    /// 入力検証は media type を case-insensitive で受理するため（RFC 9110。
    /// <see cref="AllowedContentTypes"/> の比較は <see cref="StringComparer.OrdinalIgnoreCase"/>）、
    /// DB には "IMAGE/PNG" のような表記も保存されうる。AI クライアント側で完全一致比較を
    /// されると PNG が JPEG として送信され、外部 API に拒否されて<b>再実行しても直らない</b>
    /// 恒久失敗になる（保存済みの値は変わらないため）。境界を跨ぐ前にここで正規化して、
    /// 下流の比較方法に依存しないようにする。
    /// </para>
    /// 既存行（正規化前に保存されたもの）も読取時に本メソッドを通るため救済される。
    /// </summary>
    private static string NormalizeMediaType(string contentType) =>
        contentType.Trim().ToLowerInvariant();

    /// <summary>指示書→タグの順にラベル（「指示書画像（正） 1/3」等）を付けて AI 入力列を構築する。</summary>
    private static IReadOnlyList<AiImageInput> BuildAiImages(
        IReadOnlyList<(byte[] Data, string ContentType)> instructionImages,
        IReadOnlyList<(byte[] Data, string ContentType)> tagImages)
    {
        var images = new List<AiImageInput>(instructionImages.Count + tagImages.Count);
        images.AddRange(instructionImages.Select((image, index) => new AiImageInput(
            image.Data, NormalizeMediaType(image.ContentType),
            SubsidiaryCheckPromptBuilder.BuildImageLabel(
                SubsidiaryCheckImageKind.Instruction, index + 1, instructionImages.Count))));
        images.AddRange(tagImages.Select((image, index) => new AiImageInput(
            image.Data, NormalizeMediaType(image.ContentType),
            SubsidiaryCheckPromptBuilder.BuildImageLabel(
                SubsidiaryCheckImageKind.Tag, index + 1, tagImages.Count))));
        return images;
    }
}
