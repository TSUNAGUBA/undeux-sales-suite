/**
 * 在庫マネジメントのモックデータ生成（純粋関数）。
 *
 * バックエンドが保持しない粒度・区分を、決定的（seed 付きハッシュ）なモックで先行表現する。
 *   - 商品記号ポジショニング: byShohinKigo が API 未提供のため、品番3桁別の健全性から商品記号へ展開する。
 *   - 残在庫（倉庫在庫）: 倉庫在庫・取置・先付は店頭在庫スナップショットに無いため、業態×部門×商品記号でモック生成する。
 *   - 発注区分（売発注／情報発注）: スタースキーマに区分が無いため、SKU 週次の変化をモック生成する。
 *
 * 実データ接続時は、店舗/倉庫ディメンション・発注区分を持つ集計 API へ置き換える。
 */
import type { InventoryDepartmentHealthRow } from '~/types/api'

/** 決定的ハッシュ（文字列→32bit 符号なし整数）。 */
export function hashString(value: string): number {
  let h = 2166136261
  for (let i = 0; i < value.length; i++) {
    h ^= value.charCodeAt(i)
    h = Math.imul(h, 16777619)
  }
  return h >>> 0
}

/** 種から 0..1 の決定的な擬似乱数（mulberry32 の1ステップ）。 */
export function seededUnit(seed: number): number {
  let t = (seed + 0x6d2b79f5) >>> 0
  t = Math.imul(t ^ (t >>> 15), t | 1)
  t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
  return ((t ^ (t >>> 14)) >>> 0) / 4294967296
}

/** 商品記号らしいモックコード（業態×部門×連番から決定的に生成）。残在庫・発注区分と同一体系。 */
function mockShohinKigo(bt: string, dept: string, k: number): string {
  return `LG${dept}${String(100 + (hashString(`${bt}|${dept}|kigo|${k}`) % 900))}`
}

/**
 * 商品記号ポジショニングのモック行（業態×部門ごとに商品記号を単位として集計）。
 *
 * byShohinKigo が API 未提供のため、実データ由来の品番3桁別（byHinban）から按分すると
 * ラベルが品番ベースの疑似コードになり「商品記号」にならない。そこで残在庫タブと同一体系の
 * 商品記号（LG{部門}{nnn}）を単位に、消化率×在庫日数×在庫数を決定的にモック生成する。
 * 業態・部門はページ上部フィルターの選択で絞る（未選択は全選択肢）。実データ接続時は byShohinKigo に置換する。
 */
export function buildShohinKigoPositioningMock(
  businessTypeCodes: readonly string[],
  departmentCodes: readonly string[],
): InventoryDepartmentHealthRow[] {
  const rows: InventoryDepartmentHealthRow[] = []
  for (const bt of businessTypeCodes) {
    for (const dept of departmentCodes) {
      const count = 3 + (hashString(`pos|${bt}|${dept}`) % 4) // 3〜6 商品記号
      for (let k = 0; k < count; k++) {
        const seed = hashString(`pos|${bt}|${dept}|${k}`)
        const stock = 50 + Math.round(seededUnit(seed) * 950)
        const sell = Math.round(seededUnit(seed + 1) * 100) / 100
        const days = Math.round(seededUnit(seed + 2) * 120)
        const cost = Math.round(stock * (300 + seededUnit(seed + 3) * 1500))
        rows.push({
          key: `pos-${bt}-${dept}-${k}`,
          label: mockShohinKigo(bt, dept, k),
          stock,
          stockValueCost: cost,
          sellThroughRate: sell,
          averageStockDays: days,
          // 健全性の内訳（4象限チャートは stock/消化率/在日のみ使用。内訳は近似で保持）。
          healthyStock: days <= 60 && sell >= 0.75 ? stock : 0,
          cautionStock: days <= 60 && sell < 0.75 ? stock : 0,
          stagnantStock: days > 60 && sell < 0.75 ? stock : 0,
          dormantStock: 0,
        })
      }
    }
  }
  return rows
}

const HINMEI_POOL = [
  'デニムパンツ', 'レギンス', 'カットソー', 'ニットカーデ', 'スウェットパーカー',
  'ソックス', 'Tシャツ', 'フレアスカート', 'ワイドパンツ', 'インナーシャツ',
]

// ============================================================
// 残在庫（倉庫在庫）モック
// 倉庫在庫・取置・先付・発注は店頭在庫スナップショットに無いため、業態×部門で SKU を決定的に生成する。
// ============================================================

/**
 * 残在庫（倉庫在庫）の1行（SKU 単位）。数量①〜⑬は売上参照/WMS/算出のソースに対応する。
 * 不変条件: 先付① ≥ 発注② ≥ 納品③ ≥ 売上④。⑥⑨⑩⑪⑫⑬ は算出（負値もあり得る）。
 */
