/**
 * メニュー・ページ構成の SoT（単一定義）。
 *
 * rakuten-ec-suite のポータル型ナビゲーション（目的ごとにドリルダウンし、配下ページを
 * タブで遷移する構成）に倣い、UndeuxSales では「ホーム（目的別カテゴリカード）→
 * カテゴリ配下ページ（タブバー）」の2階層で構成する。
 * ホームのカード・タブバー・パンくず・戻るボタンは全て本ファイルの定義から導出する。
 *
 * 既存ページの URL は変更しない（ブックマーク・外部リンクの下位互換を維持する）。
 * カテゴリはナビゲーション表示上のグルーピングであり、ルーティングには影響しない。
 */
import type { Component } from 'vue'
import {
  BookOpenText,
  Boxes,
  Building2,
  CalendarDays,
  CalendarPlus,
  CalendarRange,
  Database,
  Gauge,
  Handshake,
  Images,
  LayoutDashboard,
  LayoutGrid,
  ListChecks,
  ListOrdered,
  MessagesSquare,
  Package,
  PieChart,
  ScatterChart,
  Shirt,
  SlidersHorizontal,
  Sparkles,
  Store,
  Table2,
  Tag,
  Tags,
  Target,
  Telescope,
  TrendingUp,
  Upload,
  Wallet,
} from 'lucide-vue-next'
import type { AccountType } from '~/types/api'

/** ナビゲーション上のページ。カテゴリ配下のタブ1つに対応する。 */
export interface NavPage {
  /** ページのルートパス（既存 URL をそのまま使う）。 */
  path: string
  /** タブ・ホームカード内リストに表示するページ名。 */
  label: string
  icon: Component
  /**
   * 詳細サブルート（`path + '/'` 配下）でもこのページをアクティブ扱いにするか。
   * 商品別分析・商品マスタのような一覧→詳細構造を持つページで true にする。
   * `/mart`（全社サマリー）は他ページの親パスでもあるため厳密一致のまま運用する。
   */
  matchSubroutes?: boolean
  /**
   * 表示を許可するアカウント種別。未指定はカテゴリの roles を継承（＝全許可）。
   * 同一カテゴリ内でページごとに出し分けたい場合に指定する（例: 商品マスタ・週次取込は
   * サプライヤーのみ、予算管理は両ロール）。
   */
  roles?: AccountType[]
}

/** 目的別カテゴリ。ホームのカード1枚・タブバー1本の単位。 */
export interface NavCategory {
  id: string
  /** 目的を表すカテゴリ名。 */
  label: string
  icon: Component
  /** ホームのカードに表示する目的の説明。 */
  description: string
  /** カテゴリ配下のページ。先頭がカテゴリの既定ページ（カード・パンくず押下時の遷移先）。 */
  pages: NavPage[]
  /**
   * 表示を許可するアカウント種別。未指定は全ロールに表示する。
   * 例: OTB管理はバイヤーのみ、メーカー向けモニタリング群はサプライヤーのみ。
   */
  roles?: AccountType[]
}

/**
 * 目的別カテゴリの定義。並び順は「全体モニタリング → 在庫マネジメント → アイテム分析 →
 * 副資材チェック → データ管理 → 探索・予測分析」という現状把握→深掘り→整備→探索の思考順序に
 * 合わせている。OTB管理はバイヤー専用のため先頭に置く（サプライヤーには表示されない）。
 *
 * 副資材チェックは出荷前検品の独立した業務のため、データ管理配下のタブではなく
 * トップレベルの独立カテゴリ（ホームのカード）として表示する（サプライヤー専用）。
 *
 * 旧「週間モニタリング」「ブランド/シリーズ分析」は独立カテゴリを廃し、アイテム分析配下の
 * タブへ統合した（URL は不変のため下位互換を維持）。
 */
