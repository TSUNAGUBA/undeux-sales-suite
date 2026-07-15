/**
 * 意思決定オントロジー・ビュー（/mart/decision）の概念モックデータ（決定論的・SoT）。
 *
 * 参照実装は `refference/意思決定オントロジー・ビュー（概念モック）/index.html`（動く概念モック）と
 * 同 `ENGINEER_HANDOFF.md`。狙いは「オントロジー3次元（①意味／②関連／③アクション＆制約）を
 * 1オブジェクト（滞留SKU）で見せ、③制約の ✗ で打ち手が潰れ、○/△ の打ち手だけが選択肢カードへ
 * 昇格する」流れを体感させること。
 *
 * 本フェーズはダミーデータ固定（実データ接続・実行処理・制約ルールの作り込みは次フェーズ対象外）。
 * したがって otbMock / storeAnalysisMock と同様に、乱数・時刻に依存しない固定の定数として定義する。
 * 数値は参照モックの表示値をそのまま採用しており、粗利率のように売価・原価から機械的に導けない
 * 値も参照モックの提示に合わせて固定値で持つ（現場知識に基づく設計事項のため勝手に再計算しない）。
 */
import type { AccountType } from '~/types/api'

/**
 * 意思決定ペルソナ。参照モックの DATA キー（retail=小売／maker=メーカー）に合わせる。
 * アプリ共通の {@link AccountType}（buyer=小売／supplier=メーカー）から {@link personaForAccountType} で写像する。
 */
export type DecisionPersona = 'retail' | 'maker'

/** 打ち手の判定。ok=○（実行可）／warn=△（条件付き）／ng=✗（制約で潰れる）。 */
export type ActionStatus = 'ok' | 'warn' | 'ng'

/** 選択肢カードのスロット。③の ○/△ 打ち手から昇格した先を表す。 */
export type OptionSlot = 'A' | 'B' | 'C'

/** 強調区間を含む表示テキストの断片（v-html を使わず安全に描画するための構造）。 */
export interface RichSegment {
  text: string
  /** true のとき太字＋濃色で強調する。 */
  strong?: boolean
}

/** ② 関連（オントロジー・リンク）。クリックでミニ情報を開く。 */
export interface OntologyLink {
  /** 関連の種別（ブランド／同シリーズ 等）。 */
  label: string
  /** 繋がる先の表示名。 */
  to: string
  /** クリックで開くミニ情報（③制約の出所などの補足）。 */
  info: string
}

/** ③ アクション＆制約の1打ち手。 */
export interface OntologyAction {
  /** 打ち手の名称。 */
  name: string
  /** 判定（○/△/✗）。 */
  status: ActionStatus
  /** 昇格先の選択肢スロット。ng（潰れた打ち手）は null で選択肢に繋がらない。 */
  slot: OptionSlot | null
  /** 判定理由（制約の内容）。 */
  why: string
}

/** 選択肢カード（③の ○/△ 打ち手から昇格した実行可能な選択肢）。 */
export interface DecisionOption {
  slot: OptionSlot
  /** AI 推奨（★・枠強調）か。 */
  recommended: boolean
  title: string
  /** 予測影響（ダミー数値）。各要素が1行。 */
  prediction: string[]
  /** 根拠（どの次元から来たか）。強調区間を含む。 */
  basis: RichSegment[]
}

/** ペルソナ別の意思決定セット（目的関数・論点・打ち手・選択肢・AI推奨理由）。 */
export interface PersonaDecision {
  /** 目的関数ラベル（小売＝消化率×欠品回避／メーカー＝粗利×ブランド維持）。 */
  objective: string
  /** 論点（何を意思決定するか）。 */
  issue: string
  actions: OntologyAction[]
  options: DecisionOption[]
  /** AI 推奨の理由（強調区間を含む）。 */
  whyRecommend: RichSegment[]
}

/** ① 意味（セマンティク）の属性1行。 */
export interface SemanticAttr {
  key: string
  value: string
}

/**
 * 主役オブジェクト（滞留SKU「HE8800」）の素の値。
 * サマリー帯・① 意味の表示はこの単一定義から導出する（表示フォーマットは format ユーティリティで統一）。
 * grossMarginRate は参照モックの提示値（41.4%）を採用する固定値で、売価・原価からの機械計算値ではない。
 */
export const DECISION_OBJECT = {
  sku: 'HE8800',
  /** 品番3桁。 */
  hinban3: '558',
  /** 季節区分。 */
  season: '通季',
  /** カテゴリ（服種）。 */
  category: '靴',
  /** 原価（円）。 */
  cost: 765,
  /** 売価（円）。 */
  price: 1990,
  /** 粗利率（0..1）。参照モックの提示値。 */
  grossMarginRate: 0.414,
  /** 消化率（0..1）。 */
  sellThroughRate: 0.521,
  /** 在庫日数（日）。 */
  stockDays: 122,
  /** 店頭在庫数（点）。 */
  stockQty: 27_542,
  /** 在庫金額（円）。 */
  stockValue: 21_069_630,
  /** 状態（滞留 等）。 */
  status: '滞留',
} as const

/** ② 関連（共通・両ペルソナで不変）。参照モック LINKS をそのまま採用。 */
export const ONTOLOGY_LINKS: readonly OntologyLink[] = [
  { label: 'ブランド', to: 'AKB靴楽', info: 'ブランド「AKB靴楽」。同ブランド内での位置づけで販促優先度が決まる' },
  { label: '同シリーズ', to: 'HE8783・HE8784', info: '同シリーズ2品番。後継があるため終売判断の余地がある' },
  { label: 'サプライヤー', to: '某製造', info: 'MOQ 500・LT 6週・契約に最低取引残あり（＝③の制約の出所）' },
  { label: '販売店舗', to: '店A / 店B', info: '店A 消化率71% / 店B 消化率38%（＝店舗間移動の根拠）' },
  { label: '代替SKU', to: 'WE8692', info: 'WE8692 は急上昇・好調。棚圧が無く配分変更の逃がし先になる' },
  { label: '上位カテゴリ', to: '通季', info: '通季カテゴリ。陳腐化が緩やかで「静観」も一応成り立つ' },
]

