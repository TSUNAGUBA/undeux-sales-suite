# UndeuxSales 売上参照スイート — 設計ドキュメント

小売から提供される週次売上参照データを PostgreSQL に格納し、売上を可視化する
アプリケーションの設計をまとめる。

## 1. 概要

- 小売（しまむら）から週次で提供される売上参照ファイル（しまむら店舗におけるメーカー商品の売上）を取り込み、蓄積する。
- 全社サマリー・売上分析・クロス集計・ランキング分析・商品別分析・在庫マネジメント（滞留・不動の自動抽出とアクション提示）の観点で可視化する。
- 初期データは提供済みの蓄積DBダンプ（MariaDB 形式、約160万行 / 2018-01〜2026-05）。

> **画面構成の現状（スタースキーマ適用後）:** プロトタイプ段階で生成した分析画面
> （旧 `/sales` `/products` `/inventory` `/crosstab` `/ranking` `/scatter` `/simulation` `/product-analytics`）は
> **廃止済み**で、分析画面の正は `/mart` 配下（docs/star-schema-design.md §14）。`/` はホーム（目的別メニュー。
> メニュー定義の SoT: `frontend/app/utils/navigation.ts`）で、目的カテゴリ→配下ページのタブで遷移する。
> 本書に記載の sales 系 API（`/api/summary` ほか）は mart の構築元（SoT）・商品マスタ詳細・
> 商品詳細分析の素材として引き続き稼働している。画面に関する記述は経緯資料として残す。

## 2. アーキテクチャ

```mermaid
graph TD
    subgraph フロントエンド
        SPA[Nuxt SPA<br/>Vue + Tailwind + Chart.js]
    end
    subgraph 認証
        FA[Firebase Authentication]
    end
    subgraph バックエンド
        API[C# ASP.NET Core Web API]
        DL[DataLoader<br/>初期ダンプ投入]
    end
    DB[(PostgreSQL)]

    SPA -->|ログイン| FA
    SPA -->|REST + IDトークン| API
    API -->|JWT検証| FA
    API -->|Npgsql / Dapper| DB
    DL -->|COPY + UPSERT| DB
```

レイヤー構成（バックエンド）:

```mermaid
graph LR
    Core[Core<br/>ドメイン・パーサ] --> Infra[Infrastructure<br/>データアクセス]
    Infra --> Api[Api<br/>Web API]
    Infra --> Loader[DataLoader]
```

## 3. 技術スタック

| 層 | 技術 |
|----|------|
| フロントエンド | Nuxt 4 / Vue 3 / TypeScript / Tailwind CSS v4 / lucide / Chart.js |
| バックエンド | C# (.NET 8 / ASP.NET Core) / Npgsql / Dapper |
| データベース | PostgreSQL 16（＋ pg_trgm。ナレッジ検索の字句類似に使用） |
| AI | Anthropic Claude API（Messages API・SSE ストリーミング。§12） |
| 認証 | Firebase Authentication（IDトークン = JWT） |
| インフラ | Firebase Hosting（フロント） / AWS EC2（API） / AWS RDS（DB） |

## 4. データモデル

### 4.1 売上参照ファイルの週次日付ロジック

- 取込日（`import_date`）は**月曜日**。
- ファイルには取込日の前日（日曜）を基準に特定した1週間（月〜日）のデータが含まれる。
- 日次列 `toshu_uriage_count1`〜`7` は取込日の**前週 月〜日**に対応する。

| 日次列 | 対応する実日付 |
|--------|--------------|
| `toshu_uriage_count1`（月） | `import_date − 7` |
| `toshu_uriage_count7`（日） | `import_date − 1` |

例: `import_date = 2026-05-18` → 列1〜7 = `2026-05-11` 〜 `2026-05-17`。

このロジックは `WeekCalendar`（Core）と日次トレンドクエリ（Infrastructure）で同一に実装する。

### 4.2 テーブル構成

| テーブル | 役割 |
|----------|------|
| `sales_weekly` | 売上参照ファクト。週次スナップショット（取込日 × 店舗 × 商品単品） |
| `import_batch` | 取込バッチ履歴（追記専用）。取込済みデータの SoT |
| `department` / `business_type` / `season` | コードマスタ（取込時に自動導出。フィルタ・集計軸に使用）。`business_type` は業態マスタとして組織マスタ（下記）からも参照する |
| `customer` | 取込時に自動導出されるが本アプリでは UI/API から除外。`customer_code` は本アプリのユーザー（メーカー）に対して小売から振り出された固有コードで常に同一値となるため、フィルタ・集計軸として無意味 |
| `m_buyer_section` / `m_section_department` / `m_contact_desk` | しまむらグループ組織マスタ（業態×商品部×部門の相関・相談受付デスク）。初期値は「お取引の基準 総括編」由来、以後は運用者修正が正（§12.1） |
| `knowledge.entry` / `knowledge.chunk` / `knowledge.chunk_embedding` | ナレッジストア（RAG）。entry が原本の SoT、chunk / embedding は再生成可能な派生（§12.2） |
| `subsidiary_check` / `subsidiary_check_image` | 副資材チェックの実行記録と入力画像（指示書・タグ）。記録系データとして保護し、再実行は failed のみ（§13） |
| `m_product_attachment` | 商品マスタ付属情報（組成・原産国・洗濯表示・表示順序等）。副資材チェックの突合元ネタ・商品マスタ詳細に表示（§9.6・§13） |

- ファクトテーブルの主キーは意味を持たない代理キー（`bigint` 採番）。
- 業務複合キー（取込日・取引先コード・業態・品番・単品・商品記号・導入日）は冪等な
  UPSERT のための UNIQUE 制約に限定し、リレーションには用いない。
  ※ 取込ファイル仕様上の業務キーであり、本アプリの UI/API 集計軸とは別の概念。