export interface WarehouseRow {
  key: string
  businessTypeCode: string
  departmentCode: string
  shohinKigou: string
  hinbanCode: string
  tanpinCode: string
  hinmei: string
  /** 帳票区分（売発注／情報発注）。 */
  chohyoKubun: OrderClass
  /** ① 先付数（売上参照）。 */
  sakizukeCount: number
  /** ② 発注数（売上参照）。 */
  hatchuCount: number
  /** ③ 納品数（売上参照）。 */
  ruikeiNohinCount: number
  /** ④ 売上数（売上参照）。 */
  ruikeiUriageCount: number
  /** ⑤ 店頭在庫（売上参照：在庫数）。 */
  zaikosu: number
  /** ⑥ 発注済未納品＝②−③。 */
  orderNotDelivered: number
  /** ⑦ 取置在庫数（WMS）。 */
  reservedStock: number
  /** ⑧ 論理在庫数（WMS：在庫数）。 */
  logicalStock: number
  /** ⑨ 出荷可能数(取置)＝⑧−⑦。 */
  shippableReserved: number
  /** ⑩ 出荷可能数(発注済未納品)＝⑧−⑦−⑥。 */
  shippableOrder: number
  /** ⑪ 累計在庫数＝③+⑥+⑧。 */
  cumulativeStock: number
  /** ⑫ 先付増減数＝⑪−①。 */
  sakizukeDelta: number
  /** ⑬ 先付増減率＝⑪÷①（比率。0..）。 */
  sakizukeRate: number
}

/**
 * 残在庫（倉庫在庫）のモックデータを業態×部門ごとに決定的に生成する。
 * 数量は 先付 ≥ 発注 ≥ 納品 ≥ 売上 の整合を保つ。実データ接続時は WMS/EDI/基幹の結合クエリへ置換する。
 */
export function buildWarehouseMock(
  businessTypeCodes: readonly string[],
  departmentCodes: readonly string[],
): WarehouseRow[] {
  const rows: WarehouseRow[] = []
  for (const bt of businessTypeCodes) {
    for (const dept of departmentCodes) {
      const kigoCount = 2 + (hashString(`wh|${bt}|${dept}`) % 2) // 2-3 商品記号
      for (let k = 0; k < kigoCount; k++) {
        const kigo = mockShohinKigo(bt, dept, k)
        const hinban = String(100 + (hashString(`hb|${bt}|${dept}|${k}`) % 900))
        for (let t = 0; t < 2; t++) {
          const seed = hashString(`${bt}|${dept}|${k}|${t}`)
          const sakizuke = 300 + Math.round(seededUnit(seed) * 1200) // ①
          const hatchu = Math.round(sakizuke * (0.55 + seededUnit(seed + 1) * 0.35)) // ② ≤ ①
          const nohin = Math.round(hatchu * (0.7 + seededUnit(seed + 2) * 0.28)) // ③ ≤ ②
          const uriage = Math.round(nohin * (0.4 + seededUnit(seed + 3) * 0.45)) // ④ ≤ ③
          const remain = Math.max(0, nohin - uriage)
          const reserved = Math.round(seededUnit(seed + 6) * 30) // ⑦
          const logical = reserved + Math.round(remain * (0.5 + seededUnit(seed + 5) * 0.6)) // ⑧ ≥ ⑦
          const orderNotDelivered = Math.max(0, hatchu - nohin) // ⑥
          const cumulativeStock = nohin + orderNotDelivered + logical // ⑪
          rows.push({
            key: `${bt}-${dept}-${k}-${t}`,
            businessTypeCode: bt,
            departmentCode: dept,
            shohinKigou: kigo,
            hinbanCode: hinban,
            tanpinCode: String(1000 + t),
            hinmei: HINMEI_POOL[seed % HINMEI_POOL.length]!,
            chohyoKubun: seededUnit(seed + 8) < 0.6 ? 'sales' : 'info',
            sakizukeCount: sakizuke,
            hatchuCount: hatchu,
            ruikeiNohinCount: nohin,
            ruikeiUriageCount: uriage,
            zaikosu: Math.round(remain * (0.4 + seededUnit(seed + 4) * 0.3)), // ⑤
            orderNotDelivered,
            reservedStock: reserved,
            logicalStock: logical,
            shippableReserved: logical - reserved, // ⑨
            shippableOrder: logical - reserved - orderNotDelivered, // ⑩
            cumulativeStock,
            sakizukeDelta: cumulativeStock - sakizuke, // ⑫
            sakizukeRate: sakizuke > 0 ? cumulativeStock / sakizuke : 0, // ⑬
          })
        }
      }
    }
  }
  return rows
}