export const NAV_CATEGORIES: NavCategory[] = [
  {
    id: 'otb',
    label: 'OTB管理',
    icon: Target,
    description: '全社・部門横断でOTB（仕入枠）と在庫健全性を俯瞰し、未来の仕入を意思決定する',
    roles: ['buyer'],
    pages: [
      { path: '/mart/otb', label: '全社OTBサマリー', icon: Target },
    ],
  },
  {
    id: 'monitoring',
    label: '全体モニタリング',
    icon: Gauge,
    description: '全社の売上・粗利の現状をまとめて把握する',
    roles: ['supplier'],
    pages: [
      { path: '/mart', label: '全社サマリー', icon: LayoutDashboard },
      { path: '/mart/sales', label: '売上分析', icon: TrendingUp },
    ],
  },
  {
    id: 'inventory',
    label: '在庫マネジメント',
    icon: Boxes,
    description: '滞留・不動在庫を抽出し、発注抑制・値下げの判断につなげる',
    roles: ['supplier'],
    pages: [
      { path: '/mart/inventory', label: '在庫マネジメント', icon: Boxes },
    ],
  },
  {
    id: 'product',
    label: 'アイテム分析',
    icon: Package,
    description: '商品を起点に、写真帳・詳細分析・週次/ブランド動向・導入状況まで深掘りする',
    // メーカー（supplier）向けに構築したアイテム分析を、小売（buyer）へ配下タブごと丸ごと移植する。
    // ページは mart 集計（両ロール共通のデータ源）で描画するため、ロール固有の分岐は不要。
    roles: ['supplier', 'buyer'],
    pages: [
      { path: '/mart/products', label: '商品別分析', icon: Package, matchSubroutes: true },
      { path: '/mart/item-detail', label: '商品詳細分析', icon: Table2 },
      { path: '/mart/photobook', label: '写真帳', icon: Images },
      { path: '/mart/weekly', label: '週間モニタリング', icon: CalendarRange },
      { path: '/mart/weekly-daily', label: '日次分析：特定品番', icon: CalendarDays },
      { path: '/mart/brand', label: 'ブランド/シリーズ分析', icon: Tag },
      { path: '/mart/introductions', label: '商品導入管理', icon: CalendarPlus },
    ],
  },
  {
    id: 'subsidiary-check',
    label: '副資材チェック',
    icon: Tags,
    description: '指示書（工程表FAX等）とタグ画像をAIで突合し、レイアウト・順番・内容をチェックする',
    // サプライヤー（メーカー）の出荷前副資材検品（タグ・下げ札・品質表示のAIチェック）。
    // 独立した業務のため、データ管理配下ではなくトップレベルの独立メニューとして表示する。
    roles: ['supplier'],
    pages: [
      { path: '/subsidiary-check', label: '副資材チェック', icon: Tags, matchSubroutes: true },
    ],
  },
  {
    id: 'store',
    label: '店舗分析',
    icon: Store,
    description: '発注タイプ・店舗属性・商品属性の条件を組み合わせ、売れている商品ランキングを見る',
    // 小売（buyer）専用。現状は店舗粒度の実データが不足するためモックで表現する。
    roles: ['buyer'],
    pages: [
      { path: '/mart/store-analysis', label: '店舗別売れ筋ランキング', icon: Store },
    ],
  },
  {
    id: 'ai',
    label: 'AIチャット',
    icon: Sparkles,
    description: '社内ナレッジ（RAG）を参照するAIチャットで、業務の質問と商談準備を支援する',
    // 業務チャット・商談チャットは両ロール共通のナレッジ活用機能のため roles は指定しない。
    pages: [
      { path: '/ai/business-chat', label: '業務チャット', icon: MessagesSquare },
      { path: '/ai/negotiation-chat', label: '商談チャット', icon: Handshake },
      { path: '/ai/rag-settings', label: 'RAG設定', icon: BookOpenText },
    ],
  },
  {
    id: 'data',
    label: 'データ管理',
    icon: Database,
    description: '予算の登録と、商品マスタ・週次実績データの整備を行う',
    pages: [
      // 予算管理は両ロール（バイヤー=仕入予算/売上予算、サプライヤー=売上予算）。
      { path: '/mart/budget', label: '予算管理', icon: Wallet },
      // 商品マスタ・週次取込は自社データ整備のためサプライヤー（メーカー）向け。
      { path: '/product-master', label: '商品マスタ', icon: Shirt, matchSubroutes: true, roles: ['supplier'] },
      { path: '/imports', label: '週次取込', icon: Upload, roles: ['supplier'] },
      // 業態・部門マスタは商談チャットの選択肢や相談受付デスクの SoT 表示のため両ロールに公開する。
      { path: '/org-master', label: '業態・部門マスタ', icon: Building2 },
    ],
  },
  {
    id: 'exploration',
    label: '探索・予測分析',
    icon: Telescope,
    description: '集計軸を切り替えた多角集計と統計分析・売上予測を行う',
    pages: [
      { path: '/mart/crosstab', label: 'クロス集計', icon: LayoutGrid },
      { path: '/mart/department', label: '部門分析', icon: PieChart },
      { path: '/mart/ranking', label: 'ランキング分析', icon: ListOrdered },
      { path: '/mart/scatter', label: '散布図・回帰分析', icon: ScatterChart },
      { path: '/mart/simulation', label: '重回帰シミュレーター', icon: SlidersHorizontal },
      { path: '/mart/decision', label: '意思決定オントロジー・ビュー', icon: ListChecks },
    ],
  },
]