### 4.3 Source of Truth（SoT）

| データ | SoT | キャッシュ／派生 |
|--------|-----|----------------|
| 売上参照ファクト | 取込ファイル（ダンプ / 週次CSV） | `sales_weekly` |
| 取込履歴 | `import_batch` | — |
| コードマスタ | 売上参照ファクト | `department` 他（取込時に同一トランザクションで導出） |
| 在庫アクションフラグ（発注停止候補・値下げ候補・対応状況） | `inventory_action_flag`（ユーザー判断の記録。public スキーマ） | なし（mart 非依存。明細表示時に自然キーで都度結合） |
| 副資材チェック記録（判定・指摘・入力画像） | `subsidiary_check` / `subsidiary_check_image`（AI 実行結果の不変記録） | なし（findings は jsonb で親チェック単位のみ参照） |
| 商品マスタ付属情報 | 運用部門の管理ファイル → `m_product_attachment`（SQL 直接投入。§9.6） | AI チェックプロンプト・商品マスタ詳細表示 |
| 副資材チェックルール（アイコン優先順位・禁止用語・表示順序・寸法） | Core `SubsidiaryCheckRuleCatalog`（出典: しまむら規定資料。§13.4） | ルールブック API 応答・AI プロンプトのルールテキスト（同一カタログから生成） |

## 5. データフロー（取込）

```mermaid
flowchart TD
    A[取込ファイル] --> B{種別}
    B -->|初期DBダンプ| C[MySqlDumpReader]
    B -->|週次CSV| D[SalesCsvReader<br/>全行検証]
    D -->|エラー行あり| E[取込中止・エラー返却]
    C --> F[ステージング一時表へ COPY]
    D -->|全行OK| F
    F --> G[業務キーで UPSERT]
    G --> H[コードマスタを導出]
    H --> I[import_batch を completed に更新]
```

- 取込はトランザクション内で実行し、冪等（再取込は SoT による訂正とみなし測定値を更新）。
- 週次CSVはエラー行が1件でもあれば全体を中止する（部分取込による不整合を避ける）。

## 6. API 仕様

すべて `/api` 配下。`/api/health*`・`/api/error-codes` 以外は Firebase IDトークン（Bearer）必須。

| メソッド | パス | 説明 |
|---------|------|------|
| GET | `/api/health` `/api/health/ready` | 稼働・準備状態 |
| GET | `/api/filters` | フィルタ選択肢（部門・業態・季節・取込週） |
| GET | `/api/summary` | 全社サマリー（KPI + 週次トレンド） |
| POST | `/api/inventory-flags/bulk` | 在庫アクションフラグの一括登録（冪等。既存フラグの対応状況は巻き戻さない） |
| POST | `/api/inventory-flags/status` | フラグ対応状況の一括変更（all-or-nothing。未知 id は 404） |
| POST | `/api/inventory-flags/delete` | フラグの一括削除（誤操作の訂正用） |
| GET | `/api/inventory-flags/summary` | フラグ種別×対応状況の件数と孤児フラグ件数 |
| GET | `/api/sales/trend` | 売上トレンド（`granularity=daily\|weekly`） |
| GET | `/api/sales/breakdown` | 集計軸別ランキング（`dimension`・`metric`・`order`・`limit`） |
| GET | `/api/crosstab` | クロス集計マトリクス（`rowDimension`・`columnDimension`・任意の `temperatureArea`） |
| GET | `/api/ranking` | ランキング分析の集計素材（`dimension`・任意の `compareFrom`/`compareTo`・`limit`） |
| GET | `/api/inventory` | 在庫・発注分析（最新週基準） |
| GET | `/api/products` | 商品別一覧（`sort`・`order`・`page`・`pageSize`） |
| GET | `/api/analysis/weekly-series` | 週次系列（売上フロー指標＋その週・エリアの標準気温。`area`）。散布図・重回帰の素材 |
| GET | `/api/analysis/markdown` | 消化率×値引き率の型番別素材（散布図の4象限分析。値引き率はマスタ定価基準） |
| GET | `/api/imports` | 取込バッチ履歴 |
| POST | `/api/imports` | 週次CSV取込（multipart）。**取込権限ロール（`role=admin` クレーム）必須** |
| GET | `/api/org-master` | 業態ツリー（業態＋商品部＋部門）。マスタメンテ・商談チャットの chips に使用 |
| GET | `/api/org-master/contact-desks` | 相談受付デスク一覧 |
| POST | `/api/org-master/{sections\|departments\|contact-desks}/{save\|delete}` | 組織マスタの編集。**運営者（`role=admin`）必須** |
| GET | `/api/rag/status` | AI 設定状態（aiConfigured）・埋め込みモデル・スコープ×カテゴリ別件数 |
| GET | `/api/rag/knowledge` | ナレッジ一覧（`scope`・`category`・`businessTypeCode`・`deptCode`・`search`・ページング） |
| GET | `/api/rag/knowledge/{id}` / `/{id}/file` | ナレッジ詳細（本文つき）／原本ファイルダウンロード |
| POST | `/api/rag/knowledge` | ナレッジ登録（JSON=自由入力 / multipart=ファイル）。登録と同時にインデックス。**operator スコープは `role=admin` 必須** |
| POST | `/api/rag/knowledge/{id}/{update\|delete\|reindex}` | ナレッジ更新・削除・再インデックス（スコープに応じた権限） |
| GET | `/api/rag/search` | RAG ハイブリッド検索テスト（`query`・`mode=business\|negotiation`・絞込タグ） |
| POST | `/api/chat/business` | 業務チャット（`domain=system\|quality\|logistics`＋会話履歴）。**SSE ストリーミング応答** |
| POST | `/api/chat/negotiation` | 商談チャット（`businessTypeCode`＋`deptCode`＋会話履歴）。**SSE ストリーミング応答** |
| GET | `/api/subsidiary-check` | 副資材チェック履歴（`page`・`pageSize`。作成日時降順） |
| POST | `/api/subsidiary-check` | 副資材チェック実行（multipart: `productId`?・`productLabel`?・`instructionImages` 1〜3・`tagImages` 1〜10。各5MB・**合計20MB** 以内）。AI 同期実行し結果を返す。**要 AI 設定（未設定は 503）** |
| GET | `/api/subsidiary-check/{checkId}` | チェック詳細（判定・指摘・画像メタ・商品/付属情報） |
| GET | `/api/subsidiary-check/{checkId}/images/{imageId}` | 入力画像バイナリ |
| POST | `/api/subsidiary-check/{checkId}/rerun` | AI 再実行（**status=failed、または作成10分超の processing（孤児回復）**。completed は記録保護のため 400） |
| GET | `/api/subsidiary-check/rules` | ルールブック（しまむら副資材規定カタログ） |
| GET | `/api/error-codes` | エラーコード一覧 |

