import type { FilterOptions, SalesFilterState } from '~/types/api'

function emptyFilter(): SalesFilterState {
  return {
    from: null,
    to: null,
    departments: [],
    customers: [],
    businessTypes: [],
    seasons: [],
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

  /** 現在のフィルタをAPIクエリパラメータへ変換する（空項目は除外）。 */
  function toQuery(): Record<string, unknown> {
    const query: Record<string, unknown> = {}
    const current = filter.value
    if (current.from) {
      query.from = current.from
    }
    if (current.to) {
      query.to = current.to
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
    return query
  }

  /** フィルタを初期状態へ戻す。 */
  function reset(): void {
    filter.value = emptyFilter()
  }

  return { filter, options, optionsError, loadOptions, toQuery, reset }
}
