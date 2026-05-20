import type { FilterOptions, SalesFilterState } from '~/types/api'

function emptyFilter(): SalesFilterState {
  return {
    year: null,
    departments: [],
    customers: [],
    businessTypes: [],
    seasons: [],
    hinbans: [],
  }
}

/** 売上分析の共通フィルタ状態と選択肢を提供するコンポーザブル。 */
export function useFilters() {
  const filter = useState<SalesFilterState>('sales-filter', emptyFilter)
  const options = useState<FilterOptions | null>('filter-options', () => null)
  const optionsError = useState<string | null>('filter-options-error', () => null)

  /** フィルタ選択肢を取得する（取得済みなら再取得しない）。 */
  async function loadOptions(): Promise<void> {
    if (options.value) {
      return
    }
    try {
      options.value = await useApi().get<FilterOptions>('/api/filters')
      optionsError.value = null
    } catch (error) {
      optionsError.value = apiErrorMessage(error)
    }
  }

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
    if (current.customers.length > 0) {
      query.customers = current.customers
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

  /** フィルタを初期状態へ戻す。 */
  function reset(): void {
    filter.value = emptyFilter()
  }

  /** 配列フィルタへ重複なく値を追加する（ドリルダウン用）。 */
  function addToFilter(field: 'departments' | 'customers' | 'businessTypes' | 'seasons' | 'hinbans', value: string): void {
    const current = filter.value[field]
    if (!current.includes(value)) {
      filter.value[field] = [...current, value]
    }
  }

  return { filter, options, optionsError, loadOptions, toQuery, reset, years, addToFilter }
}