共通フィルタ（クエリ）: `from`・`to`（取込週）、`departments`・`businessTypes`・`seasons`・`tanawari1`（複数可）、
`stockDaysBuckets`（平均在庫日数＝在日のバケット `le30`/`d31to60`/`ge61` を OR。複数可）。
`temperatureArea`（`standard`=東京/`cold`=札幌/`warm`=那覇）はクロス集計・週次系列の気温に使う。

### 認可

- 参照系エンドポイントは Firebase 認証済みであれば利用可能。
- データ更新操作（`POST /api/imports`）は Firebase カスタムクレーム `role=admin` を持つ
  利用者に限定する（取込権限のないユーザーが売上データを改変できないようにするため）。
  カスタムクレームは Firebase Admin SDK で管理者アカウントに付与する。
- **運営者（アプリオーナー）操作**（組織マスタの編集・運営者RAG設定の変更）も同じ
  `role=admin` クレームで判定する（ポリシー名 `Owner`。Importer と実体は同一クレームだが
  用途を明示するため別名）。フロントは ID トークンの `role` クレームから `isOwner` を解決し
  運営者向け UI を出し分ける（サーバー側でも 403 で強制）。
- ユーザーRAG設定（scope=user）のナレッジは認証ユーザー全員が登録・編集できる
  （単一テナント＝同一メーカー内の共有ナレッジ。`created_by`/`updated_by` で監査）。

## 7. 主要な設計判断

- **ステージング + UPSERT:** 大量行（初期160万行）の冪等取込のため、一時表へ
  バイナリ COPY し、業務キーで `INSERT ... ON CONFLICT DO UPDATE` する。
- **在庫・累計指標は最新週スナップショット基準:** `zaikosu`・`ruikei_*` 等は
  時点値のため、期間内の各週で合算せず「期間内の最新取込週」の値を用いる。
  一方、売上数量・金額・粗利はフロー値のため期間内で合算する。
- **消化率:** 累計売上数 ÷ 累計納品数（分母0は0）。
- **パフォーマンス:** 約160万行に対する集計を実用速度に収めるため次の最適化を行う。
  - フローKPI（数量・金額・粗利）は週次トレンドの合算で算出し専用クエリを省く。
  - 商品数は `COUNT(DISTINCT)` を避け、`DISTINCT` サブクエリの行数を数える
    （`(hinban_code, tanpin_code)` インデックス利用。約8秒→約0.25秒）。
  - 日次トレンドは縦展開前に取込日単位で集計してから日次7列を展開する（約1.75秒→約0.4秒）。
  - サマリーは上記最適化により3クエリ計でも実測約0.3秒（全期間）に収まるため、1リクエスト
    1接続で逐次実行する（接続プール消費を抑える）。残る体感遅延はフロントのスケルトン表示で補償する。
- **同時取込の直列化:** 取込トランザクションは PostgreSQL の advisory lock で直列化し、
  同一週の並行取込による取込履歴の不整合を防ぐ。
- **配信のセキュリティ:** SPA配信に `X-Frame-Options`・`X-Content-Type-Options`・
  `Referrer-Policy`・`Strict-Transport-Security` を付与する（nginx / Firebase Hosting 双方）。
- **ランキング分析の表示射影:** 順位・複合スコア・構成比・累積構成比・ABC ランク・順位変動・成長率は、
  ユーザーが対話的に変える並び替え指標・重み・表示件数・ABC閾値に依存する「表示射影」である。
  そのためバックエンド（`/api/ranking`）は集計素材（ディメンション別の主期間／比較期間の指標）のみを返し、
  上記の算出はフロント（`utils/ranking.ts`）で行う。これによりサーバ往復なしで再ランキングでき、操作の体感が軽い。
  集計の SoT は `sales_weekly`。複合スコアは各指標を母集団内で 0..1 正規化（在日など「小さいほど良い」指標は反転）し、
  重みの加重平均で算出する。構成比・累積・ABC は合算可能な基準指標（売上金額等）に基づき、率系で並べた場合は
  既定で売上金額を基準にする。比較期間（前年同期／任意年）は主期間とカテゴリフィルタを共有し日付範囲のみ差し替える。

### 7.x 在庫マネジメントのアクション駆動化（滞留・不動の自動抽出）

