# UndeuxSales 売上参照スイート — 設計ドキュメント

小売から提供される週次売上参照データを PostgreSQL に格納し、売上を可視化する
アプリケーションの設計をまとめる。

## 1. 概要

- 小売（しまむら）から週次で提供される売上参照ファイル（しまむら店舗におけるメーカー商品の売上）を取り込み、蓄積する。
- 全社サマリー・売上分析・クロス集計・ランキング分析・商品別分析・在庫発注分析の観点で可視化する。
- 初期データは提供済みの蓄積DBダンプ（MariaDB 形式、約160万行 / 2018-01〜2026-05）。

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
| データベース | PostgreSQL 16 |
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
| `department` / `business_type` / `season` | コードマスタ（取込時に自動導出。フィルタ・集計軸に使用） |
| `customer` | 取込時に自動導出されるが本アプリでは UI/API から除外。`customer_code` は本アプリのユーザー（メーカー）に対して小売から振り出された固有コードで常に同一値となるため、フィルタ・集計軸として無意味 |

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
| GET | `/api/sales/trend` | 売上トレンド（`granularity=daily\|weekly`） |
| GET | `/api/sales/breakdown` | 集計軸別ランキング（`dimension`・`metric`・`order`・`limit`） |
| GET | `/api/crosstab` | クロス集計マトリクス（`rowDimension`・`columnDimension`） |
| GET | `/api/ranking` | ランキング分析の集計素材（`dimension`・任意の `compareFrom`/`compareTo`・`limit`） |
| GET | `/api/inventory` | 在庫・発注分析（最新週基準） |
| GET | `/api/products` | 商品別一覧（`sort`・`order`・`page`・`pageSize`） |
| GET | `/api/imports` | 取込バッチ履歴 |
| POST | `/api/imports` | 週次CSV取込（multipart）。**取込権限ロール（`role=admin` クレーム）必須** |
| GET | `/api/error-codes` | エラーコード一覧 |

共通フィルタ（クエリ）: `from`・`to`（取込週）、`departments`・`businessTypes`・`seasons`（複数可）。

### 認可

- 参照系エンドポイントは Firebase 認証済みであれば利用可能。
- データ更新操作（`POST /api/imports`）は Firebase カスタムクレーム `role=admin` を持つ
  利用者に限定する（取込権限のないユーザーが売上データを改変できないようにするため）。
  カスタムクレームは Firebase Admin SDK で管理者アカウントに付与する。

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

## 8. エラーコード

形式 `UNDX-{領域}-{連番}`。一覧は `GET /api/error-codes` または `ErrorCodes`（Core）参照。

| 領域 | 例 | 内容 |
|------|-----|------|
| AUTH | `UNDX-AUTH-001` | 認証エラー |
| REQ | `UNDX-REQ-001`〜`003` | リクエスト検証エラー |
| IMP | `UNDX-IMP-001`〜`005` | 取込処理エラー |
| DATA / SYS | `UNDX-DATA-001` / `UNDX-DATA-002` / `UNDX-SYS-001` | データ層 / 商品未登録 / 想定外エラー |

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

## 10. 検証状況

- バックエンド: `dotnet build`（0 警告 / 0 エラー）。ユニット・統合テスト計 84 件パス（本改修で
  `/api/ranking` の統合テスト4件＝部門ランキング・期間比較・不正軸・空データ を追加）。
- フロントエンド: `nuxt typecheck`・`nuxt build` パス。ランキング分析ページ・関連コンポーネントを追加。
