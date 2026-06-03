-- ============================================================
--  UndeuxSales 売上参照スイート — PostgreSQL スキーマ
-- ------------------------------------------------------------
--  小売から提供される週次売上参照ファイルを格納し、売上を
--  可視化するためのスキーマ。元データ（MariaDB `sales` テーブル）の
--  構造を踏襲しつつ、PostgreSQL 向けに正規化・整理した。
--
--  設計方針:
--   * ファクトテーブル `sales_weekly` は意味を持たない代理キー (id) を
--     主キーとする。業務複合キーは UPSERT 用の UNIQUE 制約に限定する。
--   * 取込履歴 `import_batch` は追記専用。取込済みデータの SoT。
--   * コードマスタ (department / customer / business_type / season) は
--     取込時に同一トランザクションで自動導出される派生データ。
--   * 文字列カラムは text を採用（元データの varchar 長は MySQL の
--     ストレージ都合であり業務制約ではないため）。意味は COMMENT で記録。
-- ============================================================

BEGIN;

-- ------------------------------------------------------------
-- 取込バッチ（ingestion audit log） — 取込済みデータの SoT
-- ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS import_batch (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source_type     text NOT NULL
                        CHECK (source_type IN ('initial_dump', 'weekly_csv')),
    file_name       text NOT NULL,
    status          text NOT NULL DEFAULT 'processing'
                        CHECK (status IN ('processing', 'completed', 'failed')),
    row_count       integer NOT NULL DEFAULT 0,
    week_count      integer NOT NULL DEFAULT 0,
    min_import_date date,
    max_import_date date,
    error_message   text,
    started_at      timestamptz NOT NULL DEFAULT now(),
    completed_at    timestamptz
);

COMMENT ON TABLE  import_batch IS '取込バッチ履歴。1回の取込（初期DB投入 / 週次CSV）を1行で記録する追記専用テーブル';
COMMENT ON COLUMN import_batch.source_type IS '取込種別: initial_dump=初期DBダンプ投入 / weekly_csv=週次CSV取込';
COMMENT ON COLUMN import_batch.status      IS '取込状態: processing / completed / failed';
COMMENT ON COLUMN import_batch.week_count  IS 'このバッチに含まれる import_date（週）の種類数';