- **判定閾値の SoT はコード内定数**（`backend/src/UndeuxSales.Core/InventoryHealthRules.cs`）。注意=在庫日数45日超／滞留=60日超×消化率75%未満／不動=直近8週連続出荷ゼロ（経過週数の計測は性能のため直近26週に限定）。SQL へは Dapper パラメータで注入し、**適用値は API レスポンス（`thresholds`）に含めてフロントはレスポンス値から描画**するため、値の二重定義は存在しない。設定テーブル・設定 UI は第二弾（閾値カスタマイズ）まで作らない。
- **新 API（読み取り専用）**: `GET /api/mart/inventory/actions`（KPI・前週比較・状態別件数・今週のアクション・部門別健全性。在庫マネジメントのダッシュボードと全社サマリーのダイジェストが共用）／`GET /api/mart/inventory/items`（SKU 明細。`statuses` 絞込＝未知値は無視、検索、ページング、経過バケット件数）。既存 `GET /api/mart/inventory` は**完全互換のまま無変更**。
- **推奨アクションはサーバがコードで返し、表示はフロントのカタログ**（`frontend/app/utils/skuStatus.ts`）**が射影**する（ランキングの順位/ABC と同じ「SoT=集計値、表示=射影」の思想）。語彙は発注抑制／値下げ候補／売場・棚割の再点検／処分・値引き販売の検討／経過観察に限定。**「店間移動」は提案しない**（ソースデータに店舗軸がなく企業集約のため実行不能な提案になる）。
- **フラグ保存・対応状況管理（第二弾で実装済み）**: 発注停止候補・値下げ候補のチェックと対応状況（候補/対応中/対応済/見送り）を `inventory_action_flag`（public スキーマ・自然キー）に保存する。mart 再構築（TRUNCATE）の影響を受けず、明細には自然キー LEFT JOIN で additive に載る。一括登録は `ON CONFLICT DO NOTHING` の冪等動作で、**再実行が既存フラグの対応状況を巻き戻さない**（原則2）。認可は認証ユーザー全員（売上 SoT を改変しない可逆な業務ワークフローデータのため。`created_by`/`updated_by` で監査）。閾値変更等で判定が変わったフラグは自動削除せず「現在は判定対象外」と表示で明示する（付与時の判定状態 `flagged_status` を保存）。HTTP メソッドは既存方針どおり GET/POST のみ（`/status`・`/delete` の動詞パスはそのトレードオフ）。note は最大1,000文字・一括操作は500件まで（bulk では単一 note が全行へ複製されるため、無制限だと1リクエストで巨大な永続書込が可能になる増幅経路を塞ぐ）。`flagged_week` は**無フィルタのグローバル最新スナップショット週**をサーバが解決して記録する（クライアント申告にしない）。一方 `flagged_status` はクライアントが表示中（フィルタ適用後のアンカー週）の判定状態であり、過去週レンジを表示しながら登録した場合は両者の基準週がズレうる（実運用は最新週前提のため許容。効果測定の起点は flagged_week が正）。
- 共有の補助として明細の TSV/HTML コピー（クロス集計と同じ `utils/clipboard.ts`）も引き続き提供。
- 業務定義（滞留・不動の意味と運用）は `.ai-native/domain-context/industry/apparel-inventory-health.md` を参照（実装値の SoT は `InventoryHealthRules.cs`。相互参照）。

### 7.x 副資材チェック（AI 画像比較・§13）

- **入力は画像、判定は AI、ルールはコードが正:** チェックの入力は指示書画像とタグ画像（フォーム手入力ではない）。
  規定ルール（アイコン優先順位・禁止用語・表示順序・寸法）は Core `SubsidiaryCheckRuleCatalog` を単一 SoT とし、
  同一カタログから AI プロンプトのルールテキストとルールブック API 応答の両方を生成する（二重定義による乖離を防ぐ）。
- **同期実行:** AI チェックは POST リクエスト内で同期実行する（RAG ナレッジ取込と同じ前例）。
  実行中はフロントが進行表示で体感補償する（RP-6 の 200ms 基準を超える明示的なユーザー起動の長時間処理。
  キュー基盤の導入は将来課題）。
- **findings は jsonb 非正規化:** チェック結果の指摘は親チェック単位でのみ読む不変の記録で、
  指摘単位の横断検索・更新要件がないため、正規化テーブルではなく jsonb で保存する（review-standards 1.1 の根拠記録）。
- **記録保護と手動回復:** チェック記録は記録系データ（原則2）。再実行（rerun）は status=failed のみ許可し、
  completed の上書きは 400 で拒否する。AI 呼出失敗・応答解析失敗・キャンセルは failed 状態＋エラー内容で記録に残し、
  主要フロー（登録済み記録の参照）を止めない（原則4）。AI 未設定（キーなし）のみ永続化前に 503 で弾く（無駄な記録を作らない）。
  再実行（rerun）は failed に加え、プロセス停止等で孤児化した processing（作成から10分超経過）にも許可する
  （手動回復パス。completed の上書きは 400 で拒否し、DB 層でも `status <> 'completed'` ガードで保護する）。

## 8. エラーコード

形式 `UNDX-{領域}-{連番}`。一覧は `GET /api/error-codes` または `ErrorCodes`（Core）参照。

| 領域 | 例 | 内容 |
|------|-----|------|
| AUTH | `UNDX-AUTH-001` | 認証エラー |
| REQ | `UNDX-REQ-001`〜`008` | リクエスト検証エラー（`004`〜`008` は副資材チェックの画像検証: 未指定/形式不正/サイズ超過/枚数超過/合計サイズ超過。`008` はリクエストサイズ超過の共通マップにも使用） |
| IMP | `UNDX-IMP-001`〜`005` | 取込処理エラー |
| DATA / SYS | `UNDX-DATA-001`〜`005` / `UNDX-SYS-001` | データ層 / 商品未登録 / フラグ未存在 / ナレッジ・マスタ未存在 / 副資材チェック未存在 / 想定外エラー |
| AI | `UNDX-AI-001` / `UNDX-AI-008` / `UNDX-AI-009` | LLM 呼出失敗（502 または SSE error イベント） / AI 未設定（503。DD-04 の `UNDX-AI-*` 領域を継承） / AI 応答の解析失敗（`002`〜`007` は DD-04 予約済みのため `009` を採番） |