/**
 * ページのアクティブ判定。matchSubroutes を持つページは詳細サブルート
 * （`path + '/'` 配下）でもアクティブ、それ以外は完全一致のみ。
 * `/mart` が `/mart/sales` 等の配下でアクティブにならないよう、前方一致は使わない。
 */
export function isNavPageActive(page: NavPage, currentPath: string): boolean {
  if (page.matchSubroutes) {
    return currentPath === page.path || currentPath.startsWith(`${page.path}/`)
  }
  return currentPath === page.path
}

/** 現在パスが属するカテゴリを返す。どのカテゴリにも属さない場合は undefined。 */
export function findCategoryByPath(currentPath: string): NavCategory | undefined {
  return NAV_CATEGORIES.find((category) =>
    category.pages.some((page) => isNavPageActive(page, currentPath)),
  )
}

/** カテゴリの既定ページ（ホームカード・パンくず押下時の遷移先）。 */
export function categoryDefaultPath(category: NavCategory): string {
  return category.pages[0]?.path ?? '/'
}

/**
 * 戻るボタンの遷移先（1つ上の階層）。
 * - 詳細サブルート（例: `/mart/products/123`）→ その一覧ページ（例: `/mart/products`）
 * - カテゴリ配下のページ → ホーム（`/`）
 * - ホーム・どのカテゴリにも属さないパス → null（戻るボタン非表示）
 */
export function findParentPath(currentPath: string): string | null {
  if (currentPath === '/') return null
  const category = findCategoryByPath(currentPath)
  if (!category) return null
  const page = category.pages.find((p) => isNavPageActive(p, currentPath))
  if (page && page.matchSubroutes && currentPath !== page.path) {
    return page.path
  }
  return '/'
}

// ============================================================
// アカウント種別（ロール）によるメニュー出し分け
// ホーム・タブバー・パンくず・ガードは本群を SoT とする。
// ============================================================

/** カテゴリが指定ロールに表示可能か（roles 未指定は全許可）。 */
export function isCategoryVisible(category: NavCategory, role: AccountType): boolean {
  return category.roles === undefined || category.roles.includes(role)
}

/** ページが指定ロールに表示可能か（page.roles 未指定はカテゴリの roles を継承）。 */
export function isNavPageVisible(page: NavPage, category: NavCategory, role: AccountType): boolean {
  const roles = page.roles ?? category.roles
  return roles === undefined || roles.includes(role)
}

/**
 * 指定ロールに表示するカテゴリ一覧（カテゴリ・ページ双方を roles で絞り込む）。
 * 表示ページが1つも残らないカテゴリは除外する。ホームのカード導出に使う。
 */
export function categoriesForRole(role: AccountType): NavCategory[] {
  return NAV_CATEGORIES
    .filter((category) => isCategoryVisible(category, role))
    .map((category) => ({
      ...category,
      pages: category.pages.filter((page) => isNavPageVisible(page, category, role)),
    }))
    .filter((category) => category.pages.length > 0)
}

/**
 * 現在パスが属するカテゴリを、指定ロールで表示可能なページだけに絞って返す。
 * タブバー・パンくずのロール出し分けに使う。カテゴリ自体が非表示ロールの場合は undefined。
 */
export function visibleCategoryForPath(
  currentPath: string,
  role: AccountType,
): NavCategory | undefined {
  const category = findCategoryByPath(currentPath)
  if (!category || !isCategoryVisible(category, role)) return undefined
  return {
    ...category,
    pages: category.pages.filter((page) => isNavPageVisible(page, category, role)),
  }
}

/**
 * 指定パスが指定ロールに許可されているか（ミドルウェアのガードに使う）。
 * どのカテゴリにも属さないパス（`/`・`/login`・カテゴリ外の詳細サブルート等）は許可する
 * （過剰ブロックを避ける）。カテゴリに属するがロール非対象のページ・カテゴリは不許可。
 */
export function isPathAllowedForRole(currentPath: string, role: AccountType): boolean {
  const category = findCategoryByPath(currentPath)
  if (!category) return true
  if (!isCategoryVisible(category, role)) return false
  const page = category.pages.find((p) => isNavPageActive(p, currentPath))
  return page ? isNavPageVisible(page, category, role) : true
}
