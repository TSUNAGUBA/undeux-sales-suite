import type { FilterOptions, SalesFilterState } from '~/types/api'

function emptyFilter(): SalesFilterState {
  return {
    year: new Date().getFullYear(),
    departments: [],
    businessTypes: [],
    seasons: [],
    hinbans: [],
  }
}

/**
 * 売上分析の共通フィルタ状態と選択肢を提供するコンポーザブル。
 *
 * @param scopeKey フィルタ状態を分離するキー。同じキーを指定したページ間で状態が共有される。
 *                 既定 'sales-filter' は /sales /products /inventory /crosstab で共有される
 *                 全社共通フィルタ。商品軸分析など別軸の画面では独自のキーを渡して分離する。
 */
export function useFilters(scopeKey: string = 'sales-filter') {
  const filter = useState<SalesFilterState>(scopeKey, emptyFilter)
  const options = useState<FilterOptions | null>('filter-options', () => null)
  const optionsError = useState<string | null>('filter-options-error', () => null)

  /** 取込日（週）から西暦年の昇順リストを導出する。 */
  const years = computed<number[]>(() => {
    const weeks = options.value?.weeks ?? []
    const set = new Set<number>()
    for (const week of weeks) {
      const year = Number.parseInt(week.slice(0, 4), 10)
      if (Number.isFinite(year)) {
        set.add(year)
      }
    }
    return [...set].sort((a, b) => a - b)
  })

  // 規定年（今年度）が利用可能なデータに含まれない場合、最新の利用可能年に切替えるガード。
  // 取込済みデータが過去年のみのデモ/テスト環境でも、初期表示で空にならないようにする。
  function applyDefaultYearGuard(): void {
    if (
      filter.value.year !== null
      && years.value.length > 0
      && !years.value.includes(filter.value.year)
    ) {
      const latest = years.value[years.value.length - 1]
      filter.value.year = latest ?? null
    }
  }

  /** フィルタ選択肢を取得する（取得済みなら再取得しない）。完了後に年度ガードを適用する。 */
  async function loadOptions(): Promise<void> {
    if (options.value) {
      applyDefaultYearGuard()
      return
    }
    try {
      options.value = await useApi().get<FilterOptions>('/api/filters')
      optionsError.value = null
      applyDefaultYearGuard()
    } catch (error) {
      optionsError.value = apiErrorMessage(error)
    }
  }

  /** 現在のフィルタをAPIクエリパラメータへ変換する（年 → from/to に展開、空項目は除外）。 */
  function toQuery(): Record<string, unknown> {
    const query: Record<string, unknown> = {}
    const current = filter.value
    if (current.year !== null) {
      query.from = `${current.year}-01-01`
      query.to = `${current.year}-12-31`
    }
    if (current.departments.length > 0) {
      query.departments = current.departments
    }
    if (current.businessTypes.length > 0) {
      query.businessTypes = current.businessTypes
    }
    if (current.seasons.length > 0) {
      query.seasons = current.seasons
    }
    if (current.hinbans.length > 0) {
      query.hinbans = current.hinbans
    }
    return query
  }

  /** フィルタを初期状態へ戻す。規定年は今年度（必要に応じ最新年に補正）。 */
  function reset(): void {
    filter.value = emptyFilter()
    applyDefaultYearGuard()
  }

  /** 配列フィルタへ重複なく値を追加する（ドリルダウン用）。 */
  function addToFilter(field: 'departments' | 'businessTypes' | 'seasons' | 'hinbans', value: string): void {
    const current = filter.value[field]
    if (!current.includes(value)) {
      filter.value[field] = [...current, value]
    }
  }

  return { filter, options, optionsError, loadOptions, toQuery, reset, years, addToFilter }
}