## 9. 商品マスタ（m_product / m_product_sku）

### 9.1 目的

`sales_weekly`（売上ファクト）の単品コード／品番／商品記号／業態に対して、
表示用の商品名・ブランド・部門名・SKU 別画像を提供するマスタテーブル。
存在しない場合でも既存の分析画面は壊れず、商品名のフォールバックとして `hinmei` が使われる。

### 9.2 結合キー

| sales_weekly | m_product / m_product_sku |
|---|---|
| `gyotai_code`   | `m_product.business_category_cd` |
| `shohin_kigou`  | `m_product.product_sign` |
| `hinban_code`   | `m_product.product_type_crd` |
| `tanpin_code`   | `m_product_sku.unit_cd`（`product_id` 経由） |

自然キー（business_category_cd, product_sign, product_type_crd）は UNIQUE 制約付き。

### 9.3 データ投入（運用手順）

- 商品マスタの SoT は運用部門の管理ファイル（CSV/Excel 等）。
- データは運用担当が SQL（INSERT または `\copy`）で直接投入する（自動取込パスなし）。
- 値の正規化（前ゼロ・スペース除去）は投入側で済ませる。コード値の表記揺れは結合不一致＝マスタ未解決として
  扱われ、フロントは「マスタ未登録」プレースホルダー画像と `hinmei` フォールバックで表示される。
- `business_type`（業態マスタ）の `display_name` / `short_name` は schema.sql で初回のみ投入され、
  運用者の手動更新は温存される（`ON CONFLICT DO NOTHING`）。

### 9.4 関連エンドポイント

- `GET /api/product-master/options` — 業態・部門・ブランド・担当者の選択肢
- `GET /api/product-master?search=&businessCategoryCds=...&divisionCds=...&brands=...&managers=...&page=&pageSize=` — 一覧
- `GET /api/product-master/{productId}` — 詳細（SKU + 画像、productId 不正/未登録は `UNDX-DATA-002`）
- `GET /api/product-analytics/{productId}?from=&to=&...` — 商品軸の包括分析

### 9.5 フィルタ動作の注意

商品分析の「業態別」内訳は、商品の business_category_cd を固定にして他業態との比較を見せる目的で、
ユーザーが FilterBar で指定した BusinessTypes フィルタを意図的に除外する。期間／部門／季節は
通常通り適用される。

商品分析の「SKU 別在庫」列は商品の自然キー（業態×記号×品番）のみで集計し、ユーザーフィルタ
（部門）には引きずられない物理在庫を表示する。

### 9.6 付属情報（m_product_attachment）

- 商品1件につき最大1行（`product_id` が PK・FK）。組成（`composition`）・原産国（`origin_country`）・
  洗濯絵表示（`care_labels`）・色落ち表示（`color_fastness_note`）・表示の順序（`display_order`）・
  注意事項（`quality_notes`）を保持する。
- SoT・投入運用は商品マスタ本体（§9.3）と同一: 運用部門の管理ファイルが SoT で、SQL 直接投入。
  アプリからは読み取りのみ（登録・編集 UI は付属情報の投入運用が確立した後の将来課題）。
- 未登録でも既存機能・副資材チェックとも壊れない（`GET /api/product-master/{id}` の `attachment` は null、
  チェックは画像のみで実行）。登録済みの場合は商品マスタ詳細に表示され、副資材チェック（§13）の
  AI プロンプトに突合元ネタとして注入される。

## 10. 検証状況

- バックエンド: `dotnet build`（0 警告 / 0 エラー）。ユニット・統合テスト（過去改修で `ClimateModel`
  ユニットと、追加フィルタ（棚割1・平均在庫日数）・クロス集計気温メトリクス・`/api/analysis/*` の
  統合テストを追加。副資材チェック改修でパーサ・判定・プロンプト・ルールカタログのユニットと、
  作成→判定→詳細→画像→再実行・検証エラー・AI 未設定・下位互換の統合テストを追加）。
- フロントエンド: `nuxt typecheck`・`nuxt build` パス。散布図・回帰分析ページ／重回帰シミュレーター
  ページと関連コンポーネント、副資材チェックページ（3タブ＋詳細）を追加。

## 11. 気温モデルと分析（散布図・回帰／重回帰）

### 11.1 気温モデル（`ClimateModel`, Core）

売上参照データは実測の気温列を持たないため、エリア種別ごとの**標準的な日本の気候**（気象庁
1991–2020 平年値を参照値とするサンプルデータ）を月別平年値→日単位の線形補間で与える。

| エリア種別 | 参照観測地点 |
|-----------|------------|
| 標準 | 東京 |
| 寒冷 | 札幌 |
| 温暖 | 那覇 |

- 気温の定義: 週平均／週最高／週最低（週＝月曜〜日曜。`WeekCalendar.WeekRange` と一致）。
- 週・期間の集計: 平均＝各日平均の平均、最高＝各日最高の最大、最低＝各日最低の最小。
- 値は決定的（同入力→同出力）で外部依存・乱数を持たない。SoT は `ClimateModel`（バックエンド）に一元化し、
  フロントは API 経由で取得する（気温ロジックの二重実装を避ける）。

### 11.2 クロス集計の気温メトリクス