// ============================================================
// 発注区分（売発注／情報発注）モック
// スタースキーマに発注区分が無いため、SKU の週次区分と変化をモック生成する。
// 実データ接続時は public.sales-weekly を発注区分つきでスタースキーマに取り込み置換する。
// ============================================================

/** 帳票区分（売発注／情報発注／無）。「無」は当該週に発注実績が無い状態。 */
export type OrderClass = 'sales' | 'info' | 'none'

export const ORDER_CLASS_LABEL: Record<OrderClass, string> = {
  sales: '売発注',
  info: '情報発注',
  none: '発注なし',
}

/** 帳票区分の略記（売発注→売／情報発注→情／無→無）。変化列の表示に使う。 */
export const ORDER_CLASS_ABBR: Record<OrderClass, string> = {
  sales: '売',
  info: '情',
  none: '無',
}

/**
 * 前週→当週の帳票区分変化を略記で返す（例: 売→情、売→無）。
 * 変化なし（前後同一）または履歴不足のときは null。
 */
export function orderClassTransitionLabel(
  previous: OrderClass | null,
  current: OrderClass | null,
): string | null {
  if (previous === null || current === null || previous === current) return null
  return `${ORDER_CLASS_ABBR[previous]}→${ORDER_CLASS_ABBR[current]}`
}

/** 発注区分の週次履歴1点。 */
export interface OrderClassHistoryPoint {
  week: string
  orderClass: OrderClass
}

/** 発注区分の変化を表す1行（SKU 単位）。 */
export interface OrderClassRow {
  key: string
  businessTypeCode: string
  departmentCode: string
  shohinKigou: string
  hinbanCode: string
  tanpinCode: string
  hinmei: string
  /** 週次履歴（昇順）。 */
  history: OrderClassHistoryPoint[]
  /** 前週の区分（履歴が1点未満なら null）。 */
  previous: OrderClass | null
  /** 当週の区分（履歴が空なら null）。 */
  current: OrderClass | null
  /** 前週から当週で区分が変わったか。 */
  changed: boolean
  /** 履歴全体での変化回数。 */
  changeCount: number
}

/**
 * 帳票区分（売発注／情報発注／無）の週次履歴をモック生成する。
 * 各 SKU は初期区分（売または情）から、週ごとに小確率で切り替わる（区分は一定の粘着性を持つ）。
 * 一定確率で「無」（発注なし）へ遷移し、売→無・情→無 等の変化も表現する。
 */
export function buildOrderClassMock(
  businessTypeCodes: readonly string[],
  departmentCodes: readonly string[],
  weeks: readonly string[],
): OrderClassRow[] {
  const rows: OrderClassRow[] = []
  for (const bt of businessTypeCodes) {
    for (const dept of departmentCodes) {
      const kigoCount = 2 + (hashString(`oc|${bt}|${dept}`) % 2)
      for (let k = 0; k < kigoCount; k++) {
        const kigo = `LG${dept}${String(100 + (hashString(`oc|${bt}|${dept}|${k}`) % 900))}`
        const hinban = String(100 + (hashString(`ochb|${bt}|${dept}|${k}`) % 900))
        for (let t = 0; t < 2; t++) {
          const skuKey = `${bt}-${dept}-${k}-${t}`
          const seed0 = hashString(`ocsku|${skuKey}`)
          // 初期区分は売または情（発注が始まった状態）。以後、週ごとに小確率で遷移する。
          let cls: OrderClass = seededUnit(seed0) < 0.6 ? 'sales' : 'info'
          const history: OrderClassHistoryPoint[] = []
          let changeCount = 0
          weeks.forEach((week, wi) => {
            if (wi > 0) {
              const r = seededUnit(hashString(`${skuKey}|${wi}`))
              let next: OrderClass = cls
              if (r < 0.1) next = 'sales'
              else if (r < 0.2) next = 'info'
              else if (r < 0.27) next = 'none'
              if (next !== cls) {
                cls = next
                changeCount += 1
              }
            }
            history.push({ week, orderClass: cls })
          })
          const current = history.length > 0 ? history[history.length - 1]!.orderClass : null
          const previous = history.length > 1 ? history[history.length - 2]!.orderClass : null
          rows.push({
            key: skuKey,
            businessTypeCode: bt,
            departmentCode: dept,
            shohinKigou: kigo,
            hinbanCode: hinban,
            tanpinCode: String(1000 + t),
            hinmei: HINMEI_POOL[seed0 % HINMEI_POOL.length]!,
            history,
            previous,
            current,
            changed: previous !== null && current !== null && previous !== current,
            changeCount,
          })
        }
      }
    }
  }
  return rows
}

