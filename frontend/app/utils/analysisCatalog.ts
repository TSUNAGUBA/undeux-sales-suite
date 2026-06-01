/**
 * 分析系の共通カタログ（平均在庫日数バケット・気温エリア種別）。
 *
 * フィルタ UI（FilterControls / 各条件パネル）とクロス集計・新規分析ページで共有する
 * 単一の定義（SoT）。バックエンドのバケットキー（le30 / d31to60 / ge61）・エリアキー
 * （standard / cold / warm）と一致させること。
 */
import type { StockDaysBucket, TemperatureArea } from '~/types/api'

/** 平均在庫日数（在日）バケットの選択肢。記号付きラベルは要件定義に準拠。 */
export const STOCK_DAYS_BUCKETS: ReadonlyArray<{ value: StockDaysBucket; label: string }> = [
  { value: 'le30', label: '◎ 30日以内' },
  { value: 'd31to60', label: '〇 31〜60日' },
  { value: 'ge61', label: '△ 61日以上' },
]

const STOCK_DAYS_BUCKET_LABEL = new Map<string, string>(
  STOCK_DAYS_BUCKETS.map((b) => [b.value, b.label]),
)

/** バケットキーから表示ラベルを返す（未知キーはそのまま返す）。 */
export function stockDaysBucketLabel(key: string): string {
  return STOCK_DAYS_BUCKET_LABEL.get(key) ?? key
}

/** 気温エリア種別の選択肢（括弧内は参照観測地点）。 */
export const TEMPERATURE_AREAS: ReadonlyArray<{ value: TemperatureArea; label: string; city: string }> = [
  { value: 'standard', label: '標準（東京）', city: '東京' },
  { value: 'cold', label: '寒冷（札幌）', city: '札幌' },
  { value: 'warm', label: '温暖（那覇）', city: '那覇' },
]

const TEMPERATURE_AREA_LABEL = new Map<string, string>(
  TEMPERATURE_AREAS.map((a) => [a.value, a.label]),
)

/** エリアキーから表示ラベルを返す（未知キーはそのまま返す）。 */
export function temperatureAreaLabel(key: string): string {
  return TEMPERATURE_AREA_LABEL.get(key) ?? key
}