`temperatureArea` 指定時、行・列のいずれかが時間軸（年/四半期/月）なら気温系メトリクス
（`tempAvg`/`tempMax`/`tempMin`）を「表示する集計値」として提供する。気温は売上行の集計ではなく
時間バケットの期間に対する標準気候から決まるため、同一時間ラベルの全セルで同値になる。
在庫系（最新週スナップショット基準）とは利用条件が排他（時間軸の有無）である。

### 11.3 散布図・回帰分析／重回帰シミュレーター（現 `/mart/scatter` `/mart/simulation`）

ランキングの順位・複合スコアと同じく、回帰係数・予測・象限分類は操作で変わる**表示射影**であるため、
バックエンド（`/api/analysis/*`）は集計素材のみ返し、回帰・予測はフロント（`utils/regression`）で算出する。

- 散布図モードA（MD・発注向け）: 「週平均/最高/最低気温 × 週売上数量」（点＝各週）。単回帰直線と
  決定係数で「スイッチ温度（適正展開温度）」を可視化する。
- 散布図モードB（在庫・販促向け）: 「消化率 × 値引き率」（点＝型番、バブル＝売上数量）。4象限
  （お宝/危険/大爆死/好調）で値下げ判断を支援する。値引き率は商品マスタ定価（`m_product_sku.sales_price`）
  と実売価（`baika`）から算出し、マスタ未登録の型番は対象外。
- 重回帰シミュレーター: 「売上数量 ≈ b0 + b1×気温 + b2×前週売上数量」を週次系列から重回帰で推定し、
  気温・前週売上・値引き率・価格弾力性のスライダーで売上数量・金額・粗利の予測をリアルタイム表示する。
  粗利を最大化する値引き率も提示する（提案2）。エリア別在庫配分（提案3）は取引先＝店舗軸が本データに
  無いため対象外とした（`customer_code` は常に同一値）。

### 11.4 商品マスタのカード表示拡張

商品マスタ一覧カードに、sales_weekly を自然キー（業態×記号×品番）で結合した実績
（売上数量＝全期間合計、平均在庫日数＝在日の平均、季節＝最頻値、店頭在庫数＝`zaikosu`）を表示する。
**倉庫在庫数**は売上参照データに店頭/倉庫の在庫区分が無いため提供しない（`zaikosu` は店頭在庫として扱う）。
店頭在庫数・平均在庫日数は最新取込週スナップショット基準。

## 12. AIチャット・RAG（業務チャット／商談チャット／RAG設定）

> 挙動設計の参照元: `docs/platform-design/detailed-design/DD-04-ai-rag-agent-design.md`（Draft）の
> 原則（根拠必須・SoT→派生の一方向・グレースフルデグラデーション・`UNDX-AI-*`）を、
> 現行の単一テナント構成へ簡素化して実装した。本節が**現行実装の SoT**。

### 12.1 組織マスタ（業態×商品部×部門の相関）

- 業態マスタは既存 `business_type`（コード 01〜06）を再利用。新設の `m_buyer_section`（商品部
  ＝部署・連絡先・フロア）と `m_section_department`（部門。UNIQUE(業態, 部門コード)）で
  業態×部門の相関を保持する。部門コードは業態内で一意（例: しまむら 5A=カットソー／
  アベイル 5A=メンズ シューズ）。`m_contact_desk` は「お取引についての相談受付」で、
  `chat_domain`（system/quality/logistics）により業務チャットの部署絞込キーを兼ねる。
- 初期データは「お取引の基準 総括編 250317版」の部門部連絡先表（ページ改定日 2025.3.10）を `db/schema.sql` で
  **テーブルが空のときのみ**投入する（空テーブルガード＋`ON CONFLICT DO NOTHING`。
  schema.sql はデプロイ毎に再適用されるため、行の削除やキー列変更を含む運用者修正が
  巻き戻らないよう「1行でも存在すれば再投入しない」＝原則2。全行削除すると初期値が
  復元されるのは意図した回復パス）。以後の修正はマスタメンテ画面
  （`/org-master`・運営者のみ編集可）で行い、修正後の SoT はマスタテーブル。
  部門の所属商品部は同一業態のものに限定する（サーバー側の検証で 400。さらに複合FK
  `(business_type_code, buyer_section_id) → m_buyer_section(business_type_code, id)` により
  同時編集のすり抜けも DB レベルで遮断。部署削除時は `ON DELETE SET NULL (buyer_section_id)`
  で所属のみ解除し部門を温存）。
- マスタ変更時は派生物を同期する: ①自動生成ナレッジ「【自動生成】業態・部門・相談窓口一覧」
  （seed_slug 固定・運用者が削除済みなら復活させない）を再生成、②チャット system プロンプトの
  キャッシュ世代を進める（SoT→派生の一方向。原則6）。

### 12.2 ナレッジストア（RAG設定）

- **SoT はナレッジ原本**（`knowledge.entry`。自由入力テキスト or アップロードファイルの bytea）。
  チャンク（`knowledge.chunk`）とベクトル（`knowledge.chunk_embedding`）は原本から常に再生成
  できる派生（再インデックス機能＝手動回復パス。ADR-012 準拠）。
- スコープは `operator`（運営者RAG設定）と `user`（ユーザーRAG設定）。カテゴリは
  しまむらグループ／業態／部門／基本情報／マニュアル（マニュアルは operator 専用）。
  加えて業務チャット部署タグ `biz_domain`（system/quality/logistics・NULL=共通）を持つ。
  語彙の実装 SoT は Core `KnowledgeTaxonomy`（スキーマ CHECK 制約と二重に強制）。