/** ③＋選択肢：ペルソナ別（小売／メーカー）。参照モック DATA をそのまま採用。 */
export const DECISION_DATA: Record<DecisionPersona, PersonaDecision> = {
  retail: {
    objective: '消化率 × 欠品回避',
    issue: '論点: HE8800（滞留・在庫 ¥21.1M）をどうする',
    actions: [
      { name: '追加発注', status: 'ng', slot: null, why: 'MOQ500・LT6週。滞留品に追加は非合理＋発注枠外' },
      { name: '値下げ30%', status: 'ok', slot: 'A', why: '予算内・棚割影響なし' },
      { name: '店舗間移動', status: 'ok', slot: 'B', why: '物流枠あり（店B→店A）' },
      { name: '終売', status: 'warn', slot: 'C', why: 'サプライヤー契約に最低取引残あり' },
    ],
    options: [
      {
        slot: 'A',
        recommended: true,
        title: '値下げ30%で消化',
        prediction: ['粗利 -¥1.1M', '在庫金額 -¥8.6M', '消化率 52 → 78%'],
        basis: [
          { text: '根拠: ' },
          { text: '消化率×在庫日数ポジショニング 右上', strong: true },
          { text: '・価格弾力性1.0' },
        ],
      },
      {
        slot: 'B',
        recommended: false,
        title: '店舗間移動',
        prediction: ['粗利 ±0', '消化の店舗偏り解消'],
        basis: [
          { text: '根拠: ' },
          { text: '店舗別消化率差', strong: true },
          { text: '（店A71% / 店B38%）' },
        ],
      },
      {
        slot: 'C',
        recommended: false,
        title: '静観（次期繰越）',
        prediction: ['在庫金額 維持', '陳腐化リスク ↑'],
        basis: [
          { text: '根拠: ' },
          { text: '通季品', strong: true },
          { text: 'で陳腐化は緩やか' },
        ],
      },
    ],
    whyRecommend: [
      { text: 'AI推奨: ' },
      { text: 'A 値下げ30%', strong: true },
      { text: ' — 粗利犠牲（-¥1.1M）より滞留 ¥21.1M の解消による回転改善が上回る。値下げは予算内で即実行可。' },
    ],
  },
  maker: {
    objective: '粗利 × ブランド維持',
    issue: '論点: HE8800（滞留・在庫 ¥21.1M）をどうする',
    actions: [
      { name: '増産', status: 'ng', slot: null, why: '需要下降局面。生産キャパを好調SKUへ' },
      { name: '販促投下', status: 'warn', slot: 'C', why: '販促予算残少・費用対効果は限定的' },
      { name: 'チャネル配分変更', status: 'ok', slot: 'A', why: 'ECへ在庫寄せ可（自社Shopify枠あり）' },
      { name: '終売判断(消化計画付)', status: 'ok', slot: 'B', why: '在庫消化計画を付ければ実行可' },
    ],
    options: [
      {
        slot: 'A',
        recommended: true,
        title: 'チャネル配分変更（EC寄せ）',
        prediction: ['粗利 -¥0.3M', '消化 +（EC回転）'],
        basis: [
          { text: '根拠: ' },
          { text: '自社EC枠', strong: true },
          { text: '・代替SKU好調で棚圧なし' },
        ],
      },
      {
        slot: 'B',
        recommended: false,
        title: '終売判断（消化計画付）',
        prediction: ['在庫圧 解消', 'ブランド影響 小'],
        basis: [
          { text: '根拠: ' },
          { text: '通季・シリーズ内に後継', strong: true },
          { text: 'あり（HE8783/8784）' },
        ],
      },
      {
        slot: 'C',
        recommended: false,
        title: '販促投下',
        prediction: ['粗利 -¥0.6M', '効果 不確実'],
        basis: [
          { text: '根拠: ' },
          { text: '販促予算残', strong: true },
          { text: '・弾力性' },
        ],
      },
    ],
    whyRecommend: [
      { text: 'AI推奨: ' },
      { text: 'A チャネル配分変更', strong: true },
      { text: ' — 増産（✗）・販促（△）より、好調ECチャネルへの配分変更が粗利毀損最小で回転を作れる。' },
    ],
  },
}

/**
 * アクション判定の表示メタ（記号・強調色クラス）。参照モックの ○/△/✗ を踏襲する。
 * 文字色は -700 系にして -50 背景とのコントラストを WCAG AA（4.5:1）以上に確保する
 * （12px 太字は「大きな文字」に該当せず 4.5:1 が必要。参照モックも警告色を手動で濃く調整している）。
 */
export const ACTION_STATUS_META: Record<ActionStatus, { symbol: string; pillClass: string }> = {
  ok: { symbol: '○', pillClass: 'bg-emerald-50 text-emerald-700' },
  warn: { symbol: '△', pillClass: 'bg-amber-50 text-amber-700' },
  ng: { symbol: '✗', pillClass: 'bg-rose-50 text-rose-700' },
}

/** アプリ共通のアカウント種別を意思決定ペルソナへ写像する（buyer=小売／supplier=メーカー）。 */
export function personaForAccountType(accountType: AccountType): DecisionPersona {
  return accountType === 'buyer' ? 'retail' : 'maker'
}
