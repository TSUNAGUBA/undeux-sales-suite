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
CREATE UNIQUE INDEX IF NOT EXISTS ux_m_product_business_key
    ON m_product (business_category_cd, product_sign, product_type_crd);

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

COMMIT;