-- ------------------------------------------------------------
-- 売上参照ファクト（週次スナップショット）
-- ------------------------------------------------------------
--  1行 = ある取込日(週)における、ある店舗・商品単品の売上スナップショット。
--  日次列 toshu_uriage_count1..7 は import_date の前週 月〜日に対応する
--  （import_date が月曜のとき、月曜列 = import_date-7、日曜列 = import_date-1）。
-- ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sales_weekly (
    id                    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    import_batch_id       bigint NOT NULL REFERENCES import_batch(id),

    import_date           date NOT NULL,
    customer_code         text NOT NULL,
    gyotai_code           text NOT NULL,
    chohyo_kubun_name     text NOT NULL,
    department            text NOT NULL,
    hinban_code           text NOT NULL,
    tanpin_code           text NOT NULL,
    hinmei                text NOT NULL,
    shohin_kigou          text NOT NULL,
    color                 text NOT NULL,
    size                  text NOT NULL,
    tanawari1             text,
    tanawari2             text,

    toshu_uriage_count1   integer NOT NULL DEFAULT 0,
    toshu_uriage_count2   integer NOT NULL DEFAULT 0,
    toshu_uriage_count3   integer NOT NULL DEFAULT 0,
    toshu_uriage_count4   integer NOT NULL DEFAULT 0,
    toshu_uriage_count5   integer NOT NULL DEFAULT 0,
    toshu_uriage_count6   integer NOT NULL DEFAULT 0,
    toshu_uriage_count7   integer NOT NULL DEFAULT 0,

    uriage_count_zenshu   integer NOT NULL DEFAULT 0,
    uriage_count_2shumae  integer NOT NULL DEFAULT 0,
    uriage_count_3shumae  integer NOT NULL DEFAULT 0,
    uriage_count_4shumae  integer NOT NULL DEFAULT 0,

    zaikosu               integer NOT NULL DEFAULT 0,
    ruikei_uriage_count   integer NOT NULL DEFAULT 0,
    ruikei_nohin_count    integer NOT NULL DEFAULT 0,
    hatchu_count          numeric(10,1) NOT NULL DEFAULT 0,

    donyu_date            text NOT NULL,
    zainiti               integer NOT NULL DEFAULT 0,
    genka                 integer NOT NULL DEFAULT 0,
    baika                 integer NOT NULL DEFAULT 0,
    kisetsu               text NOT NULL,
    sakizuke_count        integer NOT NULL DEFAULT 0,

    source_created_at     timestamp,
    source_updated_at     timestamp,
    ingested_at           timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE  sales_weekly IS '売上参照ファクト。週次スナップショット（取込日 × 店舗 × 商品単品）';
COMMENT ON COLUMN sales_weekly.import_date          IS '取込日（月曜日）。元データ varchar→date';
COMMENT ON COLUMN sales_weekly.customer_code        IS '取引先コード（店舗・取引アカウント）。元 varchar(4)';
COMMENT ON COLUMN sales_weekly.gyotai_code          IS '業態コード。元 varchar(2)';
COMMENT ON COLUMN sales_weekly.chohyo_kubun_name    IS '帳票区分名。元 varchar(4)';
COMMENT ON COLUMN sales_weekly.department           IS '部門コード。元 varchar(2)';
COMMENT ON COLUMN sales_weekly.hinban_code          IS '品番コード。元 varchar(3)';
COMMENT ON COLUMN sales_weekly.tanpin_code          IS '単品コード。元 varchar(4)';
COMMENT ON COLUMN sales_weekly.hinmei               IS '品名。元 varchar(15)';
COMMENT ON COLUMN sales_weekly.shohin_kigou         IS '商品記号。元 varchar(12)';
COMMENT ON COLUMN sales_weekly.color                IS 'カラー。元 varchar(15)';
COMMENT ON COLUMN sales_weekly.size                 IS 'サイズ。元 varchar(11)';
COMMENT ON COLUMN sales_weekly.tanawari1            IS '棚割1。元 varchar(6) NULL可';
COMMENT ON COLUMN sales_weekly.tanawari2            IS '棚割2。元 varchar(6) NULL可';
COMMENT ON COLUMN sales_weekly.toshu_uriage_count1  IS '当週売上数量（月） = import_date-7 の売上数量';
COMMENT ON COLUMN sales_weekly.toshu_uriage_count7  IS '当週売上数量（日） = import_date-1 の売上数量';
COMMENT ON COLUMN sales_weekly.uriage_count_zenshu  IS '前週売上数量（週合計）';
COMMENT ON COLUMN sales_weekly.uriage_count_2shumae IS '2週前売上数量（週合計）';
COMMENT ON COLUMN sales_weekly.zaikosu              IS '在庫数（マイナス値あり＝調整・過剰販売）';
COMMENT ON COLUMN sales_weekly.ruikei_uriage_count  IS '累計売上数';
COMMENT ON COLUMN sales_weekly.ruikei_nohin_count   IS '累計納品数';
COMMENT ON COLUMN sales_weekly.hatchu_count         IS '発注数。元 decimal(10,1)';
COMMENT ON COLUMN sales_weekly.donyu_date           IS '導入日（YYYYMMDD 文字列。"0"=未設定）。元 varchar(8)';
COMMENT ON COLUMN sales_weekly.zainiti              IS '在日（導入からの経過日数）';
COMMENT ON COLUMN sales_weekly.genka                IS '原価（円）';
COMMENT ON COLUMN sales_weekly.baika                IS '売価（円）';
COMMENT ON COLUMN sales_weekly.kisetsu              IS '季節区分（例: 通季）。元 varchar(4)';
COMMENT ON COLUMN sales_weekly.sakizuke_count       IS '先付数';
COMMENT ON COLUMN sales_weekly.source_created_at    IS '元データの created_at（取込前の値を保持）';
COMMENT ON COLUMN sales_weekly.ingested_at          IS '当システムへ取り込んだ日時';

-- 業務複合キー: 冪等な UPSERT（再取込時の重複防止）のための UNIQUE 制約。
-- リレーションには使用しない（リレーションは代理キー id を使用）。
CREATE UNIQUE INDEX IF NOT EXISTS ux_sales_weekly_business_key
    ON sales_weekly (import_date, customer_code, gyotai_code,
                     hinban_code, tanpin_code, shohin_kigou, donyu_date);

-- 分析クエリ用インデックス
CREATE INDEX IF NOT EXISTS ix_sales_weekly_import_date   ON sales_weekly (import_date);
CREATE INDEX IF NOT EXISTS ix_sales_weekly_department    ON sales_weekly (department);
CREATE INDEX IF NOT EXISTS ix_sales_weekly_customer_code ON sales_weekly (customer_code);
CREATE INDEX IF NOT EXISTS ix_sales_weekly_kisetsu       ON sales_weekly (kisetsu);
CREATE INDEX IF NOT EXISTS ix_sales_weekly_gyotai_code   ON sales_weekly (gyotai_code);
CREATE INDEX IF NOT EXISTS ix_sales_weekly_batch         ON sales_weekly (import_batch_id);

-- 商品（品番・単品）単位の集計・件数算出を高速化する。
CREATE INDEX IF NOT EXISTS ix_sales_weekly_product
    ON sales_weekly (hinban_code, tanpin_code);

-- ------------------------------------------------------------
-- コードマスタ（取込時に自動導出される派生参照データ）
-- ------------------------------------------------------------
--  フィルタUIの選択肢として利用する（review-standards LAYER_1 1.3）。
--  SoT は売上参照ファイル。取込時に同一トランザクションで UPSERT され、
--  ファクトと原子的に整合する。display_name は将来の表示名付与用（任意）。
-- ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS department (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code         text NOT NULL UNIQUE,
    display_name text,
    created_at   timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE department IS '部門コードマスタ（取込時に自動導出）';

CREATE TABLE IF NOT EXISTS customer (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code         text NOT NULL UNIQUE,
    display_name text,
    created_at   timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE customer IS '取引先コードマスタ（取込時に自動導出）';

CREATE TABLE IF NOT EXISTS business_type (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code         text NOT NULL UNIQUE,
    display_name text,
    created_at   timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE business_type IS '業態コードマスタ（取込時に自動導出）';

CREATE TABLE IF NOT EXISTS season (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code         text NOT NULL UNIQUE,
    display_name text,
    created_at   timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE season IS '季節区分マスタ（取込時に自動導出）';

-- ------------------------------------------------------------
-- 業態マスタの拡張: short_name 列を追加し、業態名（display_name）と
-- 略称（short_name）を保守できるようにする。SoTは依然として sales_weekly
-- だが、コード単独では UI 表示が読みにくいため、運用側で代表名を付与する。
-- 取込時の自動 UPSERT（INSERT ... ON CONFLICT DO NOTHING）は code 行のみ
-- 挿入し、display_name / short_name は上書きしないため運用設定が温存される。
-- ------------------------------------------------------------
ALTER TABLE business_type
    ADD COLUMN IF NOT EXISTS short_name text;
COMMENT ON COLUMN business_type.short_name IS '業態の英数略称（例: sm=しまむら, av=アベイル）';

-- 業態マスタの代表データ（01-06）。display_name / short_name は運用者が編集する
-- 設定値であり、毎回の上書きを避けるため DO NOTHING で初回のみ投入する。
-- 既存行が運用者によりカスタマイズされている場合は温存される（CLAUDE.md 原則2）。
INSERT INTO business_type (code, display_name, short_name) VALUES
    ('01', 'しまむら',   'sm'),
    ('02', 'アベイル',   'av'),
    ('03', '思夢樂',     'sr'),
    ('04', 'バースデイ', 'br'),
    ('05', 'シャンブル', 'cm'),
    ('06', 'ディバロ',   'di')
ON CONFLICT (code) DO NOTHING;

-- ------------------------------------------------------------
-- 商品マスタ（運用側で手動投入される参照データ）
-- ------------------------------------------------------------
--  m_product   : 商品の親（業態 × 商品記号 × 品番で1行）
--  m_product_sku: SKU（カラー × サイズ × 単品コード）。画像は SKU + image_index で多枚保持。
--
--  sales_weekly との結合キー対応:
--    sales_weekly.gyotai_code   = m_product.business_category_cd
--    sales_weekly.shohin_kigou  = m_product.product_sign
--    sales_weekly.hinban_code   = m_product.product_type_crd
--    sales_weekly.tanpin_code   = m_product_sku.unit_cd
--
--  SoTは投入元の運用ファイル（手動投入）。アプリは表示・分析のみ行う。
-- ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS m_product (
    product_id              uuid          NOT NULL,
    business_category_cd    varchar(2)    NOT NULL,
    business_category_sign  varchar(50)   NOT NULL,
    division_cd             integer       NOT NULL,
    division_name           varchar(50)   NOT NULL,
    product_name            varchar(255)  NOT NULL,
    brand                   varchar(100),
    product_sign            varchar(50)   NOT NULL,
    manager                 varchar(100),
    product_type_crd        varchar(50)   NOT NULL,
    created_at              timestamptz   NOT NULL DEFAULT now(),
    updated_at              timestamptz   NOT NULL DEFAULT now(),

    CONSTRAINT pk_m_product PRIMARY KEY (product_id)
);

COMMENT ON TABLE  m_product IS '商品マスタ（親）。業態×商品記号×品番で一意';
COMMENT ON COLUMN m_product.business_category_cd  IS '業態コード（sales_weekly.gyotai_code と対応）';
COMMENT ON COLUMN m_product.business_category_sign IS '業態の表示用記号';
COMMENT ON COLUMN m_product.division_cd           IS '部門コード（数値。例 11, 12, 51, 56）';
COMMENT ON COLUMN m_product.division_name         IS '部門表示名';
COMMENT ON COLUMN m_product.product_name          IS '商品名';
COMMENT ON COLUMN m_product.product_sign          IS '商品記号（sales_weekly.shohin_kigou と対応）';
COMMENT ON COLUMN m_product.product_type_crd      IS '品番コード（sales_weekly.hinban_code と対応）';
COMMENT ON COLUMN m_product.brand                 IS 'ブランド名（任意）';
COMMENT ON COLUMN m_product.manager               IS '担当者名（任意）';

-- 業務上の自然キー。手動投入時の重複防止と sales_weekly との結合インデックス。
-- 既存環境で運用者が UNIQUE 制約のないテーブルへ既にデータ投入済みの場合、後から
-- UNIQUE INDEX を作成すると重複違反 (23505) で失敗するため、エラーを捕捉してスキップする。
-- 新規環境では通常通り UNIQUE INDEX が作成される。スキップされた場合は運用者が
-- データ整合（重複行の整理）後に手動で `CREATE UNIQUE INDEX` を実行することを想定する。
DO $$
BEGIN
    CREATE UNIQUE INDEX IF NOT EXISTS ux_m_product_business_key
        ON m_product (business_category_cd, product_sign, product_type_crd);
EXCEPTION WHEN unique_violation THEN
    RAISE NOTICE 'ux_m_product_business_key の作成をスキップしました（既存データに重複あり）。データ整合後に手動で UNIQUE INDEX を作成してください。';
END
$$;

CREATE INDEX IF NOT EXISTS ix_m_product_division_cd ON m_product (division_cd);
CREATE INDEX IF NOT EXISTS ix_m_product_brand       ON m_product (brand);
CREATE INDEX IF NOT EXISTS ix_m_product_manager     ON m_product (manager);

CREATE TABLE IF NOT EXISTS m_product_sku (
    sku_item_id     uuid          NOT NULL,
    product_id      uuid          NOT NULL,
    unit_cd         varchar(50)   NOT NULL,
    color_name      varchar(50)   NOT NULL,
    size_name       varchar(50)   NOT NULL,
    sales_price     integer       NOT NULL DEFAULT 0,
    cost_price      integer       NOT NULL DEFAULT 0,
    image_id        uuid          NOT NULL,
    image_index     integer       NOT NULL,
    image_file_name varchar(255),
    image_url       text          NOT NULL,
    created_at      timestamptz   NOT NULL DEFAULT now(),
    updated_at      timestamptz   NOT NULL DEFAULT now(),

    CONSTRAINT pk_m_product_sku PRIMARY KEY (sku_item_id),
    CONSTRAINT fk_m_product_sku_parent FOREIGN KEY (product_id)
        REFERENCES m_product (product_id) ON DELETE CASCADE
);

COMMENT ON TABLE  m_product_sku IS '商品SKU。1行=SKU×画像（単品×色×サイズ×画像index）';
COMMENT ON COLUMN m_product_sku.unit_cd     IS '単品コード（sales_weekly.tanpin_code と対応）';
COMMENT ON COLUMN m_product_sku.image_index IS '同一SKU内での画像表示順（0=サムネ既定）';
COMMENT ON COLUMN m_product_sku.image_url   IS '画像配信URL（外部CDN想定）';

-- SKU 単位（単品×色×サイズ）のクエリと、sales_weekly との結合性能を確保する。
CREATE INDEX IF NOT EXISTS ix_m_product_sku_product
    ON m_product_sku (product_id);
CREATE INDEX IF NOT EXISTS ix_m_product_sku_product_unit
    ON m_product_sku (product_id, unit_cd);
CREATE INDEX IF NOT EXISTS ix_m_product_sku_unit_cd
    ON m_product_sku (unit_cd);

-- 日次粒度の集計は、週次ファクトを取込日で先に集計してから日次7列を展開する
-- 方式（アプリ側クエリ）で行う。160万行を縦展開する前に集約するため高速。
-- 日付対応ロジック: sales_date = import_date - 8 + day_index
--   day_index 1 → import_date-7（月） / day_index 7 → import_date-1（日）

-- ============================================================
--  分析 mart（スタースキーマ） — docs/star-schema-design.md
-- ------------------------------------------------------------
--  既存 sales_weekly（取込ソース層）を温存しつつ、分析用に
--  ディメンショナルモデルを別スキーマ mart に構築する。
--  本スキーマは派生（キャッシュ）であり、SoT は sales_weekly。
--  mart.rebuild() で sales_weekly + 商品マスタから全再構築する。
--
--  範囲:
--   * 次元: dim_date / dim_retailer / dim_product / dim_sku（全て SCD1）／ dim_climate（気温日次・CSV由来）
--   * ファクト: fact_sales_weekly（週次フロー）／ fact_inventory_snapshot（在庫スナップショット）
--     （グレインはいずれも 週×小売×SKU）
--   * 日次派生ファクト・テナント別スキーマ分離・集約マテビューは後続。
-- ============================================================
CREATE SCHEMA IF NOT EXISTS mart;

-- 日付次元（週=取込日の月曜）
CREATE TABLE IF NOT EXISTS mart.dim_date (
    date_key    integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    week_monday date    NOT NULL UNIQUE,
    iso_year    integer NOT NULL,
    iso_week    integer NOT NULL,
    year        integer NOT NULL,
    quarter     integer NOT NULL,
    month       integer NOT NULL
);
COMMENT ON TABLE mart.dim_date IS '日付次元（週=月曜）。sales_weekly.import_date から導出';

-- 小売次元（企業集約。channel=業態）
CREATE TABLE IF NOT EXISTS mart.dim_retailer (
    retailer_key  integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    retailer_code text NOT NULL,
    retailer_name text,
    channel_code  text NOT NULL,
    channel_name  text,
    UNIQUE (retailer_code, channel_code)
);
COMMENT ON TABLE mart.dim_retailer IS '小売次元（企業集約）。retailer=customer_code / channel=業態(gyotai_code)';

-- 商品次元（SCD1。自然キー=業態×記号×品番）
CREATE TABLE IF NOT EXISTS mart.dim_product (
    product_key     integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    channel_code    text NOT NULL,
    product_sign    text NOT NULL,
    product_code    text NOT NULL,
    product_name    text,
    department_code text,
    department_name text,
    brand           text,
    manager         text,
    category        text,
    season          text,
    attributes      jsonb NOT NULL DEFAULT '{}'::jsonb,
    UNIQUE (channel_code, product_sign, product_code)
);
COMMENT ON TABLE mart.dim_product IS '商品次元（SCD1）。業種固有属性（季節・棚割）は attributes(jsonb)';

-- 業種固有属性のうち頻用する季節は生成列＋索引で集計性能を担保（設計 §5.2）
ALTER TABLE mart.dim_product
    ADD COLUMN IF NOT EXISTS season_attr text
    GENERATED ALWAYS AS (attributes->>'season') STORED;
CREATE INDEX IF NOT EXISTS ix_dim_product_department ON mart.dim_product (department_code);
CREATE INDEX IF NOT EXISTS ix_dim_product_season     ON mart.dim_product (season);

-- SKU次元（SCD1。汎用バリアント2軸＋定価）
CREATE TABLE IF NOT EXISTS mart.dim_sku (
    sku_key             integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    product_key         integer NOT NULL REFERENCES mart.dim_product(product_key),
    unit_code           text NOT NULL,
    variant_axis1_label text,
    variant_axis1_value text,
    variant_axis2_label text,
    variant_axis2_value text,
    list_price          integer,
    image_url           text,
    UNIQUE (product_key, unit_code)
);
COMMENT ON TABLE mart.dim_sku IS 'SKU次元（SCD1）。色/サイズは汎用バリアント2軸。list_price=定価（現在値）';

-- 売上ファクト（週次フロー。グレイン=週×SKU×小売。数量/金額/粗利は事前計算）
CREATE TABLE IF NOT EXISTS mart.fact_sales_weekly (
    date_key     integer NOT NULL REFERENCES mart.dim_date(date_key),
    retailer_key integer NOT NULL REFERENCES mart.dim_retailer(retailer_key),
    product_key  integer NOT NULL REFERENCES mart.dim_product(product_key),
    sku_key      integer NOT NULL REFERENCES mart.dim_sku(sku_key),
    quantity     bigint  NOT NULL,
    amount       bigint  NOT NULL,
    gross_profit bigint  NOT NULL,
    sale_price   integer NOT NULL,
    cost_price   integer NOT NULL,
    PRIMARY KEY (date_key, retailer_key, product_key, sku_key)
);
COMMENT ON TABLE mart.fact_sales_weekly IS '売上ファクト（週次フロー）。amount=数量×売価, gross_profit=数量×(売価−原価) を事前計算';
CREATE INDEX IF NOT EXISTS ix_fact_sw_date     ON mart.fact_sales_weekly (date_key);
CREATE INDEX IF NOT EXISTS ix_fact_sw_product  ON mart.fact_sales_weekly (product_key);
CREATE INDEX IF NOT EXISTS ix_fact_sw_retailer ON mart.fact_sales_weekly (retailer_key);

-- mart のメタ情報（最終再構築時刻）。1行のみ。
CREATE TABLE IF NOT EXISTS mart.build_info (
    id           integer PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    rebuilt_at   timestamptz,
    source_rows  bigint NOT NULL DEFAULT 0,
    fact_rows    bigint NOT NULL DEFAULT 0
);
INSERT INTO mart.build_info (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

-- 非同期再構築の進捗管理列（冪等に追加）。
-- status はアプリ側で idle → running → completed / failed と遷移させる。
ALTER TABLE mart.build_info
    ADD COLUMN IF NOT EXISTS status     text NOT NULL DEFAULT 'idle',
    ADD COLUMN IF NOT EXISTS error      text,
    ADD COLUMN IF NOT EXISTS started_at timestamptz;
DO $$
BEGIN
    ALTER TABLE mart.build_info
        ADD CONSTRAINT mart_build_info_status_chk
        CHECK (status IN ('idle', 'running', 'completed', 'failed'));
EXCEPTION WHEN duplicate_object THEN NULL;
END
$$;

-- 在庫スナップショットファクト（ピリオディック・スナップショット。グレイン=週×小売×SKU）
--  設計 §3.3。在庫・累計は時点値（時間方向に非加算／SKU・小売方向に加算可）、在日は平均で集計。
--  SKU は単一商品に属する（dim_sku.product_key）ため product_key も保持し、部門等の絞り込みを
--  dim_product 経由で fact_sales_weekly と対称に行えるようにする（グレインは週×小売×SKU のまま）。
--  これにより約20箇所に散在する「期間内最新 import_date で在庫取得」ロジックが本テーブル参照に一元化される。
CREATE TABLE IF NOT EXISTS mart.fact_inventory_snapshot (
    date_key     integer NOT NULL REFERENCES mart.dim_date(date_key),
    retailer_key integer NOT NULL REFERENCES mart.dim_retailer(retailer_key),
    product_key  integer NOT NULL REFERENCES mart.dim_product(product_key),
    sku_key      integer NOT NULL REFERENCES mart.dim_sku(sku_key),
    stock        bigint           NOT NULL,
    cum_sales    bigint           NOT NULL,
    cum_delivery bigint           NOT NULL,
    order_qty    numeric(14,1)    NOT NULL,
    advance_qty  bigint           NOT NULL,
    stock_days   double precision NOT NULL,
    PRIMARY KEY (date_key, retailer_key, product_key, sku_key)
);
COMMENT ON TABLE mart.fact_inventory_snapshot IS
    '在庫スナップショット（週次・セミ加算）。stock/cum_sales/cum_delivery は時点値、stock_days は在日（平均集計）';
CREATE INDEX IF NOT EXISTS ix_fact_inv_date     ON mart.fact_inventory_snapshot (date_key);
CREATE INDEX IF NOT EXISTS ix_fact_inv_product  ON mart.fact_inventory_snapshot (product_key);
CREATE INDEX IF NOT EXISTS ix_fact_inv_retailer ON mart.fact_inventory_snapshot (retailer_key);

-- 気温次元（日次・エリア別）。db/climate_daily.csv を DataLoader が投入する（sales 非依存）。
--  area_code: standard=東京 / cold=札幌 / warm=那覇（ClimateModel のエリア種別と一致）。
--  売上週への結合は the_date の範囲結合（FK は張らず疎結合に保つ）。mart.rebuild() では触らない
--  （sales_weekly 由来ではないため、再構築の TRUNCATE/CASCADE 対象に含めない）。
CREATE TABLE IF NOT EXISTS mart.dim_climate (
    area_code text NOT NULL,
    the_date  date NOT NULL,
    temp_avg  double precision NOT NULL,
    temp_max  double precision NOT NULL,
    temp_min  double precision NOT NULL,
    PRIMARY KEY (area_code, the_date)
);
COMMENT ON TABLE mart.dim_climate IS
    '気温次元（日次・エリア別）。出典 db/climate_daily.csv。standard=東京/cold=札幌/warm=那覇';
CREATE INDEX IF NOT EXISTS ix_dim_climate_date ON mart.dim_climate (the_date);

-- ------------------------------------------------------------
--  mart 全再構築（sales_weekly + 商品マスタ → 次元・ファクト）
--  冪等: 何度呼んでも同じ結果。advisory lock で再構築を直列化する。
--  代表値（名称・季節・色/サイズ・定価）は各自然キーの最新取込週を採用。
-- ------------------------------------------------------------
CREATE OR REPLACE FUNCTION mart.rebuild() RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    v_source_rows bigint;
    v_fact_rows   bigint;
BEGIN
    -- 'MART'(0x4D415254) を鍵に再構築を直列化する。
    PERFORM pg_advisory_xact_lock(1296913492);

    -- dim_climate は sales 非依存（CSV 由来）のため TRUNCATE 対象に含めない。
    -- dim_* への CASCADE は両ファクト（売上・在庫）に波及するが、明示して意図を残す。
    TRUNCATE mart.fact_sales_weekly, mart.fact_inventory_snapshot,
             mart.dim_sku, mart.dim_product,
             mart.dim_retailer, mart.dim_date RESTART IDENTITY CASCADE;

    -- 日付次元
    INSERT INTO mart.dim_date (week_monday, iso_year, iso_week, year, quarter, month)
    SELECT DISTINCT
           sw.import_date,
           EXTRACT(ISOYEAR  FROM sw.import_date)::int,
           EXTRACT(WEEK     FROM sw.import_date)::int,
           EXTRACT(YEAR     FROM sw.import_date)::int,
           EXTRACT(QUARTER  FROM sw.import_date)::int,
           EXTRACT(MONTH    FROM sw.import_date)::int
    FROM sales_weekly sw;

    -- 小売次元（業態名は business_type マスタから）
    INSERT INTO mart.dim_retailer (retailer_code, retailer_name, channel_code, channel_name)
    SELECT DISTINCT sw.customer_code, NULL::text, sw.gyotai_code, bt.display_name
    FROM sales_weekly sw
    LEFT JOIN business_type bt ON bt.code = sw.gyotai_code;

    -- 商品次元（自然キーの最新取込週を代表値に）。
    -- 商品マスタ m_product は自然キー（業態×記号×品番）に重複があり得る
    -- （重複により ux_m_product_business_key が未作成の運用環境）。LATERAL + LIMIT 1 で
    -- 1件（updated_at 最新）に絞り、LEFT JOIN の行増幅と dim_product の一意制約違反を防ぐ。
    INSERT INTO mart.dim_product
        (channel_code, product_sign, product_code, product_name,
         department_code, department_name, brand, manager, category, season, attributes)
    SELECT s.gyotai_code, s.shohin_kigou, s.hinban_code,
           COALESCE(mp.product_name, s.hinmei),
           s.department, mp.division_name, mp.brand, mp.manager,
           NULL::text, NULLIF(s.kisetsu, ''),
           jsonb_strip_nulls(jsonb_build_object(
               'season',    NULLIF(s.kisetsu, ''),
               'tanawari1', NULLIF(s.tanawari1, ''),
               'tanawari2', NULLIF(s.tanawari2, '')))
    FROM (
        SELECT DISTINCT ON (gyotai_code, shohin_kigou, hinban_code)
               gyotai_code, shohin_kigou, hinban_code,
               hinmei, department, kisetsu, tanawari1, tanawari2
        FROM sales_weekly
        ORDER BY gyotai_code, shohin_kigou, hinban_code, import_date DESC
    ) s
    LEFT JOIN (
        -- 商品マスタを自然キーで1回だけ重複排除（updated_at 最新を代表）。
        -- LATERAL（行数ぶんの再スキャン）を避け、インデックス未整備でも高速に結合する。
        SELECT DISTINCT ON (business_category_cd, product_sign, product_type_crd)
               business_category_cd, product_sign, product_type_crd,
               product_name, division_name, brand, manager
        FROM m_product
        ORDER BY business_category_cd, product_sign, product_type_crd,
                 updated_at DESC, product_id
    ) mp
        ON mp.business_category_cd = s.gyotai_code
       AND mp.product_sign        = s.shohin_kigou
       AND mp.product_type_crd    = s.hinban_code;

    -- SKU次元（定価・代表画像は商品マスタから）
    INSERT INTO mart.dim_sku
        (product_key, unit_code, variant_axis1_label, variant_axis1_value,
         variant_axis2_label, variant_axis2_value, list_price, image_url)
    SELECT dp.product_key, s.tanpin_code,
           'カラー', NULLIF(s.color, ''),
           'サイズ', NULLIF(s.size, ''),
           sk.sales_price, sk.image_url
    FROM (
        SELECT DISTINCT ON (gyotai_code, shohin_kigou, hinban_code, tanpin_code)
               gyotai_code, shohin_kigou, hinban_code, tanpin_code, color, size
        FROM sales_weekly
        ORDER BY gyotai_code, shohin_kigou, hinban_code, tanpin_code, import_date DESC
    ) s
    JOIN mart.dim_product dp
        ON dp.channel_code = s.gyotai_code
       AND dp.product_sign = s.shohin_kigou
       AND dp.product_code = s.hinban_code
    LEFT JOIN (
        -- SKU（業態×記号×品番×単品）ごとに代表（画像 index 最小）を1回で確定する。
        -- LATERAL の再スキャンを避け、インデックス未整備でも高速に結合する。
        SELECT DISTINCT ON (mp.business_category_cd, mp.product_sign, mp.product_type_crd, msk.unit_cd)
               mp.business_category_cd, mp.product_sign, mp.product_type_crd, msk.unit_cd,
               msk.sales_price, msk.image_url
        FROM m_product mp
        JOIN m_product_sku msk ON msk.product_id = mp.product_id
        ORDER BY mp.business_category_cd, mp.product_sign, mp.product_type_crd, msk.unit_cd,
                 msk.image_index, msk.sku_item_id
    ) sk
        ON sk.business_category_cd = s.gyotai_code
       AND sk.product_sign        = s.shohin_kigou
       AND sk.product_type_crd    = s.hinban_code
       AND sk.unit_cd             = s.tanpin_code;

    -- 売上ファクト＋在庫スナップショットを「1回の集約」から両方構築する。
    -- 両ファクトはグレイン（週×小売×SKU）と次元結合が完全に同一で、測定値だけが異なる。
    -- そこで sales_weekly の走査・次元結合・GROUP BY を1度だけ行い（agg）、データ変更CTEで
    -- 両ファクトへ流し込む（agg は複数参照のため1回だけマテリアライズされる）。
    -- これにより 160万行の二重走査・二重結合を避け、再構築時間を約半減する
    -- （在庫追加で従来比2倍となり command timeout を超えた問題への対処）。
    WITH agg AS (
        SELECT dd.date_key, dr.retailer_key, dp.product_key, ds.sku_key,
               SUM(q.qty)::bigint                         AS quantity,
               SUM(q.qty * sw.baika)::bigint              AS amount,
               SUM(q.qty * (sw.baika - sw.genka))::bigint AS gross_profit,
               MAX(sw.baika)                              AS sale_price,
               MAX(sw.genka)                              AS cost_price,
               SUM(sw.zaikosu)::bigint                    AS stock,
               SUM(sw.ruikei_uriage_count)::bigint        AS cum_sales,
               SUM(sw.ruikei_nohin_count)::bigint         AS cum_delivery,
               SUM(sw.hatchu_count)                       AS order_qty,
               SUM(sw.sakizuke_count)::bigint             AS advance_qty,
               COALESCE(AVG(sw.zainiti), 0)::float8       AS stock_days
        FROM sales_weekly sw
        CROSS JOIN LATERAL (SELECT (sw.toshu_uriage_count1 + sw.toshu_uriage_count2
               + sw.toshu_uriage_count3 + sw.toshu_uriage_count4 + sw.toshu_uriage_count5
               + sw.toshu_uriage_count6 + sw.toshu_uriage_count7) AS qty) q
        JOIN mart.dim_date     dd ON dd.week_monday   = sw.import_date
        JOIN mart.dim_retailer dr ON dr.retailer_code = sw.customer_code
                                 AND dr.channel_code  = sw.gyotai_code
        JOIN mart.dim_product  dp ON dp.channel_code  = sw.gyotai_code
                                 AND dp.product_sign  = sw.shohin_kigou
                                 AND dp.product_code  = sw.hinban_code
        JOIN mart.dim_sku      ds ON ds.product_key   = dp.product_key
                                 AND ds.unit_code     = sw.tanpin_code
        GROUP BY dd.date_key, dr.retailer_key, dp.product_key, ds.sku_key
    ),
    ins_sales AS (
        INSERT INTO mart.fact_sales_weekly
            (date_key, retailer_key, product_key, sku_key,
             quantity, amount, gross_profit, sale_price, cost_price)
        SELECT date_key, retailer_key, product_key, sku_key,
               quantity, amount, gross_profit, sale_price, cost_price
        FROM agg
    )
    INSERT INTO mart.fact_inventory_snapshot
        (date_key, retailer_key, product_key, sku_key,
         stock, cum_sales, cum_delivery, order_qty, advance_qty, stock_days)
    SELECT date_key, retailer_key, product_key, sku_key,
           stock, cum_sales, cum_delivery, order_qty, advance_qty, stock_days
    FROM agg;

    SELECT count(*) INTO v_source_rows FROM sales_weekly;
    SELECT count(*) INTO v_fact_rows   FROM mart.fact_sales_weekly;

    UPDATE mart.build_info
    SET rebuilt_at  = now(),
        source_rows = v_source_rows,
        fact_rows   = v_fact_rows
    WHERE id = 1;
END;
$$;
COMMENT ON FUNCTION mart.rebuild() IS 'sales_weekly + 商品マスタから mart を全再構築する（冪等・直列化）';

COMMIT;