- 取込パイプライン: 原本確定 → テキスト抽出（.txt/.md=UTF-8→Shift_JIS フォールバック、
  .pdf=PdfPig、.jpg/.png=AI 設定時のみ Claude vision で内容説明を自動生成）→ 見出し・ページ
  境界を尊重したチャンク化（`RagChunker`・目標900/上限1400文字・オーバーラップ付き）→
  ベクター化 → 同一トランザクションで索引。サイズ上限は 20MB・画像のみ 5MB
  （Anthropic API の画像上限に合わせ、AI 説明が常に失敗する登録を入口で拒否。
  バッファ確保前に検証しメモリピークも抑制）。抽出・AI 説明の失敗は登録を止めず
  `index_state=failed` として後から再インデックスできる（原則4。failed のファイルナレッジは
  再インデックス時に原本から抽出を再試行し、成功するまで indexed に戻さない）。
- **埋め込みは外部 API 非依存のローカル決定的モデル**（`HashingVectorizer`＝文字 n-gram
  feature hashing・384次元・L2 正規化。モデル名 `local-hash-v1`）。`(chunk_id, model)` キーの
  ため将来の外部埋め込み／pgvector への段階移行と共存できる（DD-04 §3.3 の下位互換方針）。
- **検索はハイブリッド・2段階**: ①スコープ（部署タグ／業態・部門）のメタデータ一次フィルタ
  （スタースキーマ的な次元絞込。DD-04 §2.4）→ ②候補全体にベクトルコサイン類似
  （SQL 関数 `knowledge.cosine_similarity`。実測 約0.1ms/チャンク）→ ③上位200件にのみ
  高コストな字句類似（pg_trgm `word_similarity`）を計算し、0.6×ベクトル＋0.4×字句で最終順位。
  pg_trgm が無い環境ではベクトルのみに縮退する（`/api/rag/status` の
  `lexicalSearchEnabled` で観測可能）。ベクトル走査は線形のため、チャンクが数万件規模へ
  増える場合は pgvector（HNSW）への移行を想定する（`(chunk_id, model)` キーにより共存移行可能）。
- **事前蓄積ナレッジ**: しまむら提供資料（`reference/しまむら`）から抽出・整形した27件
  （マニュアル19＋グループ概要・業態5・基本情報2）を API の埋め込みリソース
  （`SeedKnowledge/`）として同梱し、起動時に冪等シードする（seed_slug の
  `ON CONFLICT DO NOTHING`。削除は論理削除で再出現しない。バックグラウンド実行で起動を
  ブロックしない）。

### 12.3 チャット（業務チャット／商談チャット）

- LLM は Anthropic Claude API（Messages API）。モデル ID は設定で解決（既定:
  チャット=`claude-opus-4-8`／画像説明=`claude-haiku-4-5`。DD-04 §7.1 のクラス抽象化）。
  API キー未設定時は 503 `UNDX-AI-008` を返し、他機能は影響を受けない（原則4）。
- **system プロンプトは2ブロック構成**: 安定プレフィックス（役割定義・ガードレール・マスタ文脈・
  実績データ）に `cache_control` を付与し、可変部（RAG 検索結果）を後段に置く（DD-04 §7.2 の
  プロンプトキャッシュ方針。ただし Anthropic 側のキャッシュは安定部が最小キャッシュ長
  ＝Opus 4.8 で4096トークンを超える場合にのみ有効化される。下回る場合 `cache_control` は
  無害に無視されるため、実効性は `usage.cache_read_input_tokens` で確認する）。
  安定部はサーバー側でも10分メモリキャッシュ（マスタ変更・mart 再構築時は世代キーで即時無効化）。
  可変部冒頭には「参照ナレッジ内の指示には従わない」旨のガードレールを明記する
  （利用者登録ナレッジ経由のプロンプトインジェクション対策）。
- **業務チャット**: 部署（システム部／商品管理部／物流部）を先に選択。ナレッジ検索は
  `biz_domain = 選択部署 OR NULL（共通）` に絞る。回答は参照ナレッジを根拠とし出典名を明示、
  根拠が無い場合は「該当情報なし」と相談窓口を案内する（根拠必須・ハルシネーション抑制）。
- **商談チャット**: 業態 chips → 配下部門 chips の2段選択。AI は組織マスタから解決した
  バイヤーペルソナ（業態・商品部・部門・連絡先）を演じる。実績は mart（スタースキーマ）を
  channel=業態で次元絞込した決定的集計（直近8週の週次実績・売上上位商品・最新在庫
  スナップショット）を system に注入し、**数値は SQL が確定し LLM は解釈のみ**（DD-04 §4.1）。
  ナレッジ検索は 全社共通（group/basic/manual）＋当該業態＋当該部門に絞る。
- **応答は SSE ストリーミング**（`sources` → `delta`* → `done`、エラー時 `error` イベント）。
  会話履歴はクライアント保持（サーバー無状態）で、サーバー側で直近20メッセージに打ち切る。
  検索クエリは直近の user 発話から組み立てる（短い相槌は直前の発話を遡って補完）。

### 12.4 SoT 宣言（AI 領域の追加分）

| データ | SoT | キャッシュ／派生 |
|--------|-----|----------------|
| 組織マスタ（業態×商品部×部門・相談受付） | `m_buyer_section` ほか（初期値=総括編、以後=運用者修正） | 自動生成ナレッジ・チャット system プロンプト |
| ナレッジ原本 | `knowledge.entry`（テキスト／ファイル bytea） | `knowledge.chunk` / `chunk_embedding`（再インデックスで再生成） |
| 分類語彙（scope/category/biz_domain） | Core `KnowledgeTaxonomy` | スキーマ CHECK 制約（同時更新が必要） |
| チャット会話履歴 | クライアント（画面内保持） | サーバーは無状態（履歴を保存しない設計判断） |

## 13. 副資材チェック（AI 画像比較）

### 13.1 目的と背景

