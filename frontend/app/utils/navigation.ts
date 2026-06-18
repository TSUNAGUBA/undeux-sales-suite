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
  Boxes,
  CalendarPlus,
  CalendarRange,
  Database,
  Gauge,
  LayoutDashboard,
  LayoutGrid,
  ListOrdered,
  Package,
  ScatterChart,
  Shirt,
  ShoppingCart,
  SlidersHorizontal,
  Tag,
  Telescope,
  TrendingUp,
  Upload,
} from 'lucide-vue-next'

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
}

/**
 * 目的別カテゴリの定義。並び順は「現状把握 → 深掘り → 探索・予測 → データ整備」という
 * 分析業務の思考順序に合わせている。
 */
export const NAV_CATEGORIES: NavCategory[] = [
  {
    id: 'monitoring',
    label: '販売モニタリング',
    icon: Gauge,
    description: '全社の売上・粗利の現状をまとめて把握する',
    pages: [
      { path: '/mart', label: '全社サマリー', icon: LayoutDashboard },
      { path: '/mart/sales', label: '売上分析', icon: TrendingUp },
    ],
  },
  {
    id: 'weekly',
    label: '週間モニタリング',
    icon: CalendarRange,
    description: '直近週の実績と前週比・週次推移を確認する',
    pages: [
      { path: '/mart/weekly', label: '週間モニタリング', icon: CalendarRange },
    ],
  },
  {
    id: 'inventory',
    label: '在庫マネジメント',
    icon: Boxes,
    description: '滞留・不動在庫を抽出し、発注抑制・値下げの判断につなげる',
    pages: [
      { path: '/mart/inventory', label: '在庫マネジメント', icon: Boxes },
    ],
  },
  {
    id: 'otb',
    label: 'OTB管理',
    icon: ShoppingCart,
    description: 'バイヤー視点で仕入枠（OTB）・発注残・在庫健全性を管理し、次の発注を判断する',
    pages: [
      { path: '/mart/otb', label: '全社OTBサマリー', icon: ShoppingCart },
    ],
  },
  {
    id: 'brand',
    label: 'ブランド/シリーズ分析',
    icon: Tag,
    description: 'ブランド・シリーズ（商品記号）軸で売れ行きを比較する',
    pages: [
      { path: '/mart/brand', label: 'ブランド/シリーズ分析', icon: Tag },
    ],
  },
  {
    id: 'product',
    label: 'アイテム分析',
    icon: Package,
    description: '商品を起点に売れ行き・導入状況を深掘りする',
    pages: [
      { path: '/mart/products', label: '商品別分析', icon: Package, matchSubroutes: true },
      { path: '/mart/introductions', label: '商品導入管理', icon: CalendarPlus },
    ],
  },
  {
    id: 'exploration',
    label: '探索・予測分析',
    icon: Telescope,
    description: '集計軸を切り替えた多角集計と統計分析・売上予測を行う',
    pages: [
      { path: '/mart/crosstab', label: 'クロス集計', icon: LayoutGrid },
      { path: '/mart/ranking', label: 'ランキング分析', icon: ListOrdered },
      { path: '/mart/scatter', label: '散布図・回帰分析', icon: ScatterChart },
      { path: '/mart/simulation', label: '重回帰シミュレーター', icon: SlidersHorizontal },
    ],
  },
  {
    id: 'data',
    label: 'データ管理',
    icon: Database,
    description: '商品マスタの整備と週次実績データの取込を行う',
    pages: [
      { path: '/product-master', label: '商品マスタ', icon: Shirt, matchSubroutes: true },
      { path: '/imports', label: '週次取込', icon: Upload },
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
