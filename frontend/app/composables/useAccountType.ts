import type { AccountType } from '~/types/api'

/** デモ用にアカウント種別の手動切替を保存する localStorage キー。 */
const STORAGE_KEY = 'undeux.accountType'

/** アカウント種別の表示メタ（ラベル・短縮ラベル）。出し分け UI で共有する。 */
export const ACCOUNT_TYPE_META: Record<AccountType, { label: string; short: string }> = {
  supplier: { label: 'サプライヤー（メーカー）', short: 'メーカー' },
  buyer: { label: 'バイヤー（小売）', short: '小売' },
}

/** 文字列が AccountType か判定する（クレーム・localStorage の検証に使う）。 */
export function isAccountType(value: unknown): value is AccountType {
  return value === 'supplier' || value === 'buyer'
}

/**
 * アカウント種別（サプライヤー/バイヤー）の共有状態を提供するコンポーザブル。
 *
 * 状態は {@link useState} で全ページ間に共有する。SoT は Firebase カスタムクレーム
 * `accountType`（`plugins/firebase.client.ts` が解決して state に反映）。
 *
 * 本プロダクトはモック段階でユーザー管理 UI が無いため、デモ用に手動切替（{@link setAccountType}）を
 * 許容し、選択は localStorage に保持する（クレームより優先＝デモで両体験を確認できる）。
 * 実運用ではクレームを SoT とし、デモ切替を無効化する想定。
 */
export function useAccountType() {
  const accountType = useState<AccountType>('account-type', () => 'supplier')

  /**
   * 管理者（オーナー）かどうか。SoT は Firebase カスタムクレーム `role`（'admin' のみ true）。
   * アカウント種別と異なり権限に直結するため、デモ用の localStorage 上書きは提供しない
   * （表示だけ切り替えてもサーバ側 403 になるため。強制はサーバ側認可が SoT）。
   */
  const isOwner = useState<boolean>('account-owner', () => false)

  const isBuyer = computed(() => accountType.value === 'buyer')
  const isSupplier = computed(() => accountType.value === 'supplier')
  const meta = computed(() => ACCOUNT_TYPE_META[accountType.value])

  /** デモ用の手動上書き値（localStorage）。未設定なら null。クライアントのみ。 */
  function readOverride(): AccountType | null {
    if (import.meta.server) return null
    try {
      const raw = window.localStorage.getItem(STORAGE_KEY)
      return isAccountType(raw) ? raw : null
    } catch {
      return null
    }
  }

  /**
   * アカウント種別を手動で切り替える（デモ用）。状態と localStorage を更新する。
   * 実運用ではクレームが SoT のため本操作は提供しない想定。
   */
  function setAccountType(value: AccountType): void {
    accountType.value = value
    if (import.meta.server) return
    try {
      window.localStorage.setItem(STORAGE_KEY, value)
    } catch {
      // localStorage 不可（プライベートモード等）でも状態切替は機能させる（原則4: 非ブロッキング）。
    }
  }

  /**
   * クレーム由来の種別を反映する。ただしデモ上書き（localStorage）があればそちらを優先する。
   * `plugins/firebase.client.ts` から認証状態確定時に呼ぶ。
   */
  function applyFromClaim(claim: unknown): void {
    const override = readOverride()
    if (override) {
      accountType.value = override
      return
    }
    accountType.value = isAccountType(claim) ? claim : 'supplier'
  }

  /**
   * 管理者クレーム（`role`）を反映する。`plugins/firebase.client.ts` から認証状態確定時に呼ぶ。
   * 'admin' 以外（未設定・不正値・未認証）は常に false。
   */
  function applyOwnerFromClaim(claim: unknown): void {
    isOwner.value = claim === 'admin'
  }

  return {
    accountType,
    isBuyer,
    isSupplier,
    isOwner,
    meta,
    setAccountType,
    applyFromClaim,
    applyOwnerFromClaim,
  }
}