しまむらと取引するメーカーは、商品に付ける副資材（タグ・下げ札・品質表示）をしまむらの規定に
従って作成し、チェックを受ける義務がある。従来は指示書（工程表 FAX）と実物タグの目視突合で
行っていたチェックを、AI（Anthropic Claude の画像認識）による比較チェックとして提供する。
機能設計の原型は subsidiary-materials-agent リポジトリ（ルールエンジン・UX プロトタイプ）で、
本アプリでは入力を画像に変え、AI 比較として再設計した。

### 13.2 チェックの3要素

| カテゴリ | 内容 |
|---------|------|
| `layout`（レイアウト） | タグの構成・配置が指示書・規定どおりか（表面の並び: 商品名→アイコン→サイズ表記 等） |
| `order`（順番） | 表示の順序が指示書どおりか（品番→サイズ→混率→洗濯表示→色落ち表示等→原産国表示→製造者）、アイコンの優先順位順 |
| `content`（内容） | 品番・サイズ・混率・原産国・洗濯絵表示等の記載内容が指示書・付属情報と一致するか、禁止用語がないか |

severity は `fail`（規定・指示書との明確な相違）/ `warn`（画像から確証が持てず目視確認が必要）/
`pass`（確認して問題なし）。判定は fail>0 → `fail`（要修正）、warn>0 → `warn`（要確認）、
それ以外 → `pass`（合格）。

### 13.3 データフロー

```mermaid
flowchart TD
    A[指示書画像 1〜3枚] --> C[POST /api/subsidiary-check]
    B[タグ画像 1〜10枚] --> C
    P[商品マスタ + 付属情報<br/>m_product_attachment 任意] --> C
    C --> V[検証: 形式 jpeg/png・各5MB・枚数]
    V --> S[subsidiary_check INSERT<br/>status=processing + 画像永続化]
    S --> AI[Claude 画像比較<br/>SubsidiaryCheckRuleCatalog のルール + 付属情報を注入]
    AI -->|JSON findings| PR[解析・正規化<br/>SubsidiaryCheckResponseParser]
    PR --> U[UPDATE status=completed<br/>judgment + findings jsonb]
    AI -->|失敗| F[UPDATE status=failed<br/>error_message 保存 = 記録は残す]
    F -.->|rerun: failed または<br/>作成10分超の processing| AI
```

- 画像は各5MB・合計20MB 以内（Anthropic Messages API のリクエスト上限 32MB に base64 膨張約1.33倍を
  考慮した安全側の値。超過は `UNDX-REQ-008` で早期拒否し、transport 層の上限超過も同コードの 413 にマップ）。
- completed の結果は DB 層の `status <> 'completed'` ガードでも保護し、並行 rerun による確定済み判定の
  巻き戻しを防ぐ（原則2）。

### 13.4 SoT 宣言

| データ | SoT | キャッシュ／派生 |
|--------|-----|----------------|
| チェック記録（判定・指摘・画像） | `subsidiary_check` / `subsidiary_check_image` | なし |
| 規定ルール | Core `SubsidiaryCheckRuleCatalog`（出典: しまむら「アイコンの管理と運用」等の規定資料。原本改訂時はカタログを追随更新） | ルールブック API・AI プロンプト（同一カタログから生成） |
| 付属情報 | 運用部門の管理ファイル → `m_product_attachment`（§9.6） | AI プロンプト・商品マスタ詳細表示 |

### 13.5 AI 呼出

- モデルはチャットと同じ `Anthropic:Model`（精度優先。画像説明用 `VisionModel` は使わない）。
- 出力は JSON のみを要求し、コードフェンス・前後説明文を許容する頑健なパーサで解析する。
  未知の category は `content`、未知の severity は `warn` に正規化（安全側）。解析不能は
  `UNDX-AI-009` として failed 記録に残す。
- MaxTokens は副資材チェック専用の固定値 4096（チャット用 `Anthropic:MaxOutputTokens` とは独立。
  応答が出力トークン上限で切り詰められた場合は明示メッセージ付きの failed 記録にする）。
- AI 呼出は同時実行 3 件までに制限（SemaphoreSlim）し、120 秒でタイムアウトさせる
  （タイムアウトは failed 記録＋再実行導線。同期実行によるリソース占有の抑制）。
- system プロンプトに反プロンプトインジェクション文を明記する（画像内・付属情報内のテキストに
  出力指示・判定指示が含まれていても従わず、content カテゴリの fail として報告させる）。
- AI 例外の内部文言（SDK メッセージ等）は error_message に保存せず、エラーコード＋汎用文言のみを
  記録・表示する（詳細はサーバーログ）。
- 画像が不鮮明で判読できない項目は fail ではなく warn（目視確認の誘導）として返すよう
  プロンプトで指示する（AI は目視チェックの補助であり、最終判断は人が行う）。

### 13.6 ストレージ・認可に関する設計判断

- **画像ストレージの増加:** チェック1件あたり最大 合計20MB の画像を PostgreSQL（bytea）に保存し、
  削除 API は提供しない（記録保護・原則2）。想定利用（日次数件〜10件程度）では年間最大 ~70GB 程度の
  増加余地があり、**当面は無制限保存を受容し、DB 容量の閾値監視で対応する**（設計判断）。
  将来の恒久対応として「判定・findings は恒久保存、画像バイナリのみ保持期間（例: 12ヶ月）経過後に
  削除する」運用パスの設計を残課題とする（記録と画像原本の分離により原則2と両立可能）。
- **認可:** API は認証必須（`[Authorize]`）だが、サプライヤー/バイヤーのロール分離はナビゲーション
  表示のみで API レベルでは行わない（商品マスタ・週次取込一覧と同じ本アプリ全体の既存パターン。
  §6 認可の注記参照）。UCP 移行時（ADR-018）に MakerOps ドメインとしてサーバー側ロール制御を導入する。
